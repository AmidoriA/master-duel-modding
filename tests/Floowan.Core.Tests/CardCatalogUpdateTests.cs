using System.IO.Compression;
using System.Text;
using Floowan.Core.Data;
using Floowan.Core.Imaging;
using Microsoft.Data.Sqlite;

namespace Floowan.Core.Tests;

public class CardCatalogUpdateTests
{
    private static string TempPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + ".db");

    private static void TryDelete(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static byte[] ZlibCompress(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(payload);
        return output.ToArray();
    }

    private static byte[] Encrypt(byte[] plain, int key)
    {
        var zlibbed = ZlibCompress(plain);
        CardDataCrypto.XorInPlace(zlibbed, key);
        return zlibbed;
    }

    [Fact]
    public void CardDataCrypto_RoundTrips_AndFindsKey()
    {
        const int key = 0x25F;
        var plain = Encoding.UTF8.GetBytes("hello-master-duel-card-data");
        var encrypted = Encrypt(plain, key);

        Assert.Equal(plain, CardDataCrypto.Decrypt(encrypted, key));
        Assert.Equal(key, CardDataCrypto.FindCryptoKey(encrypted, preferredKey: key, minPlainLength: plain.Length));
    }

    [Fact]
    public void CardDataFilesParser_ParseIdsAndIndexedStrings()
    {
        // Indx: first 8 bytes header-ish, then offsets for names at start=0: 0, 5, 10
        // Desc offsets at start=4 within each 8-byte record: we'll build records as
        // [nameOff:4][descOff:4] repeating. ETL uses step 8 from indexStart.
        // Record0 (dropped): nameOff=0, descOff=0
        // Record1: nameOff=0, descOff=0
        // Record2: nameOff=5, descOff=4
        // Record3: nameOff=10, descOff=8
        var indx = new byte[]
        {
            0, 0, 0, 0, 0, 0, 0, 0, // dropped by parser after collect
            0, 0, 0, 0, 0, 0, 0, 0,
            5, 0, 0, 0, 4, 0, 0, 0,
            10, 0, 0, 0, 8, 0, 0, 0
        };
        var namesPayload = Encoding.UTF8.GetBytes("AlphaBeta\0\0"); // offsets 0->5 "Alpha", 5->10 "Beta\0"
        // Fix: Alpha is 5 chars, Beta is 4 + pad
        namesPayload = Encoding.UTF8.GetBytes("AlphaBetaX");
        var descPayload = Encoding.UTF8.GetBytes("D1\0\0D2\0\0XX");
        // desc offsets 0,4,8 → "D1\0\0", "D2\0\0"

        var names = CardDataFilesParser.SplitIndexedStrings(indx, namesPayload, indexStart: 0);
        Assert.Equal(2, names.Count);
        Assert.Equal("Alpha", names[0]);
        Assert.Equal("BetaX", names[1]);

        var descs = CardDataFilesParser.SplitIndexedStrings(indx, descPayload, indexStart: 4);
        Assert.Equal(2, descs.Count);
        Assert.Equal("D1", descs[0]);
        Assert.Equal("D2", descs[1]);

        // Prop: skip first 8 bytes, then id little-endian every 8 bytes.
        var prop = new byte[8 + 16];
        prop[8] = 0x39; // 12345 = 0x3039
        prop[9] = 0x30;
        prop[16] = 0x02;
        prop[17] = 0x00;
        var ids = CardDataFilesParser.ParseCardIds(prop);
        Assert.Equal(new[] { 0x3039, 2 }, ids.ToArray());
    }

    [Fact]
    public void CardDataFilesParser_BuildIdentityMap_Skips30000Band_AndAddAltSuffixes()
    {
        var ids = new[] { 1, 30050, 2 };
        var names = new[] { "Same", "Skip", "Same" };
        var descs = new[] { "[Dragon/Effect]", "x", "[Spell]" };
        var map = CardDataFilesParser.BuildIdentityMap(ids, names, descs);
        Assert.False(map.ContainsKey(30050));
        Assert.Equal("Same", map[1].Name);
        Assert.Equal(0, map[1].DataIndex);
        Assert.Equal(2, map[2].DataIndex);

        var suffixed = CardDataFilesParser.AddAltSuffixes(new[] { "A", "B", "A", "A" });
        // ETL processes last→first so the final duplicate keeps the bare name.
        Assert.Equal(new[] { "A (alt 2)", "B", "A (alt 1)", "A" }, suffixed.ToArray());
    }

    [Theory]
    [InlineData("[Cyberse/Link/Effect]", "Link")]
    [InlineData("[Dragon/Synchro/Effect]", "Synchro")]
    [InlineData("[Fiend/Effect]", "Effect")]
    [InlineData("[Fish/Normal]", "Normal")]
    [InlineData("[Trap]", "Trap")]
    [InlineData("Spell Card", "Spell")]
    [InlineData("[Dragon/Pendulum/Effect]", "Effect Pendulum")]
    [InlineData("[Spellcaster/Ritual/Pendulum/Effect]", "Ritual Pendulum")]
    [InlineData("[Warrior/Token]", "Token")]
    public void CardTypeLabels_InferFromCardText(string description, string expected)
    {
        Assert.Equal(expected, CardTypeLabels.InferFromCardText("Card", description));
        Assert.Equal(expected, CardTypeLabels.ToLabel(CardFrameTemplates.InferStyle("Card", description)!.Value));
    }

    [Fact]
    public void CardDatabase_AddsCardTypeAndCreatedAt_AndReplaceMasterCatalog()
    {
        var master = TempPath("floowan-catalog-master-");
        var user = TempPath("floowan-catalog-user-");
        try
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = master,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE card (
  name VARCHAR(255) NOT NULL,
  description VARCHAR(255) NOT NULL,
  bundle VARCHAR(8) NOT NULL,
  data_index INTEGER NOT NULL,
  id INTEGER NOT NULL PRIMARY KEY,
  favorite BOOLEAN NOT NULL DEFAULT 0,
  has_backup BOOLEAN NOT NULL DEFAULT 0,
  is_overframe INTEGER NOT NULL DEFAULT 0,
  UNIQUE (bundle)
);
INSERT INTO card (id, name, description, bundle, data_index, favorite, has_backup, is_overframe)
VALUES (1, 'Old', 'old', 'aaaaaaaa', 0, 0, 0, 0);";
                cmd.ExecuteNonQuery();
            }

            using var db = new CardDatabase(master, user);
            Assert.True(db.HasMasterColumn("card_type"));
            Assert.True(db.HasMasterColumn("created_at"));

            var created = DateTime.UtcNow.ToString("o");
            var written = db.ReplaceMasterCatalog(new[]
            {
                new CatalogCardRow
                {
                    Id = 42,
                    Name = "Accesscode Talker",
                    Description = "[Cyberse/Link/Effect] Link-4",
                    Bundle = "abcd1234",
                    DataIndex = 99,
                    CardType = CardTypeLabels.InferFromCardText(null, "[Cyberse/Link/Effect]"),
                    CreatedAt = created
                }
            });
            Assert.Equal(1, written);
            Assert.Equal(1, db.CountCards());

            var card = db.GetById(42);
            Assert.NotNull(card);
            Assert.Equal("Accesscode Talker", card!.Name);
            Assert.Equal("Link", card.CardType);
            Assert.Equal(created, card.CreatedAt);
            Assert.Equal("abcd1234", card.Bundle);
        }
        finally
        {
            TryDelete(master, user);
        }
    }
}

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
            Assert.True(db.HasMasterColumn("link_markers"));

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
                    CreatedAt = created,
                    LinkMarkers = LinkMarkerMask.Up | LinkMarkerMask.Left
                        | LinkMarkerMask.Right | LinkMarkerMask.Down
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
            Assert.Equal(
                LinkMarkerMask.Up | LinkMarkerMask.Left | LinkMarkerMask.Right | LinkMarkerMask.Down,
                card.LinkMarkers);

            var latest = db.GetLatestCreatedAtUtc();
            Assert.NotNull(latest);
            Assert.Equal(DateTimeOffset.Parse(created).ToUniversalTime(), latest!.Value.ToUniversalTime());
        }
        finally
        {
            TryDelete(master, user);
        }
    }

    [Fact]
    public void CardDatabase_UpsertMasterCatalog_PreservesExistingAndUpdatesById()
    {
        var master = TempPath("floowan-catalog-upsert-");
        var user = TempPath("floowan-catalog-upsert-user-");
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
);";
                cmd.ExecuteNonQuery();
            }

            using var db = new CardDatabase(master, user);
            var older = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).ToString("o");
            var newer = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero).ToString("o");

            db.ReplaceMasterCatalog(new[]
            {
                new CatalogCardRow
                {
                    Id = 1,
                    Name = "Keep Me",
                    Description = "[Spell]",
                    Bundle = "keep0001",
                    DataIndex = 0,
                    CardType = "Spell",
                    CreatedAt = older
                }
            });

            var written = db.UpsertMasterCatalog(new[]
            {
                new CatalogCardRow
                {
                    Id = 1,
                    Name = "Keep Me Updated",
                    Description = "[Spell]",
                    Bundle = "keep0001",
                    DataIndex = 0,
                    CardType = "Spell",
                    CreatedAt = newer
                },
                new CatalogCardRow
                {
                    Id = 2,
                    Name = "Brand New",
                    Description = "[Trap]",
                    Bundle = "new00002",
                    DataIndex = 1,
                    CardType = "Trap",
                    CreatedAt = newer
                }
            });
            Assert.Equal(2, written);
            Assert.Equal(2, db.CountCards());

            var updated = db.GetById(1);
            Assert.Equal("Keep Me Updated", updated!.Name);
            Assert.Equal(newer, updated.CreatedAt);

            var added = db.GetById(2);
            Assert.Equal("Brand New", added!.Name);
            Assert.Equal("Trap", added.CardType);

            Assert.Equal(
                DateTimeOffset.Parse(newer).ToUniversalTime(),
                db.GetLatestCreatedAtUtc()!.Value.ToUniversalTime());
        }
        finally
        {
            TryDelete(master, user);
        }
    }

    [Fact]
    public void CardCatalogUpdater_UpdateNewFilesOnly_RequiresExistingCreatedAt()
    {
        var master = TempPath("floowan-catalog-inc-");
        var user = TempPath("floowan-catalog-inc-user-");
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
);";
                cmd.ExecuteNonQuery();
            }

            using var db = new CardDatabase(master, user);
            Assert.Null(db.GetLatestCreatedAtUtc());
            var result = new CardCatalogUpdater().UpdateNewFilesOnly(db, @"C:\does\not\exist");
            Assert.False(result.Success);
            Assert.Contains("Update entire DB", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(master, user);
        }
    }

    [Theory]
    [InlineData(CardPropTypeDecoder.SpellFace, CardPropTypeDecoder.SpellTrapSecondary, "Spell")]
    [InlineData(CardPropTypeDecoder.TrapFace, CardPropTypeDecoder.SpellTrapSecondary, "Trap")]
    [InlineData(0xAB, 0xD0, "Link")]
    [InlineData(0x6B, 0xC0, "Link")]
    // Synchro: SynchroXyzBit + family Normal/Effect/Tuner (not only legacy 0x92 / 0x12).
    [InlineData(0x92, 0x61, "Synchro")] // Stardust Dragon
    [InlineData(0x52, 0x60, "Synchro")] // Stardust Spark Dragon (was Effect)
    [InlineData(0xD2, 0x58, "Synchro")] // Brionac (was Effect)
    [InlineData(0x53, 0x48, "Synchro")] // Formula Synchron tuner (was Effect)
    [InlineData(0x93, 0x54, "Synchro")] // Accel Synchron (was Effect)
    [InlineData(0x51, 0x59, "Synchro")] // Gaia Knight (was Effect)
    [InlineData(0x12, 0x5D, "Synchro")] // Black Rose / Nitro — plain Synchro, not pendulum
    [InlineData(0xA4, 0x68, "Synchro Pendulum")] // Nirvana High Paladin (was Link)
    // Xyz: SynchroXyzBit + family Normal/Effect (includes previously missed 0x17/0xD7).
    [InlineData(0x57, 0x90, "Xyz")]
    [InlineData(0x97, 0x90, "Xyz")]
    [InlineData(0xD7, 0x80, "Xyz")] // Bahamut Shark (was Effect)
    [InlineData(0x17, 0x70, "Xyz")] // Evolzar Laggia (was Effect)
    [InlineData(0x56, 0x90, "Xyz")] // Gem-Knight Pearl (was Effect)
    [InlineData(0xA2, 0x70, "Xyz Pendulum")] // Odd-Eyes Rebellion (was Link)
    [InlineData(CardPropTypeDecoder.FusionFaceA, 0x70, "Fusion")] // Blue-Eyes Ultimate
    [InlineData(CardPropTypeDecoder.FusionFaceB, 0x70, "Fusion")] // Neo Blue-Eyes Ultimate
    [InlineData(CardPropTypeDecoder.FusionFaceC, 0x64, "Fusion")] // Red-Eyes Black Dragon Exceed (was Fusion Pendulum)
    [InlineData(0x83, 0x59, "Fusion")] // Elemental HERO Flame Wingman (was Fusion Pendulum)
    [InlineData(CardPropTypeDecoder.FusionFaceToonUltimate, 0x70, "Fusion")] // Blue-Eyes Toon Ultimate (was Effect Pendulum)
    [InlineData(0x02, 0x55, "Fusion")] // Flame Swordsman (was Effect)
    [InlineData(0xC3, 0x60, "Fusion")] // Dinoster / Masked HERO Vapor family (was Effect)
    [InlineData(0xA9, 0x70, "Fusion Pendulum")] // Supreme King Z-ARC
    [InlineData(0xA9, 0x5C, "Fusion Pendulum")] // Starving Venemy Dragon
    [InlineData(0x69, 0x71, "Fusion Pendulum")] // Arktos XII
    [InlineData(0x85, 0x44, "Ritual")]
    [InlineData(0x45, 0x50, "Ritual")] // Cyber Angel Benten (was Effect)
    [InlineData(0xC5, 0x50, "Ritual")] // Evigishki (was Effect)
    [InlineData(0xA6, 0x60, "Ritual Pendulum")] // Shinobaron (was Link)
    [InlineData(CardPropTypeDecoder.EffectPendulumFace, 0x5C, "Effect Pendulum")]
    [InlineData(CardPropTypeDecoder.NormalPendulumFaceA, 0x55, "Normal Pendulum")]
    [InlineData(CardPropTypeDecoder.SheepTokenFace, 0x45, "Token")]
    [InlineData(CardPropTypeDecoder.KuribohTokenFace, 0x44, "Token")]
    [InlineData(CardPropTypeDecoder.SlimeTokenFace, 0x44, "Token")] // Slime Token (was Effect)
    [InlineData(CardPropTypeDecoder.SlimeTokenFace, 0x48, "Token")] // Lekunga Token (was Effect)
    [InlineData(CardPropTypeDecoder.MirageTokenFace, 0x40, "Token")] // Mirage / Clone Token (was Effect)
    [InlineData(0x0A, 0x45, "Token")] // Bomb Token (was Effect)
    [InlineData(CardPropTypeDecoder.NormalMonsterFace, 0x60, "Normal")]
    [InlineData(0x80, 0x5C, "Effect")]
    [InlineData(0x10, 0x4D, "Effect")]
    [InlineData(0x50, 0x4D, "Effect")] // Effect Veiler — must not become Synchro
    [InlineData(0x9A, 0x5C, "Effect Pendulum")] // Odd-Eyes — must not become Token (shares nibble A)
    [InlineData(0xAA, 0xC0, "Link")] // Traffic Ghost — Link nibble A must not become Token
    public void CardPropTypeDecoder_InfersKnownFaces(byte typeByte, byte typeByte2, string expected)
    {
        Assert.Equal(expected, CardPropTypeDecoder.InferLabel(typeByte, typeByte2));
    }

    [Fact]
    public void CardPropTypeDecoder_SpecialSummonFaces_WinOverEffectFallback()
    {
        // Same secondary byte as many Effect monsters — special masks must still win.
        const byte typeByte2 = 0x5C;
        Assert.Equal("Synchro", CardPropTypeDecoder.InferLabel(0x52, typeByte2));
        Assert.Equal("Xyz", CardPropTypeDecoder.InferLabel(0xD7, typeByte2));
        Assert.Equal("Ritual", CardPropTypeDecoder.InferLabel(0x45, typeByte2));
        Assert.Equal("Fusion", CardPropTypeDecoder.InferLabel(0x83, typeByte2));
        Assert.Equal("Token", CardPropTypeDecoder.InferLabel(0xCA, typeByte2));
        Assert.Equal("Effect", CardPropTypeDecoder.InferLabel(0x80, typeByte2));
    }

    [Fact]
    public void CardPropTypeDecoder_ParseEntries_ReadsIdAndTypeBytes()
    {
        var prop = new byte[8 + 16];
        prop[8] = 0x39; // 12345
        prop[9] = 0x30;
        prop[10] = CardPropTypeDecoder.SpellFace;
        prop[11] = CardPropTypeDecoder.SpellTrapSecondary;
        prop[16] = 0x02;
        prop[17] = 0x00;
        prop[18] = CardPropTypeDecoder.NormalMonsterFace;
        prop[19] = 0x60;
        var entries = CardPropTypeDecoder.ParseEntries(prop);
        Assert.Equal(2, entries.Count);
        Assert.Equal(0x3039, entries[0].Id);
        Assert.Equal("Spell", CardPropTypeDecoder.InferLabel(entries[0].TypeByte, entries[0].TypeByte2));
        Assert.Equal(2, entries[1].Id);
        Assert.Equal("Normal", CardPropTypeDecoder.InferLabel(entries[1].TypeByte, entries[1].TypeByte2));
    }

    [Fact]
    public void CardCatalogExtractor_IllustrationSizeWindow_IncludesLargeIllustBundles()
    {
        // Regression: Fabled Lurrie's LocalData illust (bundle 2caa5d54) is ~4.61 MiB.
        // A 3 MiB upper bound skipped it, so art id 8092 never entered the catalog and
        // search for "Fabled Lurrie" returned nothing after Update entire DB.
        const long fabledLurrieBundleBytes = 4_838_312;
        Assert.True(CardCatalogExtractor.IsIllustrationSizeCandidate(fabledLurrieBundleBytes));
        Assert.True(CardCatalogExtractor.IsIllustrationSizeCandidate(3 * 1024 * 1024));
        Assert.True(CardCatalogExtractor.IsIllustrationSizeCandidate(8 * 1024 * 1024));
        Assert.False(CardCatalogExtractor.IsIllustrationSizeCandidate(8 * 1024 * 1024 + 1));
        Assert.False(CardCatalogExtractor.IsIllustrationSizeCandidate(15 * 1024));
    }

    [Fact]
    public void CardCatalogExtractor_SkipsCorruptOrNonBundleFiles_WithoutThrowing()
    {
        // Minimal install layout so IsValidGamePath passes; junk under 0000 is not a Unity bundle.
        var install = Path.Combine(Path.GetTempPath(), "floowan-catalog-scan-" + Guid.NewGuid().ToString("N"));
        var player = Path.Combine(install, "LocalData", "deadbeef");
        var local0000 = Path.Combine(player, "0000", "aa");
        Directory.CreateDirectory(local0000);
        Directory.CreateDirectory(Path.Combine(install, "masterduel_Data"));
        File.WriteAllBytes(Path.Combine(install, "masterduel_Data", "data.unity3d"), Array.Empty<byte>());
        // Size in card-data + illust windows so the scanner attempts LoadBundleFile.
        File.WriteAllBytes(Path.Combine(local0000, "aabbccdd"), new byte[32 * 1024]);

        try
        {
            var notes = new List<string>();
            var progress = new Progress<string>(s => { lock (notes) notes.Add(s); });
            var result = new CardCatalogExtractor().Extract(player, progress);

            Assert.False(result.Success);
            // Must not surface NullReferenceException from a null assetsInst.
            Assert.DoesNotContain(notes, n => n.Contains("NullReference", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("CARD_", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(install, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void CardCatalogExtractor_Incremental_SkipsOldFiles_WithoutOpening()
    {
        // Old junk in the size window would normally hit LoadBundleFile ("Skipping unreadable…").
        // With a cutoff after the file's creation time, incremental must skip before open.
        var install = Path.Combine(Path.GetTempPath(), "floowan-catalog-inc-skip-" + Guid.NewGuid().ToString("N"));
        var player = Path.Combine(install, "LocalData", "deadbeef");
        var local0000 = Path.Combine(player, "0000", "aa");
        Directory.CreateDirectory(local0000);
        Directory.CreateDirectory(Path.Combine(install, "masterduel_Data"));
        File.WriteAllBytes(Path.Combine(install, "masterduel_Data", "data.unity3d"), Array.Empty<byte>());

        var junkPath = Path.Combine(local0000, "oldbundle");
        File.WriteAllBytes(junkPath, new byte[32 * 1024]);
        var oldStamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(junkPath, oldStamp);
        File.SetLastWriteTimeUtc(junkPath, oldStamp);

        var cutoff = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        try
        {
            var notes = new List<string>();
            var progress = new Progress<string>(s => { lock (notes) notes.Add(s); });
            var result = new CardCatalogExtractor().Extract(
                player, progress, illustCreatedAfterUtc: cutoff);

            Assert.True(result.Success);
            Assert.Empty(result.Rows);
            Assert.Contains(notes, n => n.Contains("not opened", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Skipped", StringComparison.OrdinalIgnoreCase));
            // Prove AssetsTools never opened the junk: no unreadable/unloadable messages.
            Assert.DoesNotContain(notes, n => n.Contains("unreadable", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(notes, n => n.Contains("unloadable", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(notes, n => n.Contains("Skipping unreadable", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(install, recursive: true); } catch { /* ignore */ }
        }
    }
}

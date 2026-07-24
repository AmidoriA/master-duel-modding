using Floowan.Core.Data;
using Floowan.Core.Imaging;
using Floowan.Core.Models;
using Floowan.Core.Services;
using Microsoft.Data.Sqlite;

namespace Floowan.Core.Tests;

public class CardFrameStyleFromTypeTests
{
    [Theory]
    [InlineData("Effect", CardFrameStyle.Effect)]
    [InlineData("Normal", CardFrameStyle.Normal)]
    [InlineData("Fusion", CardFrameStyle.Fusion)]
    [InlineData("Synchro", CardFrameStyle.Synchro)]
    [InlineData("Xyz", CardFrameStyle.Xyz)]
    [InlineData("Ritual", CardFrameStyle.Ritual)]
    [InlineData("Spell", CardFrameStyle.Spell)]
    [InlineData("Trap", CardFrameStyle.Trap)]
    [InlineData("Link", CardFrameStyle.Link)]
    [InlineData("Token", CardFrameStyle.Token)]
    [InlineData("Normal Pendulum", CardFrameStyle.PendulumNormal)]
    [InlineData("Effect Pendulum", CardFrameStyle.PendulumEffect)]
    [InlineData("Fusion Pendulum", CardFrameStyle.PendulumFusion)]
    [InlineData("Synchro Pendulum", CardFrameStyle.PendulumSynchro)]
    [InlineData("Xyz Pendulum", CardFrameStyle.PendulumXyz)]
    [InlineData("Ritual Pendulum", CardFrameStyle.PendulumRitual)]
    [InlineData("Token Pendulum", CardFrameStyle.PendulumToken)]
    [InlineData("link", CardFrameStyle.Link)]
    [InlineData("Pendulum Effect", CardFrameStyle.PendulumEffect)]
    [InlineData("PendulumNormal", CardFrameStyle.PendulumNormal)]
    public void TryParseStyle_MapsKnownLabels(string label, CardFrameStyle expected)
    {
        Assert.True(CardTypeLabels.TryParseStyle(label, out var style));
        Assert.Equal(expected, style);
        Assert.Equal(expected, CardTypeLabels.InferStyle(label, "Ignored", "[Dragon/Effect]"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAType")]
    [InlineData("Monster")]
    public void TryParseStyle_RejectsUnknown(string? label)
    {
        Assert.False(CardTypeLabels.TryParseStyle(label, out _));
    }

    [Fact]
    public void InferStyle_PrefersCardTypeOverDescription()
    {
        // Description says Link; DB card_type says Synchro → Synchro wins.
        Assert.Equal(
            CardFrameStyle.Synchro,
            CardTypeLabels.InferStyle("Synchro", "Accesscode", "[Cyberse/Link/Effect]"));
        Assert.Equal(
            CardFrameStyle.Link,
            CardTypeLabels.InferStyle(null, "Accesscode", "[Cyberse/Link/Effect]"));
    }

    [Fact]
    public void ToLabel_RoundTrips_AllStyles()
    {
        foreach (CardFrameStyle style in Enum.GetValues<CardFrameStyle>())
        {
            var label = CardTypeLabels.ToLabel(style);
            Assert.True(CardTypeLabels.TryParseStyle(label, out var parsed), label);
            Assert.Equal(style, parsed);
        }
    }

    [Fact]
    public void UpdateCardType_WritesLabel_AndResolverPrefersIt()
    {
        var master = TempPath("floowan-frame-type-master-");
        var user = TempPath("floowan-frame-type-user-");
        try
        {
            CreateMinimalMaster(master);
            using var db = new CardDatabase(master, user);
            Assert.True(db.UpdateCardType(1, "Link"));
            var card = db.GetById(1);
            Assert.NotNull(card);
            Assert.Equal("Link", card!.CardType);

            var resolver = new CardFrameStyleResolver();
            var style = resolver.Resolve(card, database: null, playerDataPath: null);
            Assert.Equal(CardFrameStyle.Link, style);
        }
        finally
        {
            TryDelete(master, user);
        }
    }

    [Fact]
    public void Resolver_MissingType_FallsBackToDescription_WithoutGamePath()
    {
        var card = new CardRecord
        {
            Id = 7,
            Name = "Accesscode Talker",
            Description = "[Cyberse/Link/Effect] Link-4",
            Bundle = "abcd1234",
            CardType = null
        };

        var resolver = new CardFrameStyleResolver();
        Assert.Equal(CardFrameStyle.Link, resolver.Resolve(card));
    }

    [Fact]
    public void Resolver_BackfillsCardType_WhenExtractorSuppliesPropMap()
    {
        var master = TempPath("floowan-frame-backfill-master-");
        var user = TempPath("floowan-frame-backfill-user-");
        try
        {
            CreateMinimalMaster(master);
            using var db = new CardDatabase(master, user);

            var card = db.GetById(1)!;
            Assert.True(string.IsNullOrWhiteSpace(card.CardType));

            // Stub loader: pretend CARD_Prop says Synchro for id 1.
            var resolver = new CardFrameStyleResolver(
                (_, _, _) => new Dictionary<int, string?> { [1] = "Synchro" });
            var style = resolver.Resolve(
                card,
                db,
                playerDataPath: @"C:\fake\LocalData\player");

            Assert.Equal(CardFrameStyle.Synchro, style);
            var refreshed = db.GetById(1);
            Assert.Equal("Synchro", refreshed!.CardType);
        }
        finally
        {
            TryDelete(master, user);
        }
    }

    private static void CreateMinimalMaster(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
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
VALUES (1, 'Test Monster', 'A monster with no type line.', 'abcd1234', 0, 0, 0, 0);";
        cmd.ExecuteNonQuery();
    }

    private static string TempPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + ".db");

    private static void TryDelete(params string[] paths)
    {
        foreach (var p in paths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
        }
    }
}

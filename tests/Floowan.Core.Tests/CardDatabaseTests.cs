using Floowan.Core.Data;

namespace Floowan.Core.Tests;

public class CardDatabaseTests
{
    private static string? FindDatabase()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "database.db")),
            Path.Combine(Directory.GetCurrentDirectory(), "database.db"),
            @"C:\Users\user\Documents\Projects\Floowan-copy\database.db"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? CopyDatabaseToTemp()
    {
        var source = FindDatabase();
        if (source is null)
            return null;

        var dest = Path.Combine(Path.GetTempPath(), $"floowan-db-test-{Guid.NewGuid():N}.db");
        File.Copy(source, dest, overwrite: true);
        return dest;
    }

    [Fact]
    public void Search_ReturnsCards_WhenDatabasePresent()
    {
        var dbPath = FindDatabase();
        if (dbPath is null)
        {
            // Allow CI without the large database checked in.
            return;
        }

        using var db = new CardDatabase(dbPath);
        Assert.True(db.CountCards() > 0);
        var hits = db.SearchCards("Dragon", limit: 20);
        Assert.NotEmpty(hits);
        Assert.All(hits, c => Assert.False(string.IsNullOrWhiteSpace(c.Bundle)));
    }

    [Fact]
    public void QueryCards_AppliesCombinedFilters_AndRespectsLimit()
    {
        var dbPath = FindDatabase();
        if (dbPath is null)
            return;

        using var db = new CardDatabase(dbPath);
        var sample = db.QueryCards(new CardQueryFilters { Limit = 1 }).First();

        var filters = new CardQueryFilters
        {
            CardId = sample.Id,
            NameContains = sample.Name.Length >= 3 ? sample.Name[..3] : sample.Name,
            Favorite = sample.Favorite,
            HasBackup = sample.HasBackup,
            HasModdedName = !string.IsNullOrWhiteSpace(sample.ModdedName),
            HasModdedDescription = !string.IsNullOrWhiteSpace(sample.ModdedDescription),
            Limit = 25,
            Offset = 0
        };

        var hits = db.QueryCards(filters);
        Assert.Single(hits);
        Assert.Equal(sample.Id, hits[0].Id);
        Assert.Equal(1, db.CountCards(filters));

        var page = db.QueryCards(new CardQueryFilters { Limit = 50, Offset = 0 });
        Assert.True(page.Count <= 50);
        Assert.True(page.Count < db.CountCards());
    }

    [Fact]
    public void UpdateCard_PersistsEditableFields_AndClearsNullModdedValues()
    {
        var dbPath = CopyDatabaseToTemp();
        if (dbPath is null)
            return;

        try
        {
            using var db = new CardDatabase(dbPath);
            var card = db.QueryCards(new CardQueryFilters { Limit = 1 }).Single();

            db.UpdateCard(
                card.Id,
                name: card.Name + " [edited]",
                description: "Edited description",
                moddedName: "Temp Mod Name",
                moddedDescription: "Temp Mod Desc",
                favorite: !card.Favorite);

            var afterSet = db.GetById(card.Id);
            Assert.NotNull(afterSet);
            Assert.Equal(card.Name + " [edited]", afterSet!.Name);
            Assert.Equal("Edited description", afterSet.Description);
            Assert.Equal("Temp Mod Name", afterSet.ModdedName);
            Assert.Equal("Temp Mod Desc", afterSet.ModdedDescription);
            Assert.Equal(!card.Favorite, afterSet.Favorite);
            Assert.Equal(card.Bundle, afterSet.Bundle);
            Assert.Equal(card.DataIndex, afterSet.DataIndex);
            Assert.Equal(card.HasBackup, afterSet.HasBackup);

            db.UpdateCard(
                card.Id,
                name: card.Name,
                description: card.Description,
                moddedName: null,
                moddedDescription: "   ",
                favorite: card.Favorite);

            var afterClear = db.GetById(card.Id);
            Assert.NotNull(afterClear);
            Assert.Null(afterClear!.ModdedName);
            Assert.Null(afterClear.ModdedDescription);
            Assert.Equal(card.Name, afterClear.Name);
            Assert.Equal(card.Favorite, afterClear.Favorite);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void UpdateCard_RejectsMissingCardId()
    {
        var dbPath = CopyDatabaseToTemp();
        if (dbPath is null)
            return;

        try
        {
            using var db = new CardDatabase(dbPath);
            var missingId = 2_000_000_001;
            Assert.Null(db.GetById(missingId));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                db.UpdateCard(missingId, "Name", "Desc", null, null, false));
            Assert.Contains(missingId.ToString(), ex.Message);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void UpdateCard_RejectsNonPositiveId()
    {
        var dbPath = CopyDatabaseToTemp();
        if (dbPath is null)
            return;

        try
        {
            using var db = new CardDatabase(dbPath);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                db.UpdateCard(0, "Name", "Desc", null, null, false));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }
}

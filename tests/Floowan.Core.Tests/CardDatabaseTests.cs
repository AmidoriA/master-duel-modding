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
}

using Floowan.Core.Data;
using Floowan.Core.Models;

namespace Floowan.Core.Tests;

public class CutInDatabaseTests
{
    [Fact]
    public void CutInDatabase_RoundTripsSeparateFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-cutin-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var db = new CutInDatabase(path);
            Assert.Equal(0, db.Count());

            db.ReplaceAll(
            [
                new CutInRecord
                {
                    CutInId = 14944,
                    Name = "Predaplant Verte Anaconda",
                    IsComplete = true,
                    TextureName = "P14944",
                    TextureBundlePath = @"C:\tmp\a",
                    AtlasName = "P14944",
                    AtlasBundlePath = @"C:\tmp\a",
                    SkeletonName = "P14944JS",
                    SkeletonBundlePath = @"C:\tmp\b",
                    UpdatedAtUtc = "2026-01-01T00:00:00Z"
                }
            ]);

            Assert.Equal(1, db.Count());
            Assert.Equal(1, db.Count(completeOnly: true));
            Assert.True(db.HasCutIn(14944));
            var row = db.Get(14944);
            Assert.NotNull(row);
            Assert.Equal("P14944JS", row!.SkeletonName);
            Assert.Equal(path, db.DatabasePath);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ResolveDefaultPath_PrefersSiblingOfCardDatabase()
    {
        var cardDb = Path.Combine(Path.GetTempPath(), "cards", "database.db");
        var expected = Path.Combine(Path.GetTempPath(), "cards", "cutin.db");
        Assert.Equal(Path.GetFullPath(expected), CutInDatabase.ResolveDefaultPath(cardDb));
    }
}

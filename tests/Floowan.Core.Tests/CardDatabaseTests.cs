using Floowan.Core.Data;
using Microsoft.Data.Sqlite;

namespace Floowan.Core.Tests;

public class CardDatabaseTests
{
    private static string? FindDatabase()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, MasterDatabasePaths.FileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", MasterDatabasePaths.FileName)),
            Path.Combine(Directory.GetCurrentDirectory(), MasterDatabasePaths.FileName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string TempPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + ".db");

    private static string? CopyDatabaseToTemp()
    {
        var source = FindDatabase();
        if (source is null)
            return null;

        var dest = TempPath("floowan-db-test-");
        File.Copy(source, dest, overwrite: true);
        return dest;
    }

    private static void TryDelete(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static void CreateLegacyMonolithicDatabase(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE app_config (
  id INTEGER PRIMARY KEY,
  mipmap_count INTEGER NOT NULL,
  game_path VARCHAR(610) NOT NULL,
  packer VARCHAR(5) NOT NULL,
  create_backup BOOLEAN NOT NULL,
  of_card_asset_bundle VARCHAR(8)
);
INSERT INTO app_config (id, mipmap_count, game_path, packer, create_backup, of_card_asset_bundle)
VALUES (1, 10, 'C:\legacy\game', 'lz4', 0, 'deadbeef');

CREATE TABLE card (
  name VARCHAR(255) NOT NULL,
  description VARCHAR(255) NOT NULL,
  bundle VARCHAR(8) NOT NULL,
  modded_name VARCHAR(255),
  modded_description VARCHAR(255),
  data_index INTEGER NOT NULL,
  id INTEGER NOT NULL PRIMARY KEY,
  favorite BOOLEAN NOT NULL,
  has_backup BOOLEAN NOT NULL,
  is_overframe INTEGER NOT NULL DEFAULT 0,
  overframe_base_id INTEGER,
  art_id INTEGER,
  UNIQUE (bundle)
);
INSERT INTO card (name, description, bundle, modded_name, modded_description, data_index, id, favorite, has_backup, is_overframe, overframe_base_id, art_id)
VALUES ('Alpha', 'Summons a unique token', 'aaa11111', 'Mod Alpha', NULL, 0, 1, 1, 1, 1, 9, 1001),
       ('Beta', 'plain desc', 'bbb22222', NULL, 'Modded unique effect', 0, 2, 0, 0, 0, NULL, NULL),
       ('Gamma', 'unrelated', 'ccc33333', NULL, NULL, 0, 3, 0, 0, 0, NULL, NULL);
";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Open_SeedsUserDb_WhenMissing()
    {
        var master = TempPath("floowan-seed-master-");
        var user = TempPath("floowan-seed-user-");
        try
        {
            CreateLegacyMonolithicDatabase(master);
            Assert.False(File.Exists(user));

            using (var db = new CardDatabase(master, user))
            {
                Assert.True(File.Exists(user));
                Assert.Equal(user, db.UserDatabasePath);
                Assert.Equal(3, db.CountCards());
                // Defaults were overwritten by legacy migration from master app_config.
                Assert.Equal(@"C:\legacy\game", db.GetStoredGamePath());
                Assert.Equal("deadbeef", db.GetOfCardAssetBundleId());
                Assert.False(db.GetCreateBackupFlag());
            }

            // Re-open: schema already present, no exception.
            using (var db2 = new CardDatabase(master, user))
            {
                Assert.Equal(3, db2.CountCards());
                Assert.Equal("deadbeef", db2.GetOfCardAssetBundleId());
            }
        }
        finally
        {
            TryDelete(master, user);
        }
    }

    [Fact]
    public void Open_MigratesLegacyUserColumns_IntoUserDb_AndStopsWritingMasterUserFields()
    {
        var master = TempPath("floowan-mig-master-");
        var user = TempPath("floowan-mig-user-");
        try
        {
            CreateLegacyMonolithicDatabase(master);

            using (var db = new CardDatabase(master, user))
            {
                var alpha = db.GetById(1)!;
                Assert.True(alpha.Favorite);
                Assert.True(alpha.HasBackup);
                Assert.True(alpha.IsOverframe);
                Assert.Equal(9, alpha.OverframeBaseId);
                Assert.Equal(1001, alpha.ArtId);
                Assert.Equal("Mod Alpha", alpha.ModdedName);

                db.SetHasBackup(1, false);
                db.SetOverframe(1, false);
                db.UpdateCard(1, alpha.Name, alpha.Description, moddedName: null, moddedDescription: null, favorite: false);
            }

            // Master legacy user columns should still hold pre-migration values (not stripped / not rewritten).
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = master,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT favorite, has_backup, is_overframe, modded_name FROM card WHERE id = 1;";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(1, Convert.ToInt32(reader.GetValue(0)));
                Assert.Equal(1, Convert.ToInt32(reader.GetValue(1)));
                Assert.Equal(1, Convert.ToInt32(reader.GetValue(2)));
                Assert.Equal("Mod Alpha", reader.GetString(3));
            }

            using var db2 = new CardDatabase(master, user);
            var after = db2.GetById(1)!;
            Assert.False(after.Favorite);
            Assert.False(after.HasBackup);
            Assert.False(after.IsOverframe);
            Assert.Null(after.ModdedName);
        }
        finally
        {
            TryDelete(master, user);
        }
    }

    [Fact]
    public void SearchCards_WithSearchDescription_MatchesDescriptionAndModdedDescription()
    {
        var path = TempPath("floowan-search-desc-");
        var user = TempPath("floowan-search-desc-user-");
        try
        {
            CreateLegacyMonolithicDatabase(path);

            using var db = new CardDatabase(path, user);

            var nameOnly = db.SearchCards("unique", searchDescription: false);
            Assert.Empty(nameOnly);

            var withDesc = db.SearchCards("unique", searchDescription: true);
            Assert.Equal(2, withDesc.Count);
            Assert.Contains(withDesc, c => c.Id == 1);
            Assert.Contains(withDesc, c => c.Id == 2);
        }
        finally
        {
            TryDelete(path, user);
        }
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

        var user = TempPath("floowan-search-user-");
        try
        {
            using var db = new CardDatabase(dbPath, user);
            Assert.True(db.CountCards() > 0);
            var hits = db.SearchCards("Dragon", limit: 20);
            Assert.NotEmpty(hits);
            Assert.All(hits, c => Assert.False(string.IsNullOrWhiteSpace(c.Bundle)));
        }
        finally
        {
            TryDelete(user);
        }
    }

    [Fact]
    public void SearchCards_NumericQuery_MatchesExactCardId()
    {
        var path = TempPath("floowan-search-id-");
        var user = TempPath("floowan-search-id-user-");
        try
        {
            CreateLegacyMonolithicDatabase(path);

            using var db = new CardDatabase(path, user);
            var byId = db.SearchCards("1");
            Assert.Contains(byId, c => c.Id == 1);
            Assert.DoesNotContain(byId, c => c.Id == 2);
        }
        finally
        {
            TryDelete(path, user);
        }
    }

    [Fact]
    public void QueryCards_AppliesCombinedFilters_AndRespectsLimit()
    {
        var dbPath = FindDatabase();
        if (dbPath is null)
            return;

        var user = TempPath("floowan-query-user-");
        try
        {
            using var db = new CardDatabase(dbPath, user);
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
        finally
        {
            TryDelete(user);
        }
    }

    [Fact]
    public void QueryCards_SortsFullFilteredSet_BeforePaging()
    {
        var dbPath = FindDatabase();
        if (dbPath is null)
            return;

        var user = TempPath("floowan-sort-user-");
        try
        {
            using var db = new CardDatabase(dbPath, user);
            const int pageSize = 50;
            var total = db.CountCards();
            Assert.True(total > pageSize * 2, "Need enough cards to span multiple pages.");

            var page0Asc = db.QueryCards(new CardQueryFilters
            {
                Limit = pageSize,
                Offset = 0,
                SortBy = CardSortColumn.Id,
                SortDescending = false
            });
            var page1Asc = db.QueryCards(new CardQueryFilters
            {
                Limit = pageSize,
                Offset = pageSize,
                SortBy = CardSortColumn.Id,
                SortDescending = false
            });

            Assert.Equal(pageSize, page0Asc.Count);
            Assert.Equal(pageSize, page1Asc.Count);
            Assert.True(page0Asc[^1].Id < page1Asc[0].Id);
            Assert.True(page0Asc.Zip(page0Asc.Skip(1), (a, b) => a.Id <= b.Id).All(x => x));

            var page0Desc = db.QueryCards(new CardQueryFilters
            {
                Limit = pageSize,
                Offset = 0,
                SortBy = CardSortColumn.Id,
                SortDescending = true
            });
            Assert.Equal(pageSize, page0Desc.Count);
            Assert.True(page0Desc[0].Id > page0Asc[0].Id);
            Assert.True(page0Desc.Zip(page0Desc.Skip(1), (a, b) => a.Id >= b.Id).All(x => x));

            var namePage0 = db.QueryCards(new CardQueryFilters
            {
                Limit = pageSize,
                Offset = 0,
                SortBy = CardSortColumn.Name,
                SortDescending = false
            });
            var namePage1 = db.QueryCards(new CardQueryFilters
            {
                Limit = pageSize,
                Offset = pageSize,
                SortBy = CardSortColumn.Name,
                SortDescending = false
            });

            Assert.Equal(pageSize, namePage0.Count);
            Assert.Equal(pageSize, namePage1.Count);
            var bridge = string.Compare(namePage0[^1].Name, namePage1[0].Name, StringComparison.OrdinalIgnoreCase);
            Assert.True(bridge <= 0);
            Assert.True(namePage0
                .Zip(namePage0.Skip(1), (a, b) =>
                    string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) <= 0)
                .All(x => x));
        }
        finally
        {
            TryDelete(user);
        }
    }

    [Fact]
    public void UpdateCard_PersistsEditableFields_AndClearsNullModdedValues()
    {
        var dbPath = CopyDatabaseToTemp();
        if (dbPath is null)
            return;

        var user = TempPath("floowan-update-user-");
        try
        {
            using var db = new CardDatabase(dbPath, user);
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
            TryDelete(dbPath, user);
        }
    }

    [Fact]
    public void UpdateCard_RejectsMissingCardId()
    {
        var dbPath = CopyDatabaseToTemp();
        if (dbPath is null)
            return;

        var user = TempPath("floowan-missing-user-");
        try
        {
            using var db = new CardDatabase(dbPath, user);
            var missingId = 2_000_000_001;
            Assert.Null(db.GetById(missingId));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                db.UpdateCard(missingId, "Name", "Desc", null, null, false));
            Assert.Contains(missingId.ToString(), ex.Message);
        }
        finally
        {
            TryDelete(dbPath, user);
        }
    }

    [Fact]
    public void UpdateCard_RejectsNonPositiveId()
    {
        var dbPath = CopyDatabaseToTemp();
        if (dbPath is null)
            return;

        var user = TempPath("floowan-nonpos-user-");
        try
        {
            using var db = new CardDatabase(dbPath, user);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                db.UpdateCard(0, "Name", "Desc", null, null, false));
        }
        finally
        {
            TryDelete(dbPath, user);
        }
    }
}

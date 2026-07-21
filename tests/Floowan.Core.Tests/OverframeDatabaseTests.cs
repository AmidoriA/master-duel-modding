using Floowan.Core.Data;
using Microsoft.Data.Sqlite;

namespace Floowan.Core.Tests;

public class OverframeDatabaseTests
{
    [Fact]
    public void Ctor_Migrates_OverframeColumns_And_SetOverframe()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-of-db-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            CreateMinimalDatabase(path);
            using (var db = new CardDatabase(path))
            {
                Assert.Null(db.GetById(1)!.OverframeBaseId);
                Assert.False(db.GetById(1)!.IsOverframe);

                db.SetOverframe(1, true, overframeBaseId: 1);
                var card = db.GetById(1)!;
                Assert.True(card.IsOverframe);
                Assert.Equal(1, card.OverframeBaseId);

                db.SetOfCardAssetBundleId("abcd1234");
                Assert.Equal("abcd1234", db.GetOfCardAssetBundleId());

                var only = db.SearchCards(null, overframeOnly: true);
                Assert.Single(only);
                Assert.Equal(1, only[0].Id);

                db.SetOverframe(2, true, 2);
                var synced = db.SyncOverframeFromGate([(1, 9)]);
                Assert.Equal(1, synced);
                Assert.True(db.GetById(1)!.IsOverframe);
                Assert.Equal(9, db.GetById(1)!.OverframeBaseId);
                Assert.False(db.GetById(2)!.IsOverframe);
            }

            // Idempotent migration on reopen
            using (var db2 = new CardDatabase(path))
            {
                Assert.True(db2.GetById(1)!.IsOverframe);
                Assert.Equal("abcd1234", db2.GetOfCardAssetBundleId());
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static void CreateMinimalDatabase(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE app_config (
  id INTEGER PRIMARY KEY,
  mipmap_count INTEGER NOT NULL,
  game_path VARCHAR(610) NOT NULL,
  packer VARCHAR(5) NOT NULL,
  create_backup BOOLEAN NOT NULL
);
INSERT INTO app_config (id, mipmap_count, game_path, packer, create_backup)
VALUES (1, 1, 'C:\game', 'lz4', 1);

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
  UNIQUE (bundle)
);
INSERT INTO card (name, description, bundle, data_index, id, favorite, has_backup)
VALUES ('Alpha', 'desc', 'aaa11111', 0, 1, 0, 0),
       ('Beta', 'desc', 'bbb22222', 0, 2, 0, 0);
";
            cmd.ExecuteNonQuery();
        }
    }
}

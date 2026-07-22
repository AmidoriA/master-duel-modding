using Floowan.Core.Assets;
using Floowan.Core.Data;
using Floowan.Core.Services;
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

                db.SetArtId(1, 1001);
                db.SetArtId(2, 1002);
                Assert.Equal(1001, db.GetById(1)!.ArtId);
                Assert.Equal(1, db.GetByArtId(1001)!.Id);
                Assert.Equal(1, db.GetByBundle("aaa11111")!.Id);

                db.SetOverframe(2, true, 2);
                var synced = db.SyncOverframeFromGate([(1001, 9)]);
                Assert.Equal(1, synced);
                Assert.True(db.GetById(1)!.IsOverframe);
                Assert.Equal(9, db.GetById(1)!.OverframeBaseId);
                Assert.False(db.GetById(2)!.IsOverframe);

                // Floowandereeze PK must NOT be treated as a gate trigger anymore.
                db.SetOverframe(2, true, 2);
                synced = db.SyncOverframeFromGate([(1, 9)]);
                Assert.Equal(0, synced);
                Assert.False(db.GetById(1)!.IsOverframe);
                Assert.False(db.GetById(2)!.IsOverframe);

                synced = db.SyncOverframeFromGate([(1001, 9)]);
                Assert.Equal(1, synced);
                Assert.True(db.GetById(1)!.IsOverframe);
            }

            // Idempotent migration on reopen
            using (var db2 = new CardDatabase(path))
            {
                Assert.True(db2.GetById(1)!.IsOverframe);
                Assert.Equal(1001, db2.GetById(1)!.ArtId);
                Assert.Equal("abcd1234", db2.GetOfCardAssetBundleId());
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryRemoveLegacyPkGateEntry_DoesNotDeleteOtherCardsArtIdTrigger()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-of-pk-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            CreateMinimalDatabase(path);
            using var db = new CardDatabase(path);
            // Card 1's real MD art id equals card 2's Floowandereeze PK (common collision).
            db.SetArtId(1, 2);

            var gate = OfCardAssetGate.FromEntries([(2, 2), (99, 99)]);
            // Applying / cleaning card 2 must NOT remove card 1's art-id trigger.
            Assert.False(OverFrameModService.TryRemoveLegacyPkGateEntry(gate, cardPk: 2, thisCardArtId: 99, db));
            Assert.True(gate.Contains(2));
            Assert.True(gate.Contains(99));

            // True legacy junk: PK entry with no art_id owner.
            gate.Add(3, 3);
            Assert.True(OverFrameModService.TryRemoveLegacyPkGateEntry(gate, cardPk: 3, thisCardArtId: 50, db));
            Assert.False(gate.Contains(3));
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

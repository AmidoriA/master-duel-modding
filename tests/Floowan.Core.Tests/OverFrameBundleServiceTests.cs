using System.IO.Compression;
using System.Text.Json;
using Floowan.Core.Backup;
using Floowan.Core.Services;

namespace Floowan.Core.Tests;

public class OverFrameBundleServiceTests
{
    [Fact]
    public void Manifest_RoundTrips_Json()
    {
        var manifest = new OverFrameBundleManifest
        {
            FormatVersion = OverFrameBundleManifest.CurrentFormatVersion,
            ExportedAtUtc = "2026-08-07T00:00:00Z",
            ExporterVersion = "0.1.5",
            Cards =
            {
                new OverFrameBundleCardEntry
                {
                    CardId = 42,
                    Name = "Test Card",
                    Bundle = "abcdef12",
                    ArtId = 100,
                    OverframeBaseId = 100,
                    AppliedPng = "cards/42/applied.png",
                    HasEditLayer = true,
                    EditLayer = OverFrameBundleLayerEntry.FromStage(
                        new CustomOverframeStageState
                        {
                            FrameStyle = "Effect",
                            SubjectScale = 1.25f,
                            SubjectOffsetX = 10,
                            SubjectOffsetY = -4
                        },
                        "cards/42/subject.png",
                        "cards/42/subject-mask.png",
                        null)
                }
            }
        };

        var json = JsonSerializer.Serialize(manifest, OverFrameBundleJson.Options);
        var round = JsonSerializer.Deserialize<OverFrameBundleManifest>(json, OverFrameBundleJson.Options);
        Assert.NotNull(round);
        Assert.Equal(1, round!.FormatVersion);
        Assert.Single(round.Cards);
        Assert.Equal(42, round.Cards[0].CardId);
        Assert.Equal("cards/42/applied.png", round.Cards[0].AppliedPng);
        Assert.True(round.Cards[0].HasEditLayer);
        Assert.NotNull(round.Cards[0].EditLayer);
        Assert.Equal(1.25f, round.Cards[0].EditLayer!.SubjectScale);
        Assert.Equal(10, round.Cards[0].EditLayer.SubjectOffsetX);
    }

    [Fact]
    public void ReadManifest_LoadsFromZip()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "floowan-of-bundle-test-" + Guid.NewGuid().ToString("N") + ".overframes");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(OverFrameBundleService.ManifestFileName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("""
                    {
                      "formatVersion": 1,
                      "exportedAtUtc": "2026-08-07T12:00:00Z",
                      "cards": [
                        { "cardId": 7, "name": "Alpha", "appliedPng": "cards/7/applied.png", "hasEditLayer": false }
                      ]
                    }
                    """);
            }

            using var service = new OverFrameBundleService();
            var manifest = service.ReadManifest(zipPath);
            Assert.Equal(1, manifest.FormatVersion);
            Assert.Single(manifest.Cards);
            Assert.Equal(7, manifest.Cards[0].CardId);
            Assert.Equal("Alpha", manifest.Cards[0].Name);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ReadManifest_RejectsMissingFile()
    {
        using var service = new OverFrameBundleService();
        Assert.Throws<FileNotFoundException>(() =>
            service.ReadManifest(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".overframes")));
    }

    [Fact]
    public void LayerEntry_ToStageState_PreservesTransforms()
    {
        var layer = new OverFrameBundleLayerEntry
        {
            FrameStyle = "OfGradientEffect",
            SubjectScale = 2f,
            SubjectOffsetX = 3,
            SubjectOffsetY = 4,
            BackgroundScale = 0.8f,
            BackgroundOffsetX = -1,
            BackgroundOffsetY = 2,
            BackgroundIsCardArt = true,
            SubjectIsFromCardArt = false,
            SubjectPng = "cards/1/subject.png",
            SubjectMaskPng = "cards/1/subject-mask.png",
            BackgroundPng = "cards/1/background.png"
        };

        var state = layer.ToStageState();
        Assert.Equal("OfGradientEffect", state.FrameStyle);
        Assert.Equal(2f, state.SubjectScale);
        Assert.Equal(3, state.SubjectOffsetX);
        Assert.Equal(4, state.SubjectOffsetY);
        Assert.Equal(0.8f, state.BackgroundScale);
        Assert.True(state.BackgroundIsCardArt);
        Assert.False(state.SubjectIsFromCardArt);
        Assert.True(state.HasSubject);
        Assert.True(state.HasBackground);
    }

    [Fact]
    public void FileExtension_IsOverframes()
    {
        Assert.Equal(".overframes", OverFrameBundleService.FileExtension);
    }

    [Fact]
    public void Import_ForcesBackup_AndFailsOnInvalidGamePath()
    {
        var master = Path.Combine(Path.GetTempPath(), "floowan-of-import-master-" + Guid.NewGuid().ToString("N") + ".db");
        var user = Path.Combine(Path.GetTempPath(), "floowan-of-import-user-" + Guid.NewGuid().ToString("N") + ".db");
        var zipPath = Path.Combine(Path.GetTempPath(), "floowan-of-import-" + Guid.NewGuid().ToString("N") + ".overframes");
        try
        {
            CreateMinimalMasterDb(master);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(OverFrameBundleService.ManifestFileName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("""{"formatVersion":1,"cards":[{"cardId":1,"name":"Test","hasEditLayer":false}]}""");
            }

            using var db = new Floowan.Core.Data.CardDatabase(master, user);
            using var service = new OverFrameBundleService();
            // createBackup: false must still be forced on inside Import (same as create-new OF).
            var result = service.Import(
                playerDataPath: Path.Combine(Path.GetTempPath(), "not-a-localdata-" + Guid.NewGuid().ToString("N")),
                database: db,
                bundlePath: zipPath,
                cardIds: new[] { 1 },
                createBackup: false);

            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* ignore */ }
            try { File.Delete(master); } catch { /* ignore */ }
            try { File.Delete(user); } catch { /* ignore */ }
        }
    }

    private static void CreateMinimalMasterDb(string path)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
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
            VALUES ('Test', 'desc', 'bundle1', 0, 1, 0, 0);
            """;
        cmd.ExecuteNonQuery();
    }
}

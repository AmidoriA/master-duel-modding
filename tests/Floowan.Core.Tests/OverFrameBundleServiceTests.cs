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
}

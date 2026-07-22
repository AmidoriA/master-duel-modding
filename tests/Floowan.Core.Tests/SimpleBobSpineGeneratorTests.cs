using Floowan.Core.Spine;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class SimpleBobSpineGeneratorTests
{
    [Fact]
    public void FromSingleImage_EmitsAtlasJsonAndTranslateKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "floowan-spine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "art.png");
        try
        {
            using (var image = new Image<Rgba32>(64, 96))
            {
                image[0, 0] = new Rgba32(255, 0, 0, 255);
                image.SaveAsPng(png);
            }

            var gen = new SimpleBobSpineGenerator();
            var assets = gen.FromSingleImage(png, "P14944", new SpineCutInOptions
            {
                Framerate = 22,
                DurationSeconds = 1,
                SkeletonSizeScale = 5
            });

            Assert.Equal("P14944", assets.TextureName);
            Assert.Equal("P14944JS", assets.SkeletonName);
            Assert.Contains("P14944.png", assets.AtlasText, StringComparison.Ordinal);
            Assert.Contains("art", assets.AtlasText, StringComparison.Ordinal);
            Assert.Contains("\"spine\": \"4.0.64\"", assets.SkeletonJson);
            Assert.Contains("\"translate\"", assets.SkeletonJson);
            Assert.Contains("\"width\": 320", assets.SkeletonJson); // 64 * 5
            Assert.Contains("\"height\": 480", assets.SkeletonJson);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void FromImageSequence_IsNotImplementedYet()
    {
        var gen = new SimpleBobSpineGenerator();
        Assert.Throws<NotImplementedException>(() =>
            gen.FromImageSequence(["a.png", "b.png"], "P1"));
    }
}

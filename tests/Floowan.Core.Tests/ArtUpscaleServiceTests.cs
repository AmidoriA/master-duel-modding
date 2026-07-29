using Floowan.Core.Imaging;

namespace Floowan.Core.Tests;

public class ArtUpscaleServiceTests
{
    [Theory]
    [InlineData(512, 512, true)]
    [InlineData(512, 683, true)]
    [InlineData(500, 500, true)]
    [InlineData(256, 256, false)]
    [InlineData(640, 640, false)]
    [InlineData(1024, 1024, false)]
    [InlineData(1024, 1366, false)]
    [InlineData(704, 1024, false)]
    [InlineData(800, 600, false)]
    public void NeedsOverFrameArtUpscale_ClassifiesMdSizedArt(int w, int h, bool expected)
    {
        Assert.Equal(expected, ArtUpscaleService.NeedsOverFrameArtUpscale(w, h));
    }

    [Fact]
    public void GetOverFrameUpscaleTargetSize_DoublesDimensions()
    {
        Assert.Equal((1024, 1024), ArtUpscaleService.GetOverFrameUpscaleTargetSize(512, 512));
        Assert.Equal((1024, 1366), ArtUpscaleService.GetOverFrameUpscaleTargetSize(512, 683));
    }

    [Fact]
    public void ModelConstants_MatchDownloadScriptExpectations()
    {
        Assert.Equal("RealESR-AnimeVideo-v3_x4.onnx", ArtUpscaleService.ModelName);
        Assert.Equal(4, ArtUpscaleService.ModelScale);
        Assert.Equal(2, ArtUpscaleService.TargetScale);
        Assert.Equal(2_495_473, ArtUpscaleService.ModelExpectedBytes);
        Assert.Equal(
            "00ece3ac21c43ee31459216b5174b2cea0c5325044c5142aeb840f4890e175ff",
            ArtUpscaleService.ModelSha256);
        Assert.Contains("tidus2102/Real-ESRGAN", ArtUpscaleService.ModelUrl);
    }

    [Fact]
    public void UpscaleRgbForOverFrameIfNeeded_ReturnsSameInstanceWhenNotNeeded()
    {
        using var service = new ArtUpscaleService(modelDirectory: Path.GetTempPath());
        using var source = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            1024, 1024, new SixLabors.ImageSharp.PixelFormats.Rgba32(10, 20, 30, 255));

        var result = service.UpscaleRgbForOverFrameIfNeeded(source);

        Assert.Same(source, result);
    }

    [Fact]
    public void UpscaleRgbToTarget_ThrowsWhenModelMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "floowan-upscale-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var service = new ArtUpscaleService(modelDirectory: dir);
            using var source = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                32, 32, new SixLabors.ImageSharp.PixelFormats.Rgba32(10, 20, 30, 255));

            Assert.Throws<FileNotFoundException>(() => service.UpscaleRgbToTarget(source, 64, 64));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void UpscaleRgbToTarget_RunsWhenLocalModelPresent()
    {
        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Floowan",
            "models",
            "realesrgan");
        var modelPath = Path.Combine(modelDir, ArtUpscaleService.ModelName);
        if (!File.Exists(modelPath))
            return; // CI / fresh machines without the download-once weights.

        using var service = new ArtUpscaleService(modelDirectory: modelDir);
        using var source = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            64, 64, new SixLabors.ImageSharp.PixelFormats.Rgba32(40, 180, 220, 255));
        // Seed a different corner so we can assert content moved, not just size.
        source[0, 0] = new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 0, 0, 255);

        using var upscaled = service.UpscaleRgbToTarget(source, 128, 128);

        Assert.Equal(128, upscaled.Width);
        Assert.Equal(128, upscaled.Height);
        // Solid fill should remain in the cyan family (model may slightly shift chroma).
        var p = upscaled[64, 64];
        Assert.True(p.B > 100 && p.G > 100, $"expected cyan-ish center, got {p}");
        Assert.NotEqual(0, p.A);
    }
}

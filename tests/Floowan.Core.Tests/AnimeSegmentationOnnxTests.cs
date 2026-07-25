using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public sealed class AnimeSegmentationOnnxTests
{
    private static int CountOpaqueMaskPixels(Image<L8> mask)
    {
        var keep = 0;
        for (var y = 0; y < mask.Height; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < row.Length; x++)
            {
                if (row[x].PackedValue >= OverFrameAutoArtComposer.MaskKeepThreshold)
                    keep++;
            }
        }

        return keep;
    }

    [Fact]
    public void ComputeLetterbox_Portrait_PadsLeftRight()
    {
        // SkyTNT: h>w → contentH=size, contentW=size*w/h
        var layout = AutoOverFrameArtService.ComputeLetterbox(512, 683, 1024);
        Assert.Equal(1024, layout.ContentHeight);
        Assert.Equal((int)(1024 * 512.0 / 683), layout.ContentWidth);
        Assert.Equal(0, layout.PadTop);
        Assert.Equal((1024 - layout.ContentWidth) / 2, layout.PadLeft);
        Assert.True(layout.PadLeft + layout.ContentWidth <= 1024);
        Assert.True(layout.ContentWidth > 700 && layout.ContentWidth < 800);
    }

    [Fact]
    public void ComputeLetterbox_Landscape_PadsTopBottom()
    {
        var layout = AutoOverFrameArtService.ComputeLetterbox(800, 400, 1024);
        Assert.Equal(1024, layout.ContentWidth);
        Assert.Equal(512, layout.ContentHeight);
        Assert.Equal(0, layout.PadLeft);
        Assert.Equal(256, layout.PadTop);
    }

    [Fact]
    public void ComputeLetterbox_Square_NoPadding()
    {
        var layout = AutoOverFrameArtService.ComputeLetterbox(640, 640, 1024);
        Assert.Equal(1024, layout.ContentWidth);
        Assert.Equal(1024, layout.ContentHeight);
        Assert.Equal(0, layout.PadLeft);
        Assert.Equal(0, layout.PadTop);
    }

    [Fact]
    public void ModelConstants_PointAtSkyTntIsnetis()
    {
        Assert.Equal("isnetis.onnx", AutoOverFrameArtService.ModelName);
        Assert.Contains("skytnt/anime-seg", AutoOverFrameArtService.ModelUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isnetis.onnx", AutoOverFrameArtService.ModelUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1024, AutoOverFrameArtService.ModelSize);
        Assert.Equal(32, AutoOverFrameArtService.ModelMd5.Length);
    }

    /// <summary>
    /// Optional smoke: runs only when <c>isnetis.onnx</c> (or legacy rembg twin) is already
    /// on disk under %LOCALAPPDATA%\Floowan\models — does not download ~168 MB in CI.
    /// Uses a small SkyTNT Space anime example fixture (~54 KB), not a synthetic box
    /// (plain shapes yield near-zero masks under the official /255 letterbox path).
    /// </summary>
    [Fact]
    public async Task PrepareSubjectWithRembgAsync_ProducesOpaqueMask_WhenLocalModelPresent()
    {
        var modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Floowan",
            "models");
        var modelPath = Path.Combine(modelsDir, AutoOverFrameArtService.ModelName);
        var legacyPath = Path.Combine(modelsDir, AutoOverFrameArtService.LegacyModelName);
        if (!File.Exists(modelPath) && !File.Exists(legacyPath))
            return;

        var fixture = Path.Combine(AppContext.BaseDirectory, "anime-seg-example.jpg");
        if (!File.Exists(fixture))
            fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "anime-seg-example.jpg");
        Assert.True(File.Exists(fixture), "Missing Fixtures/anime-seg-example.jpg test asset.");

        using var service = new AutoOverFrameArtService(modelsDir);
        var (source, mask) = await service.PrepareSubjectWithRembgAsync(fixture);
        using (source)
        using (mask)
        {
            Assert.Equal(source.Width, mask.Width);
            Assert.Equal(source.Height, mask.Height);
            var keep = CountOpaqueMaskPixels(mask);
            Assert.True(keep > 1000, $"expected opaque subject pixels from isnetis, got {keep}");
        }
    }
}

using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class Sam2PointCutoutServiceTests
{
    [Fact]
    public void ScalePointToEncoder_MapsCenterOfSquare()
    {
        var (x, y) = Sam2PointCutoutService.ScalePointToEncoder(
            imageX: 256,
            imageY: 256,
            originalWidth: 512,
            originalHeight: 512,
            encoderWidth: 1024,
            encoderHeight: 1024);

        Assert.Equal(512f, x, precision: 2);
        Assert.Equal(512f, y, precision: 2);
    }

    [Fact]
    public void ScalePointToEncoder_MapsPortraitCardArt()
    {
        var (x, y) = Sam2PointCutoutService.ScalePointToEncoder(
            imageX: 100,
            imageY: 200,
            originalWidth: 512,
            originalHeight: 683,
            encoderWidth: 1024,
            encoderHeight: 1024);

        Assert.Equal(100f / 512f * 1024f, x, precision: 2);
        Assert.Equal(200f / 683f * 1024f, y, precision: 2);
    }

    [Theory]
    [InlineData(200, 300, 400, 600, 200, 300, true, 100f, 150f)] // fills host; center
    [InlineData(10, 10, 400, 600, 200, 200, false, 0f, 0f)] // letterbox top margin
    [InlineData(200, 300, 400, 600, 200, 200, true, 100f, 100f)] // center of square in tall host
    public void TryMapPreviewClickToImage_UniformStretch(
        double hostX,
        double hostY,
        double hostW,
        double hostH,
        int imgW,
        int imgH,
        bool expectedHit,
        float expectedX,
        float expectedY)
    {
        var hit = Sam2PointCutoutService.TryMapPreviewClickToImage(
            hostX, hostY, hostW, hostH, imgW, imgH, out var imageX, out var imageY);

        Assert.Equal(expectedHit, hit);
        if (expectedHit)
        {
            Assert.Equal(expectedX, imageX, precision: 1);
            Assert.Equal(expectedY, imageY, precision: 1);
        }
    }

    [Fact]
    public void ApplyMaskAsAlpha_CopiesMaskIntoAlphaChannel()
    {
        using var source = new Image<Rgba32>(4, 2, new Rgba32(10, 20, 30, 255));
        using var mask = new Image<L8>(4, 2);
        mask[1, 0] = new L8(200);
        mask[2, 1] = new L8(50);

        using var cutout = Sam2PointCutoutService.ApplyMaskAsAlpha(source, mask);

        Assert.Equal(0, cutout[0, 0].A);
        Assert.Equal(200, cutout[1, 0].A);
        Assert.Equal(10, cutout[1, 0].R);
        Assert.Equal(50, cutout[2, 1].A);
    }

    [Fact]
    public void BuildPromptsFromPaintMask_ReturnsBoxAndPositives()
    {
        using var paint = new Image<L8>(20, 20);
        for (var y = 4; y <= 12; y++)
        {
            for (var x = 6; x <= 14; x++)
                paint[x, y] = new L8(255);
        }

        var prompts = Sam2PointCutoutService.BuildPromptsFromPaintMask(paint);

        Assert.Contains(prompts, p => p.Label == 2f && p.X == 6f && p.Y == 4f);
        Assert.Contains(prompts, p => p.Label == 3f && p.X == 14f && p.Y == 12f);
        Assert.Contains(prompts, p => p.Label == 1f);
        Assert.True(prompts.Count >= 3);
    }

    [Fact]
    public void BuildPromptsFromPaintMask_EmptyWhenNothingPainted()
    {
        using var paint = new Image<L8>(8, 8);
        Assert.Empty(Sam2PointCutoutService.BuildPromptsFromPaintMask(paint));
    }

    [Fact]
    public void UnionMasks_TakesPerPixelMaximum()
    {
        using var a = new Image<L8>(3, 1);
        using var b = new Image<L8>(3, 1);
        a[0, 0] = new L8(40);
        a[1, 0] = new L8(200);
        b[1, 0] = new L8(100);
        b[2, 0] = new L8(255);

        using var union = Sam2PointCutoutService.UnionMasks(a, b);

        Assert.Equal(40, union[0, 0].PackedValue);
        Assert.Equal(200, union[1, 0].PackedValue);
        Assert.Equal(255, union[2, 0].PackedValue);
    }

    [Fact]
    public void SubtractMasks_ClearsOpaqueRemovalPixels()
    {
        using var existing = new Image<L8>(3, 1);
        using var removal = new Image<L8>(3, 1);
        existing[0, 0] = new L8(200);
        existing[1, 0] = new L8(200);
        existing[2, 0] = new L8(200);
        removal[1, 0] = new L8(255);
        removal[2, 0] = new L8(10); // below keep threshold — keep existing

        using var result = Sam2PointCutoutService.SubtractMasks(existing, removal);

        Assert.Equal(200, result[0, 0].PackedValue);
        Assert.Equal(0, result[1, 0].PackedValue);
        Assert.Equal(200, result[2, 0].PackedValue);
    }

    [Fact]
    public void BundleConstants_MatchDocumentedTinyPackage()
    {
        Assert.Equal("sam2_hiera_tiny.encoder.onnx", Sam2PointCutoutService.EncoderFileName);
        Assert.Equal("sam2_hiera_tiny.decoder.onnx", Sam2PointCutoutService.DecoderFileName);
        Assert.Contains("sam2_hiera_tiny.zip", Sam2PointCutoutService.BundleZipUrl);
        Assert.Equal(64, Sam2PointCutoutService.BundleZipSha256.Length);
    }
}
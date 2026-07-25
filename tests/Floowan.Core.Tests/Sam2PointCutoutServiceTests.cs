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
    public void BundleConstants_MatchDocumentedTinyPackage()
    {
        Assert.Equal("sam2_hiera_tiny.encoder.onnx", Sam2PointCutoutService.EncoderFileName);
        Assert.Equal("sam2_hiera_tiny.decoder.onnx", Sam2PointCutoutService.DecoderFileName);
        Assert.Contains("sam2_hiera_tiny.zip", Sam2PointCutoutService.BundleZipUrl);
        Assert.Equal(64, Sam2PointCutoutService.BundleZipSha256.Length);
    }
}
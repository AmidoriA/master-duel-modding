using Floowan.Core.Backup;
using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class OfEditLayerUpscaleCompatTests
{
    [Theory]
    [InlineData(512, 512, true)]
    [InlineData(512, 683, true)]
    [InlineData(1024, 1024, false)]
    [InlineData(1024, 1366, false)]
    public void NeedsLayerUpscale_DetectsSubjectSize(int w, int h, bool expected)
    {
        using var subject = new Image<Rgba32>(w, h);
        using var mask = new Image<L8>(w, h);
        Assert.Equal(expected, OfEditLayerUpscaleCompat.NeedsLayerUpscale(subject, mask, null));
    }

    [Fact]
    public void NeedsLayerUpscale_DetectsBackgroundOnly512()
    {
        using var background = new Image<Rgba32>(512, 512);
        Assert.True(OfEditLayerUpscaleCompat.NeedsLayerUpscale(null, null, background));
    }

    [Fact]
    public void NeedsLayerUpscale_FalseWhenAll1024()
    {
        using var subject = new Image<Rgba32>(1024, 1024);
        using var mask = new Image<L8>(1024, 1024);
        using var background = new Image<Rgba32>(1024, 1024);
        Assert.False(OfEditLayerUpscaleCompat.NeedsLayerUpscale(subject, mask, background));
    }

    [Fact]
    public void ResolveSpatialUpscaleFactor_Is2For512Class()
    {
        Assert.Equal(2, OfEditLayerUpscaleCompat.ResolveSpatialUpscaleFactor(512, 512));
        Assert.Equal(2, OfEditLayerUpscaleCompat.ResolveSpatialUpscaleFactor(512, 683));
        Assert.Equal(1, OfEditLayerUpscaleCompat.ResolveSpatialUpscaleFactor(1024, 1024));
    }

    [Theory]
    [InlineData(512, 512, 1.5f)]
    [InlineData(512, 512, 1.38f)]
    [InlineData(512, 683, 1.5f)]
    [InlineData(512, 512, 2.0f)]
    public void CoverFitScaledSize_UnchangedWhenSourceDoubledWithSameMultiplier(
        int w, int h, float multiplier)
    {
        var artWindow = OverFrameAutoArtComposer.ArtWindow;
        var before = OfEditLayerUpscaleCompat.CoverFitScaledSize(w, h, artWindow, multiplier);
        var after = OfEditLayerUpscaleCompat.CoverFitScaledSize(w * 2, h * 2, artWindow, multiplier);

        Assert.Equal(before.ScaledW, after.ScaledW);
        Assert.Equal(before.ScaledH, after.ScaledH);
    }

    [Fact]
    public void BackgroundPanLimits_UnchangedWhenSourceDoubledWithSameScale()
    {
        var artWindow = OverFrameAutoArtComposer.ArtWindow;
        const float scale = 1.5f;
        var before = OverFrameAutoArtComposer.GetBackgroundPanLimits(512, 512, artWindow, scale);
        var after = OverFrameAutoArtComposer.GetBackgroundPanLimits(1024, 1024, artWindow, scale);

        Assert.Equal(before.MaxPanX, after.MaxPanX);
        Assert.Equal(before.MaxPanY, after.MaxPanY);
    }

    [Fact]
    public void AdjustTransformsAfterLayerUpscale_KeepsCoverRelativeScalesAndCanvasOffsets()
    {
        var state = new CustomOverframeStageState
        {
            SubjectScale = 1.75f,
            SubjectOffsetX = 12,
            SubjectOffsetY = -8,
            BackgroundScale = 1.75f,
            BackgroundOffsetX = 4,
            BackgroundOffsetY = -3,
        };
        var before = OfEditLayerUpscaleCompat.CaptureTransforms(state);

        Assert.True(OfEditLayerUpscaleCompat.AdjustTransformsAfterLayerUpscale(state, spatialFactor: 2));
        Assert.False(OfEditLayerUpscaleCompat.AdjustTransformsAfterLayerUpscale(state, spatialFactor: 1));

        Assert.Equal(before, OfEditLayerUpscaleCompat.CaptureTransforms(state));
    }

    [Fact]
    public void UpscaleEditLayersInPlace_NoOpsWhenAlready1024WithoutModel()
    {
        using var service = new ArtUpscaleService(modelDirectory: Path.GetTempPath());
        Image<Rgba32>? subject = new Image<Rgba32>(1024, 1024);
        Image<L8>? mask = new Image<L8>(1024, 1024);
        Image<Rgba32>? background = new Image<Rgba32>(1024, 1024);
        try
        {
            Assert.False(service.UpscaleEditLayersInPlace(ref subject, ref mask, ref background));
            Assert.Equal(1024, subject!.Width);
            Assert.Equal(1024, mask!.Width);
            Assert.Equal(1024, background!.Width);
        }
        finally
        {
            subject?.Dispose();
            mask?.Dispose();
            background?.Dispose();
        }
    }
}

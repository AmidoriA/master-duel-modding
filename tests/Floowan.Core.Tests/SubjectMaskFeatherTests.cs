using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class SubjectMaskFeatherTests
{
    [Fact]
    public void Apply_HardRectangle_ProducesInwardSoftEdge_ExteriorStaysZero()
    {
        using var mask = new Image<L8>(32, 32, new L8(0));
        for (var y = 8; y < 24; y++)
        for (var x = 8; x < 24; x++)
            mask[x, y] = new L8(255);

        using var soft = SubjectMaskFeather.Apply(mask, radiusPx: 1.5f);

        Assert.Equal(0, soft[0, 0].PackedValue);
        Assert.Equal(0, soft[7, 16].PackedValue);
        Assert.Equal(0, soft[8, 7].PackedValue);

        // Deep interior stays fully opaque.
        Assert.Equal(255, soft[16, 16].PackedValue);

        // Edge-adjacent interior is partially faded (not hard 0/255).
        var edge = soft[8, 16].PackedValue;
        Assert.InRange(edge, 1, 254);
        Assert.True(edge < soft[9, 16].PackedValue);
        Assert.True(soft[9, 16].PackedValue < soft[10, 16].PackedValue
            || soft[10, 16].PackedValue == 255);
    }

    [Fact]
    public void ApplyToRgbaAlphaInPlace_SoftensOpaqueCutoutEdge()
    {
        using var cutout = new Image<Rgba32>(32, 32, new Rgba32(10, 20, 30, 0));
        for (var y = 8; y < 24; y++)
        for (var x = 8; x < 24; x++)
            cutout[x, y] = new Rgba32(200, 40, 30, 255);

        SubjectMaskFeather.ApplyToRgbaAlphaInPlace(cutout, radiusPx: 1.5f);

        Assert.Equal(0, cutout[0, 0].A);
        Assert.Equal(255, cutout[16, 16].A);
        Assert.InRange(cutout[8, 16].A, 1, 254);
        Assert.Equal(200, cutout[8, 16].R);
    }

    [Fact]
    public void Compose_SubjectDragLayer_HasPartialFoilAlongSoftEdge()
    {
        using var source = new Image<Rgba32>(128, 128, new Rgba32(200, 40, 30, 255));
        using var mask = new Image<L8>(128, 128, new L8(0));
        for (var y = 16; y < 112; y++)
        for (var x = 16; x < 112; x++)
            mask[x, y] = new L8(255);

        using var layer = OverFrameAutoArtComposer.RenderSubjectDragLayer(
            source,
            mask,
            composeMode: OverFrameComposeMode.CustomArtOnly);

        var foil = OverFrameAutoArtComposer.FoilMaskAlpha;
        var soft = 0;
        var solid = 0;
        for (var y = 0; y < layer.Height; y++)
        for (var x = 0; x < layer.Width; x++)
        {
            var a = layer[x, y].A;
            if (a == 0)
                continue;
            if (a == foil)
                solid++;
            else if (a > 0 && a < foil)
                soft++;
        }

        Assert.True(solid > 0, "expected solid foil interior coverage");
        Assert.True(soft > 0, "expected partial foil alphas along soft subject edge");
    }
}

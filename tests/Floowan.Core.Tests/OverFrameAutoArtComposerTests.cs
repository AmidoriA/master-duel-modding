using Floowan.Core.Assets;
using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class OverFrameAutoArtComposerTests
{
    [Fact]
    public void Compose_TrimsAndCentersSubjectOnOverFrameCanvas()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(220, 30, 20, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 10; y < 90; y++)
        for (var x = 25; x < 75; x++)
            mask[x, y] = new L8(255);

        using var result = OverFrameAutoArtComposer.Compose(source, mask);

        Assert.Equal(OverFrameConstants.Width, result.Width);
        Assert.Equal(OverFrameConstants.Height, result.Height);
        Assert.Equal(0, result[0, 0].A);
        Assert.True(result[result.Width / 2, result.Height / 2].A > 200);

        var visible = GetVisibleBounds(result);
        Assert.InRange(visible.Width, 580, 595);
        Assert.InRange(visible.Height, 935, 945);
        Assert.InRange(Math.Abs((visible.Left + visible.Right) - result.Width), 0, 2);
        Assert.InRange(Math.Abs((visible.Top + visible.Bottom) - result.Height), 0, 2);
    }

    [Fact]
    public void Compose_RejectsMaskWithoutVisibleSubject()
    {
        using var source = new Image<Rgba32>(32, 32, Color.White);
        using var mask = new Image<L8>(32, 32, new L8(0));

        var error = Assert.Throws<InvalidOperationException>(
            () => OverFrameAutoArtComposer.Compose(source, mask));

        Assert.Contains("visible subject", error.Message);
    }

    private static Rectangle GetVisibleBounds(Image<Rgba32> image)
    {
        var left = image.Width;
        var top = image.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            if (image[x, y].A <= OverFrameAutoArtComposer.VisibleAlphaThreshold)
                continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
}

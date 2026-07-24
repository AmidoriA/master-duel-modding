using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class ImagePreparationTests
{
    [Fact]
    public void Validate_FailsForMissingFile()
    {
        var result = ImagePreparation.Validate(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".png"));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_AndPrepare_ProducesExpectedByteLength()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-img-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var img = new Image<Rgba32>(64, 32, Color.Red))
                img.SaveAsPng(path);

            var validation = ImagePreparation.Validate(path, expectedWidth: 512, expectedHeight: 512);
            Assert.True(validation.IsValid);
            Assert.Equal(64, validation.Width);
            Assert.NotNull(validation.Warning);

            var bytes = ImagePreparation.PrepareRgba32TextureBytes(path, 16, 16);
            Assert.Equal(16 * 16 * 4, bytes.Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Validate_AcceptsPendulumExactSize_WithInfo()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-pend-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var img = new Image<Rgba32>(512, 683, Color.Blue))
                img.SaveAsPng(path);

            var validation = ImagePreparation.Validate(path, expectedWidth: 512, expectedHeight: 683);
            Assert.True(validation.IsValid);
            Assert.Equal(512, validation.Width);
            Assert.Equal(683, validation.Height);
            Assert.Null(validation.Warning);
            Assert.Contains("Pendulum", validation.Info);
            Assert.Contains("3:4", validation.Info);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Validate_AcceptsPendulumNativeCanvas_WithInfo()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-pend-native-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var img = new Image<Rgba32>(512, 1024, Color.Blue))
                img.SaveAsPng(path);

            var validation = ImagePreparation.Validate(path, expectedWidth: 512, expectedHeight: 1024);
            Assert.True(validation.IsValid);
            Assert.Contains("native", validation.Info, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Validate_AcceptsNormalSize_AndWarnsWhenTargetIsPendulumCanvas()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-norm-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var img = new Image<Rgba32>(512, 512, Color.Green))
                img.SaveAsPng(path);

            var validation = ImagePreparation.Validate(path, expectedWidth: 512, expectedHeight: 1024);
            Assert.True(validation.IsValid);
            Assert.Contains("letterboxed", validation.Warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("normal", validation.Info, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Prepare_PendulumIntoSquare_LetterboxesWithoutSquash()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-pend-prep-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // Opaque magenta 3:4 — after letterbox into 512×512, left/right bars stay transparent.
            using (var img = new Image<Rgba32>(512, 683, new Rgba32(255, 0, 255, 255)))
                img.SaveAsPng(path);

            var bytes = ImagePreparation.PrepareRgba32TextureBytes(path, 512, 512, preserveAspect: true);
            Assert.Equal(512 * 512 * 4, bytes.Length);

            // Vertically flipped RGBA. Pad centers a ~384×512 image in 512×512.
            static Rgba32 At(byte[] data, int x, int y)
            {
                var i = (y * 512 + x) * 4;
                return new Rgba32(data[i], data[i + 1], data[i + 2], data[i + 3]);
            }

            Assert.Equal(0, At(bytes, 0, 256).A);
            Assert.Equal(0, At(bytes, 511, 256).A);
            Assert.Equal(255, At(bytes, 256, 256).A);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Prepare_ExactPendulumSize_KeepsPixelCount()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-pend-exact-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var img = new Image<Rgba32>(512, 683, Color.Orange))
                img.SaveAsPng(path);

            var bytes = ImagePreparation.PrepareRgba32TextureBytes(path, 512, 683, preserveAspect: true);
            Assert.Equal(512 * 683 * 4, bytes.Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Prepare_PendulumArtOntoNativeCanvas_TopAlignsLetterbox()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-pend-top-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // Opaque magenta 512×683 → tall 512×1024 canvas: art at top, transparent below.
            using (var img = new Image<Rgba32>(512, 683, new Rgba32(255, 0, 255, 255)))
                img.SaveAsPng(path);

            var bytes = ImagePreparation.PrepareRgba32TextureBytes(path, 512, 1024, preserveAspect: true);
            Assert.Equal(512 * 1024 * 4, bytes.Length);

            // Vertically flipped RGBA for Unity.
            static Rgba32 At(byte[] data, int x, int y)
            {
                var i = (y * 512 + x) * 4;
                return new Rgba32(data[i], data[i + 1], data[i + 2], data[i + 3]);
            }

            // After vertical flip: texture y=0 is visual bottom → transparent pad.
            Assert.Equal(0, At(bytes, 256, 0).A);
            // Visual top of art → near texture y=1023 after flip → opaque.
            Assert.Equal(255, At(bytes, 256, 1023).A);
            Assert.Equal(255, At(bytes, 256, 1023 - 100).A);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NormalizeToCardArtExport_CropsNativeCanvasTo512x683()
    {
        using var canvas = new Image<Rgba32>(512, 1024);
        canvas.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var color = y < 683
                    ? new Rgba32(255, 0, 0, 255)
                    : new Rgba32(0, 255, 0, 255);
                row.Fill(color);
            }
        });

        ImagePreparation.NormalizeToCardArtExport(canvas);
        Assert.Equal(512, canvas.Width);
        Assert.Equal(683, canvas.Height);
        Assert.Equal(255, canvas[0, 0].R);
        Assert.Equal(0, canvas[0, 0].G);
        Assert.Equal(255, canvas[511, 682].R);
    }

    [Fact]
    public void NormalizeToCardArtExport_ScalesExisting3x4ToCanonical()
    {
        using var art = new Image<Rgba32>(384, 512, Color.Blue);
        ImagePreparation.NormalizeToCardArtExport(art);
        Assert.Equal(512, art.Width);
        Assert.Equal(683, art.Height);
    }

    [Fact]
    public void NormalizeToCardArtExport_LeavesNormalIllustAlone()
    {
        using var art = new Image<Rgba32>(512, 512, Color.Green);
        ImagePreparation.NormalizeToCardArtExport(art);
        Assert.Equal(512, art.Width);
        Assert.Equal(512, art.Height);
    }

    [Fact]
    public void CardArtTextureSizes_ClassifiesKnownSizes()
    {
        Assert.Equal(CardArtSizeKind.Normal, CardArtTextureSizes.Classify(512, 512));
        Assert.Equal(CardArtSizeKind.Pendulum, CardArtTextureSizes.Classify(512, 683));
        Assert.Equal(CardArtSizeKind.Pendulum, CardArtTextureSizes.Classify(512, 1024));
        Assert.Equal(CardArtSizeKind.Pendulum, CardArtTextureSizes.Classify(384, 512)); // 3:4
        Assert.Equal(CardArtSizeKind.Other, CardArtTextureSizes.Classify(256, 512)); // 1:2, not Pendulum
        Assert.True(CardArtTextureSizes.IsSupportedCardArtSize(512, 512));
        Assert.True(CardArtTextureSizes.IsSupportedCardArtSize(512, 683));
        Assert.True(CardArtTextureSizes.IsSupportedCardArtSize(512, 1024));
        Assert.True(CardArtTextureSizes.HasPendulumAspect(512, 683));
        Assert.False(CardArtTextureSizes.HasPendulumAspect(512, 1024)); // canvas, not 3:4
        Assert.True(CardArtTextureSizes.IsTallPendulumStorageCanvas(512, 1024));
        Assert.Equal(683, CardArtTextureSizes.PendulumArtCropHeight(512));
        Assert.Equal((512, 683), CardArtTextureSizes.GetCardArtExportSize(512, 1024));
        Assert.Equal((512, 512), CardArtTextureSizes.GetCardArtExportSize(512, 512));
        Assert.Contains("3:4", CardArtTextureSizes.Describe(512, 683));
        Assert.Contains("native", CardArtTextureSizes.Describe(512, 1024), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Slugify_RemovesUnsafeCharacters()
    {
        Assert.Equal("blue-eyes-white-dragon", ImagePreparation.Slugify("Blue-Eyes White Dragon!"));
    }
}

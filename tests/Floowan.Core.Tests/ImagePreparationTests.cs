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
    public void Slugify_RemovesUnsafeCharacters()
    {
        Assert.Equal("blue-eyes-white-dragon", ImagePreparation.Slugify("Blue-Eyes White Dragon!"));
    }
}

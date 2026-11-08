using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class SafeImageTests
{
    [Fact]
    public void Configuration_RegistersOnlyTheFormatsTheAppAccepts()
    {
        var formats = SafeImage.Configuration.ImageFormats.Select(f => f.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "BMP", "JPEG", "PNG", "Webp" }.OrderBy(n => n), formats, StringComparer.OrdinalIgnoreCase);

        var accepted = ImagePreparation.SupportedExtensions.Select(e => e.TrimStart('.'));
        foreach (var ext in accepted)
            Assert.True(SafeImage.Configuration.ImageFormatsManager.TryFindFormatByFileExtension(ext, out _), ext);
    }

    [Fact]
    public void Load_RejectsTiff()
    {
        using var image = new Image<Rgba32>(8, 8, Color.Red);
        using var tiff = new MemoryStream();
        image.Save(tiff, new TiffEncoder());

        Assert.ThrowsAny<UnknownImageFormatException>(() => SafeImage.Load<Rgba32>(tiff.ToArray()));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("bmp")]
    [InlineData("webp")]
    public void Load_ReadsAcceptedFormatsFromFile(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"floowan-safeimage-{Guid.NewGuid():N}.{ext}");
        try
        {
            using (var image = new Image<Rgba32>(16, 12, new Rgba32(10, 200, 30, 255)))
            {
                switch (ext)
                {
                    case "png": image.Save(path, new PngEncoder()); break;
                    case "jpg": image.Save(path, new JpegEncoder()); break;
                    case "bmp": image.Save(path, new BmpEncoder()); break;
                    default: image.Save(path, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }); break;
                }
            }

            using var loaded = SafeImage.Load<Rgba32>(path);
            Assert.Equal(16, loaded.Width);
            Assert.Equal(12, loaded.Height);
            Assert.True(loaded[8, 6].G > 150);

            var info = SafeImage.Identify(path);
            Assert.Equal(16, info.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SkipsIccProfiles()
    {
        using var image = new Image<Rgba32>(4, 4, Color.Blue);
        image.Metadata.IccProfile = new IccProfile();
        using var png = new MemoryStream();
        image.Save(png, new PngEncoder());

        using (var normal = Image.Load<Rgba32>(png.ToArray()))
            Assert.NotNull(normal.Metadata.IccProfile);

        using var safe = SafeImage.Load<Rgba32>(png.ToArray());
        Assert.Null(safe.Metadata.IccProfile);
        Assert.Equal(new Rgba32(0, 0, 255, 255), safe[1, 1]);
    }
}

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// The only way the app decodes image files. ImageSharp 3.1.x has known flaws in its TIFF
/// decoder and ICC profile parser (fixed in 3.2.0, which needs a Six Labors license key to
/// build). Decoding here registers only the formats the app accepts
/// (<see cref="ImagePreparation.SupportedExtensions"/>: PNG, JPEG, BMP, WebP), skips
/// metadata, and drops any ICC profile right after decoding. 3.1 decoders read only the ICC
/// header; the vulnerable tag (CLUT) parsing runs when <c>IccProfile.Entries</c> is read, so
/// no decoded image keeps a profile that could reach it. ImageSharp 3.1 never applied ICC
/// profiles to pixels, so dropping them does not change how images look.
/// </summary>
public static class SafeImage
{
    public static Configuration Configuration { get; } = new(
        new PngConfigurationModule(),
        new JpegConfigurationModule(),
        new BmpConfigurationModule(),
        new WebpConfigurationModule());

    public static DecoderOptions DecoderOptions { get; } = new()
    {
        Configuration = Configuration,
        SkipMetadata = true,
    };

    public static Image Load(string path) => DropIccProfiles(Image.Load(DecoderOptions, path));

    public static Image<TPixel> Load<TPixel>(string path)
        where TPixel : unmanaged, IPixel<TPixel> =>
        DropIccProfiles(Image.Load<TPixel>(DecoderOptions, path));

    public static Image<TPixel> Load<TPixel>(ReadOnlySpan<byte> data)
        where TPixel : unmanaged, IPixel<TPixel> =>
        DropIccProfiles(Image.Load<TPixel>(DecoderOptions, data));

    public static ImageInfo Identify(string path)
    {
        var info = Image.Identify(DecoderOptions, path);
        info.Metadata.IccProfile = null;
        return info;
    }

    private static TImage DropIccProfiles<TImage>(TImage image)
        where TImage : Image
    {
        image.Metadata.IccProfile = null;
        foreach (var frame in image.Frames)
            frame.Metadata.IccProfile = null;
        return image;
    }
}

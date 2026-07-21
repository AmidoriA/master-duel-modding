using Floowan.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Floowan.Core.Imaging;

public static class ImagePreparation
{
    public static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    public static ImageValidationResult Validate(string imagePath, int? expectedWidth = null, int? expectedHeight = null)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return ImageValidationResult.Fail("Image file does not exist.");

        var ext = Path.GetExtension(imagePath);
        if (!SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return ImageValidationResult.Fail($"Unsupported image type '{ext}'. Use PNG/JPEG/BMP/WebP.");

        try
        {
            using var image = Image.Load(imagePath);
            string? warning = null;
            if (expectedWidth is int w && expectedHeight is int h && (image.Width != w || image.Height != h))
            {
                warning =
                    $"Image is {image.Width}x{image.Height}; target texture is {w}x{h}. It will be resized on replace.";
            }

            if (image.Width <= 0 || image.Height <= 0)
                return ImageValidationResult.Fail("Image has invalid dimensions.");

            return ImageValidationResult.Ok(image.Width, image.Height, warning);
        }
        catch (Exception ex)
        {
            return ImageValidationResult.Fail($"Could not read image: {ex.Message}");
        }
    }

    /// <summary>
    /// Prepares Unity Texture2D RGBA32 pixel bytes (vertically flipped).
    /// Matches the Floowandereeze/UnityPy RGBA32 replacement approach.
    /// </summary>
    public static byte[] PrepareRgba32TextureBytes(string imagePath, int width, int height)
    {
        using var image = Image.Load<Rgba32>(imagePath);
        if (image.Width != width || image.Height != height)
        {
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3
            }));
        }

        image.Mutate(ctx => ctx.Flip(FlipMode.Vertical));
        var bytes = new byte[width * height * 4];
        image.CopyPixelDataTo(bytes);
        return bytes;
    }

    public static void SavePng(byte[] bgraOrRgbaPixels, int width, int height, string outputPath, bool inputIsBgra = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (inputIsBgra)
        {
            using var image = Image.LoadPixelData<Bgra32>(bgraOrRgbaPixels, width, height);
            image.Mutate(ctx => ctx.Flip(FlipMode.Vertical));
            image.Save(outputPath, new PngEncoder());
        }
        else
        {
            using var image = Image.LoadPixelData<Rgba32>(bgraOrRgbaPixels, width, height);
            image.Mutate(ctx => ctx.Flip(FlipMode.Vertical));
            image.Save(outputPath, new PngEncoder());
        }
    }

    public static string Slugify(string value)
    {
        var normalized = value.ToLowerInvariant();
        var chars = normalized.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-', '_');
    }
}

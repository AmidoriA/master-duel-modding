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
            if (image.Width <= 0 || image.Height <= 0)
                return ImageValidationResult.Fail("Image has invalid dimensions.");

            string? warning = null;
            string? info = null;
            var sourceKind = CardArtTextureSizes.Classify(image.Width, image.Height);

            if (sourceKind == CardArtSizeKind.Pendulum)
            {
                info = CardArtTextureSizes.IsPendulum(image.Width, image.Height)
                    ? "Accepted as Pendulum art (512×683, 3:4)."
                    : CardArtTextureSizes.IsPendulumNativeCanvas(image.Width, image.Height)
                        ? "Accepted as Pendulum native Texture2D canvas (512×1024)."
                        : $"Accepted as Pendulum 3:4 aspect ({image.Width}×{image.Height}).";
            }
            else if (CardArtTextureSizes.IsNormal(image.Width, image.Height))
            {
                info = "Accepted as normal illust (512×512).";
            }

            if (expectedWidth is int w && expectedHeight is int h && (image.Width != w || image.Height != h))
            {
                var targetDesc = CardArtTextureSizes.Describe(w, h);
                var sourceDesc = CardArtTextureSizes.Describe(image.Width, image.Height);
                if (CardArtTextureSizes.SameAspect(image.Width, image.Height, w, h))
                {
                    warning =
                        $"Image is {sourceDesc}; target texture is {targetDesc}. It will be scaled to match.";
                }
                else
                {
                    warning =
                        $"Image is {sourceDesc}; target texture is {targetDesc}. " +
                        "It will be letterboxed (aspect preserved) — not stretched.";
                }
            }

            return ImageValidationResult.Ok(image.Width, image.Height, warning, info);
        }
        catch (Exception ex)
        {
            return ImageValidationResult.Fail($"Could not read image: {ex.Message}");
        }
    }

    /// <summary>
    /// Prepares Unity Texture2D RGBA32 pixel bytes (vertically flipped).
    /// Matches the Floowandereeze/UnityPy RGBA32 replacement approach.
    /// When <paramref name="preserveAspect"/> is true (card-art default), mismatched
    /// aspect ratios are letterboxed instead of squashed — important for Pendulum 3:4 art.
    /// </summary>
    public static byte[] PrepareRgba32TextureBytes(
        string imagePath,
        int width,
        int height,
        bool preserveAspect = false)
    {
        using var image = Image.Load<Rgba32>(imagePath);
        if (image.Width != width || image.Height != height)
        {
            var mode = preserveAspect && !CardArtTextureSizes.SameAspect(image.Width, image.Height, width, height)
                ? ResizeMode.Pad
                : ResizeMode.Stretch;

            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = mode,
                Sampler = KnownResamplers.Lanczos3,
                PadColor = Color.Transparent
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

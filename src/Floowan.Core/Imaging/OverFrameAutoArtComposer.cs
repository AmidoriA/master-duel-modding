using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Floowan.Core.Imaging;

/// <summary>
/// Converts a source image plus a subject mask into a transparent 704×1024
/// over-frame canvas. The visible subject is trimmed, enlarged while preserving
/// aspect ratio, and centered with a small safe margin.
/// </summary>
public static class OverFrameAutoArtComposer
{
    public const byte VisibleAlphaThreshold = 12;
    private const float CanvasFill = 0.92f;

    public static Image<Rgba32> Compose(Image<Rgba32> source, Image<L8> mask)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);

        using var alignedMask = mask.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = source.Size,
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
        using var cutout = source.Clone();

        for (var y = 0; y < cutout.Height; y++)
        {
            var pixels = cutout.DangerousGetPixelRowMemory(y).Span;
            var maskPixels = alignedMask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < pixels.Length; x++)
            {
                var pixel = pixels[x];
                pixel.A = (byte)(pixel.A * maskPixels[x].PackedValue / 255);
                pixels[x] = pixel;
            }
        }

        var bounds = FindVisibleBounds(cutout);
        if (bounds.IsEmpty)
            throw new InvalidOperationException("Background removal did not find a visible subject.");

        using var subject = cutout.Clone(ctx => ctx.Crop(bounds));
        var maxWidth = (int)MathF.Round(Assets.OverFrameConstants.Width * CanvasFill);
        var maxHeight = (int)MathF.Round(Assets.OverFrameConstants.Height * CanvasFill);
        var scale = Math.Min(maxWidth / (float)subject.Width, maxHeight / (float)subject.Height);
        var targetWidth = Math.Max(1, (int)MathF.Round(subject.Width * scale));
        var targetHeight = Math.Max(1, (int)MathF.Round(subject.Height * scale));

        using var resized = subject.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(targetWidth, targetHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        var canvas = new Image<Rgba32>(
            Assets.OverFrameConstants.Width,
            Assets.OverFrameConstants.Height,
            Color.Transparent);
        var xOffset = (canvas.Width - resized.Width) / 2;
        var yOffset = (canvas.Height - resized.Height) / 2;
        canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(xOffset, yOffset), 1f));
        return canvas;
    }

    private static Rectangle FindVisibleBounds(Image<Rgba32> image)
    {
        var left = image.Width;
        var top = image.Height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < image.Height; y++)
        {
            var pixels = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < pixels.Length; x++)
            {
                if (pixels[x].A <= VisibleAlphaThreshold)
                    continue;

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left || bottom < top
            ? Rectangle.Empty
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
}

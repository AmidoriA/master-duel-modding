using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Softens hard subject silhouettes with a subtle inward alpha ramp at the boundary.
/// Applied after subject scale so the fade is ~1–2px in final OF texture space without
/// shifting crop/placement (no exterior bloom).
/// </summary>
public static class SubjectMaskFeather
{
    /// <summary>
    /// Edge fade distance in pixels (user-chosen subtle range: 1–2px).
    /// </summary>
    public const float DefaultRadiusPx = 1.5f;

    /// <summary>
    /// Returns a new L8 mask: pixels at/above <paramref name="keepThreshold"/> are treated
    /// as interior, then alpha ramps from 0 at the silhouette edge to 255 by
    /// <paramref name="radiusPx"/> inward. Exterior stays 0 (no outward bloom).
    /// </summary>
    public static Image<L8> Apply(
        Image<L8> mask,
        float radiusPx = DefaultRadiusPx,
        byte keepThreshold = OverFrameAutoArtComposer.MaskKeepThreshold)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (radiusPx <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radiusPx), "Feather radius must be positive.");

        var result = new Image<L8>(mask.Width, mask.Height);
        ApplyInto(mask, result, radiusPx, keepThreshold);
        return result;
    }

    /// <summary>
    /// Replaces <paramref name="mask"/> values with a subtle inward edge feather (same rules
    /// as <see cref="Apply"/>).
    /// </summary>
    public static void ApplyInPlace(
        Image<L8> mask,
        float radiusPx = DefaultRadiusPx,
        byte keepThreshold = OverFrameAutoArtComposer.MaskKeepThreshold)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (radiusPx <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radiusPx), "Feather radius must be positive.");

        using var temp = new Image<L8>(mask.Width, mask.Height);
        ApplyInto(mask, temp, radiusPx, keepThreshold);
        for (var y = 0; y < mask.Height; y++)
        {
            var dst = mask.DangerousGetPixelRowMemory(y).Span;
            var src = temp.DangerousGetPixelRowMemory(y).Span;
            src.CopyTo(dst);
        }
    }

    /// <summary>
    /// Softens an RGBA cutout's alpha in place using the same inward distance feather
    /// as <see cref="Apply"/>. Intended for already-scaled subject layers so the fade is
    /// ~1–2px in final texture space.
    /// </summary>
    public static void ApplyToRgbaAlphaInPlace(
        Image<Rgba32> cutout,
        float radiusPx = DefaultRadiusPx,
        byte keepThreshold = OverFrameAutoArtComposer.MaskKeepThreshold)
    {
        ArgumentNullException.ThrowIfNull(cutout);
        if (radiusPx <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radiusPx), "Feather radius must be positive.");

        using var mask = new Image<L8>(cutout.Width, cutout.Height);
        for (var y = 0; y < cutout.Height; y++)
        {
            var src = cutout.DangerousGetPixelRowMemory(y).Span;
            var dst = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < src.Length; x++)
                dst[x] = new L8(src[x].A);
        }

        ApplyInPlace(mask, radiusPx, keepThreshold);
        for (var y = 0; y < cutout.Height; y++)
        {
            var pixels = cutout.DangerousGetPixelRowMemory(y).Span;
            var soft = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < pixels.Length; x++)
            {
                ref var px = ref pixels[x];
                px.A = soft[x].PackedValue;
            }
        }
    }

    private static void ApplyInto(Image<L8> source, Image<L8> dest, float radiusPx, byte keepThreshold)
    {
        var w = source.Width;
        var h = source.Height;
        var inside = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = source.DangerousGetPixelRowMemory(y).Span;
            var rowOffset = y * w;
            for (var x = 0; x < w; x++)
                inside[rowOffset + x] = row[x].PackedValue >= keepThreshold;
        }

        // Search a bit past radius so Euclidean distance to the opposite side is accurate.
        var search = (int)MathF.Ceiling(radiusPx) + 1;
        var radiusSq = radiusPx * radiusPx;

        for (var y = 0; y < h; y++)
        {
            var destRow = dest.DangerousGetPixelRowMemory(y).Span;
            var rowOffset = y * w;
            for (var x = 0; x < w; x++)
            {
                if (!inside[rowOffset + x])
                {
                    destRow[x] = default;
                    continue;
                }

                var distSq = MinDistSqToOutside(inside, w, h, x, y, search);
                if (distSq >= radiusSq)
                {
                    destRow[x] = new L8(255);
                    continue;
                }

                var dist = MathF.Sqrt(distSq);
                var t = SmoothStep(0f, radiusPx, dist);
                destRow[x] = new L8((byte)Math.Clamp((int)MathF.Round(t * 255f), 0, 255));
            }
        }
    }

    /// <summary>
    /// Minimum squared Euclidean distance from (x,y) to a pixel that is outside the
    /// subject (or out of image bounds). Caps the search at <paramref name="search"/>;
    /// when no outside pixel is found in-range, returns a value >= search^2 (treated as deep).
    /// </summary>
    private static float MinDistSqToOutside(bool[] inside, int w, int h, int x, int y, int search)
    {
        var best = (float)((search + 1) * (search + 1));
        for (var dy = -search; dy <= search; dy++)
        {
            var ny = y + dy;
            for (var dx = -search; dx <= search; dx++)
            {
                var nx = x + dx;
                var outside = (uint)nx >= (uint)w
                    || (uint)ny >= (uint)h
                    || !inside[ny * w + nx];
                if (!outside)
                    continue;

                var dSq = (float)(dx * dx + dy * dy);
                if (dSq < best)
                    best = dSq;
            }
        }

        return best;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
            return x >= edge1 ? 1f : 0f;

        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

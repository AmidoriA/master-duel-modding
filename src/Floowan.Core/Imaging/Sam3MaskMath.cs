namespace Floowan.Core.Imaging;

/// <summary>
/// Pure mask helpers for <see cref="Sam3CutoutService"/> (row-major bool / byte buffers).
/// Kept separate so they can be unit tested without the ONNX models.
/// </summary>
public static class Sam3MaskMath
{
    /// <summary>Trimap value for "certainly subject".</summary>
    public const byte TrimapForeground = 255;

    /// <summary>Trimap value for "let ViTMatte decide".</summary>
    public const byte TrimapUnknown = 128;

    /// <summary>
    /// Bilinearly upsamples one <paramref name="gridSize"/>² logit plane (starting at
    /// <paramref name="offset"/>) to <paramref name="width"/>×<paramref name="height"/> with
    /// half-pixel centres, and returns where the logit is above zero (probability above 0.5).
    /// </summary>
    public static bool[] UpsampleLogitsToMask(
        ReadOnlySpan<float> logits,
        int offset,
        int gridSize,
        int width,
        int height)
    {
        var x0 = new int[width];
        var x1 = new int[width];
        var wx = new float[width];
        FillAxis(gridSize, width, x0, x1, wx);
        var y0 = new int[height];
        var y1 = new int[height];
        var wy = new float[height];
        FillAxis(gridSize, height, y0, y1, wy);

        var mask = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            var rowA = offset + y0[y] * gridSize;
            var rowB = offset + y1[y] * gridSize;
            var fy = wy[y];
            for (var x = 0; x < width; x++)
            {
                var a = logits[rowA + x0[x]] + (logits[rowA + x1[x]] - logits[rowA + x0[x]]) * wx[x];
                var b = logits[rowB + x0[x]] + (logits[rowB + x1[x]] - logits[rowB + x0[x]]) * wx[x];
                mask[y * width + x] = a + (b - a) * fy > 0f;
            }
        }

        return mask;
    }

    private static void FillAxis(int source, int target, int[] i0, int[] i1, float[] weight)
    {
        var scale = source / (float)target;
        for (var i = 0; i < target; i++)
        {
            var s = (i + 0.5f) * scale - 0.5f;
            if (s <= 0f)
            {
                i0[i] = 0;
                i1[i] = 0;
                weight[i] = 0f;
                continue;
            }

            var f = (int)MathF.Floor(s);
            if (f >= source - 1)
            {
                i0[i] = source - 1;
                i1[i] = source - 1;
                weight[i] = 0f;
                continue;
            }

            i0[i] = f;
            i1[i] = f + 1;
            weight[i] = s - f;
        }
    }

    /// <summary>Square (Chebyshev) dilation by <paramref name="radius"/> px; the window is clipped at the borders.</summary>
    public static bool[] Dilate(bool[] mask, int width, int height, int radius)
    {
        if (radius <= 0)
            return (bool[])mask.Clone();
        var horizontal = new bool[mask.Length];
        var prefix = new int[Math.Max(width, height) + 1];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
                prefix[x + 1] = prefix[x] + (mask[row + x] ? 1 : 0);
            for (var x = 0; x < width; x++)
            {
                var lo = Math.Max(0, x - radius);
                var hi = Math.Min(width - 1, x + radius);
                horizontal[row + x] = prefix[hi + 1] - prefix[lo] > 0;
            }
        }

        var result = new bool[mask.Length];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
                prefix[y + 1] = prefix[y] + (horizontal[y * width + x] ? 1 : 0);
            for (var y = 0; y < height; y++)
            {
                var lo = Math.Max(0, y - radius);
                var hi = Math.Min(height - 1, y + radius);
                result[y * width + x] = prefix[hi + 1] - prefix[lo] > 0;
            }
        }

        return result;
    }

    /// <summary>Square erosion by <paramref name="radius"/> px (complement of <see cref="Dilate"/>).</summary>
    public static bool[] Erode(bool[] mask, int width, int height, int radius)
    {
        var inverted = new bool[mask.Length];
        for (var i = 0; i < mask.Length; i++)
            inverted[i] = !mask[i];
        var grown = Dilate(inverted, width, height, radius);
        for (var i = 0; i < grown.Length; i++)
            grown[i] = !grown[i];
        return grown;
    }

    /// <summary>
    /// Trimap for ViTMatte: <see cref="TrimapForeground"/> inside the mask eroded by
    /// <paramref name="band"/>, <see cref="TrimapUnknown"/> within <paramref name="band"/> px of
    /// the edge on either side, 0 elsewhere.
    /// </summary>
    public static byte[] BuildTrimap(bool[] mask, int width, int height, int band)
    {
        var inner = Erode(mask, width, height, band);
        var outer = Dilate(mask, width, height, band);
        var trimap = new byte[mask.Length];
        for (var i = 0; i < trimap.Length; i++)
            trimap[i] = inner[i] ? TrimapForeground : outer[i] ? TrimapUnknown : (byte)0;
        return trimap;
    }

    /// <summary>
    /// Whether a held-item detection (weapon, wings, …) belongs to the subject: it must touch
    /// <paramref name="nearSubject"/> (the subject grown a few px) with at least 2 % of its own area,
    /// and add at least 0.2 % of the image that the subject mask does not already cover.
    /// </summary>
    public static bool IsAttachedHeldItem(bool[] item, bool[] subject, bool[] nearSubject)
    {
        long area = 0, touching = 0, added = 0;
        for (var i = 0; i < item.Length; i++)
        {
            if (!item[i])
                continue;
            area++;
            if (nearSubject[i])
                touching++;
            if (!subject[i])
                added++;
        }

        return area > 0 && touching > 0.02 * area && added > 0.002 * item.Length;
    }

    public static bool Any(bool[] mask)
    {
        foreach (var value in mask)
        {
            if (value)
                return true;
        }

        return false;
    }

    public static void UnionInto(bool[] target, bool[] addition)
    {
        for (var i = 0; i < target.Length; i++)
            target[i] |= addition[i];
    }
}

using Floowan.Core.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Redraws Master Duel Link arrow markers as the topmost OF layer so subject/frame
/// punches cannot erase them. Active directions take the <em>inner triangle glyph</em>
/// only from <c>Link.png</c> (largest near-black connected component in each crop) and
/// recolor it lit orange/red — the metallic L-bevel / outer triangular housing stays
/// as painted by the frame (Decode Talker Integration OF look).
/// </summary>
public static class LinkArrowOverlay
{
    /// <summary>
    /// Crop rectangles on the 704×1024 Link frame for each direction (measured from
    /// dark arrow pixels on <c>card_frame18</c> / <c>Link.png</c>, padded for AA).
    /// </summary>
    private static readonly (LinkMarkerMask Bit, Rectangle Crop)[] ArrowCrops =
    [
        (LinkMarkerMask.UpLeft, new Rectangle(56, 157, 93, 97)),
        (LinkMarkerMask.Up, new Rectangle(296, 147, 113, 47)),
        (LinkMarkerMask.UpRight, new Rectangle(551, 156, 100, 98)),
        (LinkMarkerMask.Left, new Rectangle(45, 396, 46, 118)),
        (LinkMarkerMask.Right, new Rectangle(613, 396, 48, 118)),
        (LinkMarkerMask.DownLeft, new Rectangle(41, 656, 108, 107)),
        (LinkMarkerMask.Down, new Rectangle(296, 715, 113, 48)),
        (LinkMarkerMask.DownRight, new Rectangle(551, 656, 108, 107)),
    ];

    /// <summary>
    /// Near-black fill of the inactive triangle glyph (and thin bevel outlines).
    /// Metallic silver bevel sits above this (~lum 50–110) and is never recolored.
    /// </summary>
    private const int GlyphLuminanceMax = 32;

    // Decode Talker Integration OF: bright yellow-orange center → deep red-orange edge.
    private const byte ActiveCenterR = 255;
    private const byte ActiveCenterG = 220;
    private const byte ActiveCenterB = 48;
    private const byte ActiveEdgeR = 255;
    private const byte ActiveEdgeG = 72;
    private const byte ActiveEdgeB = 18;

    /// <summary>
    /// True when OF compose should redraw Link arrows for this frame style.
    /// Floowan has no separate Link Pendulum template today — only <see cref="CardFrameStyle.Link"/>.
    /// </summary>
    public static bool NeedsArrowOverlay(CardFrameStyle frameStyle) =>
        frameStyle == CardFrameStyle.Link;

    /// <summary>
    /// Blits lit (orange/red) active <em>triangle glyphs</em> from the Link frame template
    /// onto <paramref name="canvas"/> (must be 704×1024). No-op when
    /// <paramref name="markers"/> is <see cref="LinkMarkerMask.None"/>.
    /// </summary>
    public static void Apply(
        Image<Rgba32> canvas,
        LinkMarkerMask markers,
        string? frameDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (markers == LinkMarkerMask.None)
            return;
        if (canvas.Width != Assets.OverFrameConstants.Width
            || canvas.Height != Assets.OverFrameConstants.Height)
        {
            throw new ArgumentException(
                $"Link arrow overlay expects {Assets.OverFrameConstants.Width}×{Assets.OverFrameConstants.Height}, " +
                $"got {canvas.Width}×{canvas.Height}.",
                nameof(canvas));
        }

        using var linkFrame = CardFrameTemplates.Load(CardFrameStyle.Link, frameDirectory);
        ApplyFromTemplate(canvas, linkFrame, markers);
    }

    /// <summary>Same as <see cref="Apply"/> but reuses an already-loaded Link template.</summary>
    public static void ApplyFromTemplate(
        Image<Rgba32> canvas,
        Image<Rgba32> linkTemplate,
        LinkMarkerMask markers)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(linkTemplate);
        if (markers == LinkMarkerMask.None)
            return;

        foreach (var (bit, crop) in ArrowCrops)
        {
            if ((markers & bit) == 0)
                continue;
            BlitLitTriangleGlyph(canvas, linkTemplate, crop);
        }
    }

    /// <summary>
    /// Isolates the filled triangle glyph (largest near-black 4-connected component in
    /// <paramref name="crop"/>), then paints a center-bright orange/red gradient.
    /// Thin near-black bevel outline strokes are discarded as smaller components.
    /// </summary>
    private static void BlitLitTriangleGlyph(Image<Rgba32> dst, Image<Rgba32> src, Rectangle crop)
    {
        var x0 = Math.Clamp(crop.X, 0, src.Width - 1);
        var y0 = Math.Clamp(crop.Y, 0, src.Height - 1);
        var x1 = Math.Clamp(crop.Right, 0, src.Width);
        var y1 = Math.Clamp(crop.Bottom, 0, src.Height);
        var w = x1 - x0;
        var h = y1 - y0;
        if (w <= 0 || h <= 0)
            return;

        var dark = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = src.DangerousGetPixelRowMemory(y0 + y).Span;
            var rowOff = y * w;
            for (var x = 0; x < w; x++)
            {
                if (IsGlyphCandidate(row[x0 + x]))
                    dark[rowOff + x] = true;
            }
        }

        if (!TryLargestComponent(dark, w, h, out var glyph, out var cx, out var cy, out var maxDistSq))
            return;

        if (maxDistSq < 1f)
            maxDistSq = 1f;

        foreach (var (lx, ly) in glyph)
        {
            var c = src[x0 + lx, y0 + ly];
            var dx = lx - cx;
            var dy = ly - cy;
            var edgeT = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) / MathF.Sqrt(maxDistSq), 0f, 1f);
            dst[x0 + lx, y0 + ly] = AlphaOver(dst[x0 + lx, y0 + ly], ToLitArrowPixel(c, edgeT));
        }
    }

    /// <summary>
    /// Near-black inactive triangle fill (and thin bevel outlines). Metallic bevel
    /// mid-tones are excluded by <see cref="GlyphLuminanceMax"/>.
    /// </summary>
    public static bool IsGlyphCandidate(Rgba32 c)
    {
        if (c.A < 20)
            return false;
        var lum = (c.R + c.G + c.B) / 3;
        return lum <= GlyphLuminanceMax;
    }

    /// <summary>
    /// Maps an inactive glyph sample to lit orange/red.
    /// <paramref name="edgeT"/> 0 = triangle center (bright yellow-orange),
    /// 1 = silhouette edge (deep red-orange), matching Decode Talker Integration OF.
    /// </summary>
    public static Rgba32 ToLitArrowPixel(Rgba32 dark, float edgeT = 0.55f)
    {
        edgeT = Math.Clamp(edgeT, 0f, 1f);
        // Slight luminance nudge so AA fringe softens toward the edge color.
        var lum = (dark.R + dark.G + dark.B) / 3f;
        var lumT = Math.Clamp(lum / GlyphLuminanceMax, 0f, 1f);
        var t = Math.Clamp(edgeT * 0.85f + lumT * 0.15f, 0f, 1f);
        var r = (byte)Math.Clamp(MathF.Round(Lerp(ActiveCenterR, ActiveEdgeR, t)), 0, 255);
        var g = (byte)Math.Clamp(MathF.Round(Lerp(ActiveCenterG, ActiveEdgeG, t)), 0, 255);
        var b = (byte)Math.Clamp(MathF.Round(Lerp(ActiveCenterB, ActiveEdgeB, t)), 0, 255);
        return new Rgba32(r, g, b, dark.A);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// 4-connected flood fill; returns the largest near-black blob (filled triangle).
    /// </summary>
    private static bool TryLargestComponent(
        bool[] dark,
        int w,
        int h,
        out List<(int X, int Y)> component,
        out float centroidX,
        out float centroidY,
        out float maxDistSq)
    {
        component = [];
        centroidX = centroidY = maxDistSq = 0f;
        var seen = new bool[dark.Length];
        List<(int X, int Y)>? best = null;

        for (var sy = 0; sy < h; sy++)
        for (var sx = 0; sx < w; sx++)
        {
            var start = sy * w + sx;
            if (!dark[start] || seen[start])
                continue;

            var stack = new Stack<(int X, int Y)>();
            var comp = new List<(int X, int Y)>();
            stack.Push((sx, sy));
            seen[start] = true;
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                comp.Add((x, y));
                TryPush(x + 1, y);
                TryPush(x - 1, y);
                TryPush(x, y + 1);
                TryPush(x, y - 1);

                void TryPush(int nx, int ny)
                {
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h)
                        return;
                    var i = ny * w + nx;
                    if (!dark[i] || seen[i])
                        return;
                    seen[i] = true;
                    stack.Push((nx, ny));
                }
            }

            if (best is null || comp.Count > best.Count)
                best = comp;
        }

        if (best is null || best.Count == 0)
            return false;

        component = best;
        double sxSum = 0, sySum = 0;
        foreach (var (x, y) in best)
        {
            sxSum += x;
            sySum += y;
        }

        centroidX = (float)(sxSum / best.Count);
        centroidY = (float)(sySum / best.Count);
        foreach (var (x, y) in best)
        {
            var dx = x - centroidX;
            var dy = y - centroidY;
            var d = dx * dx + dy * dy;
            if (d > maxDistSq)
                maxDistSq = d;
        }

        return true;
    }

    private static Rgba32 AlphaOver(Rgba32 under, Rgba32 over)
    {
        if (over.A >= 250)
            return over;
        if (over.A == 0)
            return under;

        var oa = over.A / 255f;
        var ua = under.A / 255f;
        var outA = oa + ua * (1f - oa);
        if (outA <= 0f)
            return default;

        byte Mix(byte o, byte u) =>
            (byte)Math.Clamp(MathF.Round((o * oa + u * ua * (1f - oa)) / outA), 0, 255);

        return new Rgba32(Mix(over.R, under.R), Mix(over.G, under.G), Mix(over.B, under.B),
            (byte)Math.Clamp(MathF.Round(outA * 255f), 0, 255));
    }
}

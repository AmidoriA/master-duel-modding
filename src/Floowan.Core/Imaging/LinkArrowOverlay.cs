using Floowan.Core.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Redraws Master Duel Link arrow markers as the topmost OF layer so subject/frame
/// punches cannot erase them. <c>Link.png</c> only has inactive flat triangles, so
/// active directions synthesize the MD active housing:
/// metallic silver triangular bevel → thin black inset → orange/red glow fill
/// (Decode Talker Integration / A Bao A Qu look). Inactive directions stay as the
/// flat dark glyphs already on the frame.
/// </summary>
public static class LinkArrowOverlay
{
    /// <summary>
    /// Crop rectangles on the 704×1024 Link frame for each direction (measured from
    /// dark arrow pixels on <c>card_frame18</c> / <c>Link.png</c>, padded for AA and
    /// for the synthesized metallic bevel ring).
    /// </summary>
    private static readonly (LinkMarkerMask Bit, Rectangle Crop)[] ArrowCrops =
    [
        (LinkMarkerMask.UpLeft, new Rectangle(50, 150, 105, 110)),
        (LinkMarkerMask.Up, new Rectangle(290, 140, 125, 58)),
        (LinkMarkerMask.UpRight, new Rectangle(545, 149, 112, 111)),
        (LinkMarkerMask.Left, new Rectangle(38, 390, 58, 130)),
        (LinkMarkerMask.Right, new Rectangle(606, 390, 60, 130)),
        (LinkMarkerMask.DownLeft, new Rectangle(34, 650, 120, 118)),
        (LinkMarkerMask.Down, new Rectangle(290, 708, 125, 60)),
        (LinkMarkerMask.DownRight, new Rectangle(544, 650, 120, 118)),
    ];

    /// <summary>
    /// Near-black fill of the inactive triangle glyph (and thin bevel outlines).
    /// Used only to locate the glyph; metallic mid-tones are not candidates.
    /// </summary>
    private const int GlyphLuminanceMax = 32;

    /// <summary>Disk radius (px) of the black inset between metal and orange fill.</summary>
    private const int BlackMarginRadius = 2;

    /// <summary>Disk radius (px) of the outer metallic triangular housing.</summary>
    private const int MetalBevelRadius = 6;

    // Decode Talker Integration OF: bright yellow-orange center → deep red-orange edge.
    private const byte ActiveCenterR = 255;
    private const byte ActiveCenterG = 220;
    private const byte ActiveCenterB = 48;
    private const byte ActiveEdgeR = 255;
    private const byte ActiveEdgeG = 72;
    private const byte ActiveEdgeB = 18;

    // Raised silver housing (highlight on outer rim, cooler mid on inner).
    private const byte MetalOuterR = 210;
    private const byte MetalOuterG = 214;
    private const byte MetalOuterB = 222;
    private const byte MetalInnerR = 130;
    private const byte MetalInnerG = 136;
    private const byte MetalInnerB = 148;

    private static readonly Rgba32 BlackInset = new(8, 8, 12, 255);

    /// <summary>
    /// True when OF compose should redraw Link arrows for this frame style.
    /// Floowan has no separate Link Pendulum template today — only <see cref="CardFrameStyle.Link"/>.
    /// </summary>
    public static bool NeedsArrowOverlay(CardFrameStyle frameStyle) =>
        frameStyle == CardFrameStyle.Link;

    /// <summary>
    /// Composites active Link arrows (metallic bevel + black inset + lit fill) onto
    /// <paramref name="canvas"/> (must be 704×1024). No-op when
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
            BlitActiveArrow(canvas, linkTemplate, crop);
        }
    }

    /// <summary>
    /// Locates the inactive triangle glyph, synthesizes metallic bevel + black inset
    /// rings by disk dilation, then paints orange fill. Draw order: metal → black → glow.
    /// </summary>
    private static void BlitActiveArrow(Image<Rgba32> dst, Image<Rgba32> src, Rectangle crop)
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

        var glyphMask = new bool[w * h];
        foreach (var (lx, ly) in glyph)
            glyphMask[ly * w + lx] = true;

        var blackRing = DilateDisk(glyphMask, w, h, BlackMarginRadius);
        var metalOuter = DilateDisk(glyphMask, w, h, MetalBevelRadius);

        // Metal = outer dilate − black dilate; black inset = black dilate − glyph.
        for (var i = 0; i < metalOuter.Length; i++)
        {
            if (!metalOuter[i] || blackRing[i])
                continue;
            var lx = i % w;
            var ly = i / w;
            var dist = MinDistanceToMask(lx, ly, glyphMask, w, h, MetalBevelRadius + 1);
            var t = Math.Clamp(
                (dist - BlackMarginRadius) / (float)(MetalBevelRadius - BlackMarginRadius),
                0f,
                1f);
            var metal = ToMetalPixel(t);
            dst[x0 + lx, y0 + ly] = AlphaOver(dst[x0 + lx, y0 + ly], metal);
        }

        for (var i = 0; i < blackRing.Length; i++)
        {
            if (!blackRing[i] || glyphMask[i])
                continue;
            var lx = i % w;
            var ly = i / w;
            dst[x0 + lx, y0 + ly] = AlphaOver(dst[x0 + lx, y0 + ly], BlackInset);
        }

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
    /// 1 = silhouette edge (deep red-orange).
    /// </summary>
    public static Rgba32 ToLitArrowPixel(Rgba32 dark, float edgeT = 0.55f)
    {
        edgeT = Math.Clamp(edgeT, 0f, 1f);
        var lum = (dark.R + dark.G + dark.B) / 3f;
        var lumT = Math.Clamp(lum / GlyphLuminanceMax, 0f, 1f);
        var t = Math.Clamp(edgeT * 0.85f + lumT * 0.15f, 0f, 1f);
        var r = (byte)Math.Clamp(MathF.Round(Lerp(ActiveCenterR, ActiveEdgeR, t)), 0, 255);
        var g = (byte)Math.Clamp(MathF.Round(Lerp(ActiveCenterG, ActiveEdgeG, t)), 0, 255);
        var b = (byte)Math.Clamp(MathF.Round(Lerp(ActiveCenterB, ActiveEdgeB, t)), 0, 255);
        return new Rgba32(r, g, b, dark.A);
    }

    /// <summary>
    /// Silver housing shade. <paramref name="outerT"/> 0 = inner (near black inset),
    /// 1 = outer rim highlight.
    /// </summary>
    public static Rgba32 ToMetalPixel(float outerT)
    {
        outerT = Math.Clamp(outerT, 0f, 1f);
        var r = (byte)Math.Clamp(MathF.Round(Lerp(MetalInnerR, MetalOuterR, outerT)), 0, 255);
        var g = (byte)Math.Clamp(MathF.Round(Lerp(MetalInnerG, MetalOuterG, outerT)), 0, 255);
        var b = (byte)Math.Clamp(MathF.Round(Lerp(MetalInnerB, MetalOuterB, outerT)), 0, 255);
        return new Rgba32(r, g, b, 255);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static bool[] DilateDisk(bool[] mask, int w, int h, int radius)
    {
        var r2 = radius * radius;
        var result = new bool[mask.Length];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!mask[y * w + x])
                continue;
            var y0 = Math.Max(0, y - radius);
            var y1 = Math.Min(h - 1, y + radius);
            var x0 = Math.Max(0, x - radius);
            var x1 = Math.Min(w - 1, x + radius);
            for (var yy = y0; yy <= y1; yy++)
            for (var xx = x0; xx <= x1; xx++)
            {
                var dx = xx - x;
                var dy = yy - y;
                if (dx * dx + dy * dy <= r2)
                    result[yy * w + xx] = true;
            }
        }

        return result;
    }

    private static float MinDistanceToMask(int x, int y, bool[] mask, int w, int h, int searchRadius)
    {
        var best = (float)(searchRadius + 1);
        var y0 = Math.Max(0, y - searchRadius);
        var y1 = Math.Min(h - 1, y + searchRadius);
        var x0 = Math.Max(0, x - searchRadius);
        var x1 = Math.Min(w - 1, x + searchRadius);
        for (var yy = y0; yy <= y1; yy++)
        for (var xx = x0; xx <= x1; xx++)
        {
            if (!mask[yy * w + xx])
                continue;
            var dx = xx - x;
            var dy = yy - y;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < best)
                best = d;
        }

        return best;
    }

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

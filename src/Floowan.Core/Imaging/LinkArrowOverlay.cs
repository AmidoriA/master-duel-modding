using Floowan.Core.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Redraws Master Duel Link arrow markers as the topmost OF layer so subject/frame
/// punches cannot erase them. <c>Link.png</c> only has inactive flat triangles, so
/// active directions synthesize a glanceable MD-style marker:
/// soft dark drop halo → bright silver/white rim → thin black inset → orange/red fill
/// (Cyberse Witch / Decode Talker Integration look). Inactive directions stay as the
/// flat dark glyphs already on the frame.
/// </summary>
public static class LinkArrowOverlay
{
    /// <summary>
    /// Crop rectangles on the 704×1024 Link frame for each direction (measured from
    /// dark arrow pixels on <c>card_frame18</c> / <c>Link.png</c>, padded for AA,
    /// bright rim, and drop-shadow halo).
    /// </summary>
    private static readonly (LinkMarkerMask Bit, Rectangle Crop)[] ArrowCrops =
    [
        (LinkMarkerMask.UpLeft, new Rectangle(42, 142, 120, 125)),
        (LinkMarkerMask.Up, new Rectangle(282, 132, 140, 72)),
        (LinkMarkerMask.UpRight, new Rectangle(537, 141, 125, 125)),
        (LinkMarkerMask.Left, new Rectangle(30, 382, 72, 145)),
        (LinkMarkerMask.Right, new Rectangle(598, 382, 74, 145)),
        (LinkMarkerMask.DownLeft, new Rectangle(26, 642, 135, 132)),
        (LinkMarkerMask.Down, new Rectangle(282, 700, 140, 74)),
        (LinkMarkerMask.DownRight, new Rectangle(536, 642, 135, 132)),
    ];

    /// <summary>
    /// Near-black fill of the inactive triangle glyph (and thin bevel outlines).
    /// Used only to locate the glyph; metallic mid-tones are not candidates.
    /// </summary>
    private const int GlyphLuminanceMax = 32;

    /// <summary>Disk radius (px) of the black inset between rim and orange fill.</summary>
    private const int BlackMarginRadius = 2;

    /// <summary>Disk radius (px) of the bright silver/white triangular rim.</summary>
    private const int MetalBevelRadius = 8;

    /// <summary>
    /// Extra disk radius beyond the rim for the soft dark drop halo
    /// (total shadow extent = <see cref="MetalBevelRadius"/> + this).
    /// </summary>
    private const int ShadowHaloRadius = 5;

    /// <summary>Peak alpha at the shadow’s inner edge (against the rim).</summary>
    private const byte ShadowMaxAlpha = 180;

    // Bright red-orange fill (Cyberse Witch glanceability).
    private const byte ActiveCenterR = 255;
    private const byte ActiveCenterG = 210;
    private const byte ActiveCenterB = 40;
    private const byte ActiveEdgeR = 255;
    private const byte ActiveEdgeG = 55;
    private const byte ActiveEdgeB = 12;

    // Crisp bright silver/white rim (outer nearly white, inner still light silver).
    private const byte MetalOuterR = 255;
    private const byte MetalOuterG = 255;
    private const byte MetalOuterB = 255;
    private const byte MetalInnerR = 198;
    private const byte MetalInnerG = 204;
    private const byte MetalInnerB = 214;

    private static readonly Rgba32 BlackInset = new(6, 6, 10, 255);

    /// <summary>
    /// True when OF compose should redraw Link arrows for this frame style.
    /// Floowan has no separate Link Pendulum template today — only <see cref="CardFrameStyle.Link"/>.
    /// </summary>
    public static bool NeedsArrowOverlay(CardFrameStyle frameStyle) =>
        frameStyle == CardFrameStyle.Link;

    /// <summary>
    /// Composites active Link arrows (drop shadow + bright rim + black inset + lit fill)
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
            BlitActiveArrow(canvas, linkTemplate, crop);
        }
    }

    /// <summary>
    /// Locates the inactive triangle glyph, synthesizes drop halo + bright rim + black
    /// inset by disk dilation, then paints orange fill.
    /// Draw order: shadow → bright rim → black inset → glow.
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
        var shadowOuterRadius = MetalBevelRadius + ShadowHaloRadius;
        var shadowOuter = DilateDisk(glyphMask, w, h, shadowOuterRadius);
        var searchRadius = shadowOuterRadius + 1;

        // Soft dark halo outside the rim (Cyberse Witch–style pop on blue honeycomb).
        for (var i = 0; i < shadowOuter.Length; i++)
        {
            if (!shadowOuter[i] || metalOuter[i])
                continue;
            var lx = i % w;
            var ly = i / w;
            var dist = MinDistanceToMask(lx, ly, glyphMask, w, h, searchRadius);
            var shadow = ToShadowPixel(dist);
            if (shadow.A == 0)
                continue;
            dst[x0 + lx, y0 + ly] = AlphaOver(dst[x0 + lx, y0 + ly], shadow);
        }

        // Bright rim = metal dilate − black dilate; black inset = black dilate − glyph.
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
    /// Bright silver/white rim shade. <paramref name="outerT"/> 0 = inner (near black
    /// inset), 1 = outer nearly-white highlight.
    /// </summary>
    public static Rgba32 ToMetalPixel(float outerT)
    {
        outerT = Math.Clamp(outerT, 0f, 1f);
        var r = (byte)Math.Clamp(MathF.Round(Lerp(MetalInnerR, MetalOuterR, outerT)), 0, 255);
        var g = (byte)Math.Clamp(MathF.Round(Lerp(MetalInnerG, MetalOuterG, outerT)), 0, 255);
        var b = (byte)Math.Clamp(MathF.Round(Lerp(MetalInnerB, MetalOuterB, outerT)), 0, 255);
        return new Rgba32(r, g, b, 255);
    }

    /// <summary>
    /// Soft dark drop halo outside the rim. <paramref name="distFromGlyph"/> is the
    /// Euclidean distance from the nearest glyph pixel; alpha peaks at the rim edge
    /// and falls to 0 at <see cref="MetalBevelRadius"/> + <see cref="ShadowHaloRadius"/>.
    /// </summary>
    public static Rgba32 ToShadowPixel(float distFromGlyph)
    {
        var inner = MetalBevelRadius;
        var outer = MetalBevelRadius + ShadowHaloRadius;
        if (distFromGlyph <= inner || distFromGlyph >= outer)
            return default;

        var t = (distFromGlyph - inner) / ShadowHaloRadius; // 0 at rim, 1 at outer
        // Mostly linear falloff with a light ease-out so the halo stays readable.
        var falloff = (1f - t) * (1f - 0.35f * t);
        var a = (byte)Math.Clamp(MathF.Round(ShadowMaxAlpha * falloff), 0, 255);
        return a == 0 ? default : new Rgba32(0, 0, 0, a);
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

using Floowan.Core.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Redraws Master Duel Link arrow markers as the topmost OF layer so subject/frame
/// punches cannot erase them. <c>Link.png</c> only has inactive flat triangles; those
/// silhouettes are aliased, so active markers fit a geometric triangle and render with
/// an anti-aliased signed-distance field:
/// soft dark drop halo → bright silver/white rim → thin black inset → orange/red fill.
/// Inactive directions stay as the flat dark glyphs already on the frame.
/// </summary>
public static class LinkArrowOverlay
{
    /// <summary>
    /// Crop rectangles on the 704×1024 Link frame for each direction (padded for AA,
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
    /// Used only to locate / fit the glyph; metallic mid-tones are not candidates.
    /// </summary>
    private const int GlyphLuminanceMax = 32;

    /// <summary>SDF radius (px) of the black inset between rim and orange fill.</summary>
    private const float BlackMarginRadius = 2f;

    /// <summary>SDF radius (px) of the bright silver/white triangular rim.</summary>
    private const float MetalBevelRadius = 8f;

    /// <summary>
    /// Extra SDF radius beyond the rim for the soft dark drop halo
    /// (total shadow extent = <see cref="MetalBevelRadius"/> + this).
    /// </summary>
    private const float ShadowHaloRadius = 5f;

    /// <summary>Peak alpha at the shadow’s inner edge (against the rim).</summary>
    private const byte ShadowMaxAlpha = 180;

    /// <summary>AA half-width (px) for smooth coverage along SDF iso-contours.</summary>
    private const float EdgeAa = 0.85f;

    /// <summary>
    /// Push fitted vertices outward from the centroid so the smooth triangle covers
    /// the jagged <c>Link.png</c> envelope instead of sitting inside it.
    /// </summary>
    private const float TriangleExpandPx = 1.15f;

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
    /// Fits a geometric triangle to the inactive glyph, then paints AA SDF rings.
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

        if (!TryLargestComponent(dark, w, h, out var glyph, out var cx, out var cy, out _))
            return;

        if (!TryFitTriangle(glyph, x0, y0, out var tip, out var baseA, out var baseB))
            return;

        ExpandTriangleFromCentroid(ref tip, ref baseA, ref baseB, cx, cy, TriangleExpandPx);

        var shadowOuter = MetalBevelRadius + ShadowHaloRadius;
        var maxDistSq = Math.Max(
            DistSq(tip.X, tip.Y, cx, cy),
            Math.Max(DistSq(baseA.X, baseA.Y, cx, cy), DistSq(baseB.X, baseB.Y, cx, cy)));
        if (maxDistSq < 1f)
            maxDistSq = 1f;
        var maxDist = MathF.Sqrt(maxDistSq);

        for (var ly = 0; ly < h; ly++)
        {
            for (var lx = 0; lx < w; lx++)
            {
                // Pixel-center sampling for AA.
                var px = lx + 0.5f;
                var py = ly + 0.5f;
                var d = SdTriangle(px, py, tip, baseA, baseB);
                if (d >= shadowOuter + EdgeAa)
                    continue;

                var dstX = x0 + lx;
                var dstY = y0 + ly;
                var under = dst[dstX, dstY];

                // Soft shadow band outside the bright rim.
                var shadowCov = BandCoverage(d, MetalBevelRadius, shadowOuter);
                if (shadowCov > 0.004f)
                {
                    var t = Math.Clamp(
                        (d - MetalBevelRadius) / ShadowHaloRadius,
                        0f,
                        1f);
                    var falloff = (1f - t) * (1f - 0.35f * t);
                    var a = (byte)Math.Clamp(
                        MathF.Round(ShadowMaxAlpha * falloff * shadowCov),
                        0,
                        255);
                    if (a > 0)
                        under = AlphaOver(under, new Rgba32(0, 0, 0, a));
                }

                // Bright rim between black inset and outer metal edge.
                var metalCov = BandCoverage(d, BlackMarginRadius, MetalBevelRadius);
                if (metalCov > 0.004f)
                {
                    var outerT = Math.Clamp(
                        (d - BlackMarginRadius) / (MetalBevelRadius - BlackMarginRadius),
                        0f,
                        1f);
                    var metal = ToMetalPixel(outerT);
                    metal = new Rgba32(
                        metal.R,
                        metal.G,
                        metal.B,
                        (byte)Math.Clamp(MathF.Round(255f * metalCov), 0, 255));
                    under = AlphaOver(under, metal);
                }

                // Thin black inset between orange fill and bright rim.
                var blackCov = BandCoverage(d, 0f, BlackMarginRadius);
                if (blackCov > 0.004f)
                {
                    var black = new Rgba32(
                        BlackInset.R,
                        BlackInset.G,
                        BlackInset.B,
                        (byte)Math.Clamp(MathF.Round(255f * blackCov), 0, 255));
                    under = AlphaOver(under, black);
                }

                // Orange fill inside the geometric triangle.
                var fillCov = InsideCoverage(d, 0f);
                if (fillCov > 0.004f)
                {
                    var edgeT = Math.Clamp(
                        MathF.Sqrt(DistSq(px, py, cx, cy)) / maxDist,
                        0f,
                        1f);
                    var lit = ToLitArrowPixel(new Rgba32(8, 8, 10, 255), edgeT);
                    lit = new Rgba32(
                        lit.R,
                        lit.G,
                        lit.B,
                        (byte)Math.Clamp(MathF.Round(255f * fillCov), 0, 255));
                    under = AlphaOver(under, lit);
                }

                dst[dstX, dstY] = under;
            }
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
    /// unsigned distance outside the fitted fill edge; alpha peaks at the rim edge
    /// and falls to 0 at <see cref="MetalBevelRadius"/> + <see cref="ShadowHaloRadius"/>.
    /// </summary>
    public static Rgba32 ToShadowPixel(float distFromGlyph)
    {
        var inner = MetalBevelRadius;
        var outer = MetalBevelRadius + ShadowHaloRadius;
        if (distFromGlyph <= inner || distFromGlyph >= outer)
            return default;

        var t = (distFromGlyph - inner) / ShadowHaloRadius; // 0 at rim, 1 at outer
        var falloff = (1f - t) * (1f - 0.35f * t);
        var a = (byte)Math.Clamp(MathF.Round(ShadowMaxAlpha * falloff), 0, 255);
        return a == 0 ? default : new Rgba32(0, 0, 0, a);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float DistSq(float ax, float ay, float bx, float by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return dx * dx + dy * dy;
    }

    /// <summary>Coverage of band <c>inner ≤ d &lt; outer</c> with AA at both edges.</summary>
    private static float BandCoverage(float d, float inner, float outer)
    {
        var insideOuter = InsideCoverage(d, outer);
        var insideInner = InsideCoverage(d, inner);
        return Math.Clamp(insideOuter - insideInner, 0f, 1f);
    }

    /// <summary>Smooth coverage for the half-plane <c>d &lt; edge</c>.</summary>
    private static float InsideCoverage(float d, float edge)
    {
        // 1 when deeply inside (d << edge), 0 when outside (d >> edge).
        return SmoothStep(edge + EdgeAa, edge - EdgeAa, d);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge0 == edge1)
            return x < edge0 ? 1f : 0f;
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Fits an isosceles-ish triangle to the jagged glyph: tip = point farthest from
    /// the shared Effect art-window center (so L/R/SW/SE aim correctly), base = the
    /// convex-hull pair that maximizes triangle area with that tip.
    /// </summary>
    private static bool TryFitTriangle(
        List<(int X, int Y)> glyph,
        int absX0,
        int absY0,
        out PointF tip,
        out PointF baseA,
        out PointF baseB)
    {
        tip = baseA = baseB = default;
        if (glyph.Count < 3)
            return false;

        var art = OverFrameAutoArtComposer.ArtWindow;
        var artCx = art.Left + art.Width * 0.5f;
        var artCy = art.Top + art.Height * 0.5f;

        var tipLocal = glyph[0];
        var tipDist = -1f;
        foreach (var p in glyph)
        {
            var d = DistSq(absX0 + p.X, absY0 + p.Y, artCx, artCy);
            if (d > tipDist)
            {
                tipDist = d;
                tipLocal = p;
            }
        }

        var hull = ConvexHull(glyph);
        if (hull.Count < 3)
            return false;

        var bestArea = -1f;
        var b1 = hull[0];
        var b2 = hull[1];
        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            if (a.X == tipLocal.X && a.Y == tipLocal.Y)
                continue;
            for (var j = i + 1; j < hull.Count; j++)
            {
                var b = hull[j];
                if (b.X == tipLocal.X && b.Y == tipLocal.Y)
                    continue;
                var area = MathF.Abs(Cross(
                    a.X - tipLocal.X,
                    a.Y - tipLocal.Y,
                    b.X - tipLocal.X,
                    b.Y - tipLocal.Y));
                if (area > bestArea)
                {
                    bestArea = area;
                    b1 = a;
                    b2 = b;
                }
            }
        }

        if (bestArea < 4f)
            return false;

        tip = new PointF(tipLocal.X + 0.5f, tipLocal.Y + 0.5f);
        baseA = new PointF(b1.X + 0.5f, b1.Y + 0.5f);
        baseB = new PointF(b2.X + 0.5f, b2.Y + 0.5f);
        return true;
    }

    private static void ExpandTriangleFromCentroid(
        ref PointF tip,
        ref PointF baseA,
        ref PointF baseB,
        float cx,
        float cy,
        float expand)
    {
        tip = PushOut(tip, cx, cy, expand);
        baseA = PushOut(baseA, cx, cy, expand);
        baseB = PushOut(baseB, cx, cy, expand);
    }

    private static PointF PushOut(PointF p, float cx, float cy, float expand)
    {
        var dx = p.X - cx;
        var dy = p.Y - cy;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f)
            return p;
        var s = (len + expand) / len;
        return new PointF(cx + dx * s, cy + dy * s);
    }

    private static float Cross(float ax, float ay, float bx, float by) => ax * by - ay * bx;

    /// <summary>Andrew’s monotone chain convex hull (integer pixel coords).</summary>
    private static List<(int X, int Y)> ConvexHull(List<(int X, int Y)> points)
    {
        var pts = points
            .Distinct()
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ToList();
        if (pts.Count <= 1)
            return pts;

        static long CrossL((int X, int Y) o, (int X, int Y) a, (int X, int Y) b) =>
            (long)(a.X - o.X) * (b.Y - o.Y) - (long)(a.Y - o.Y) * (b.X - o.X);

        var lower = new List<(int X, int Y)>();
        foreach (var p in pts)
        {
            while (lower.Count >= 2 && CrossL(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }

        var upper = new List<(int X, int Y)>();
        for (var i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            while (upper.Count >= 2 && CrossL(upper[^2], upper[^1], p) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    /// <summary>
    /// Signed distance to a triangle (negative inside). Adapted from Inigo Quilez.
    /// </summary>
    private static float SdTriangle(float px, float py, PointF a, PointF b, PointF c)
    {
        float DistToEdge(float ex, float ey, float vx, float vy)
        {
            var ee = Dot(ex, ey, ex, ey);
            var t = ee > 1e-8f ? Math.Clamp(Dot(vx, vy, ex, ey) / ee, 0f, 1f) : 0f;
            var qx = vx - ex * t;
            var qy = vy - ey * t;
            return Dot(qx, qy, qx, qy);
        }

        var e0x = b.X - a.X;
        var e0y = b.Y - a.Y;
        var e1x = c.X - b.X;
        var e1y = c.Y - b.Y;
        var e2x = a.X - c.X;
        var e2y = a.Y - c.Y;

        var v0x = px - a.X;
        var v0y = py - a.Y;
        var v1x = px - b.X;
        var v1y = py - b.Y;
        var v2x = px - c.X;
        var v2y = py - c.Y;

        var d = Min3(
            DistToEdge(e0x, e0y, v0x, v0y),
            DistToEdge(e1x, e1y, v1x, v1y),
            DistToEdge(e2x, e2y, v2x, v2y));

        var s = MathF.Sign(e0x * e2y - e0y * e2x);
        var s0 = s * (v0x * e0y - v0y * e0x);
        var s1 = s * (v1x * e1y - v1y * e1x);
        var s2 = s * (v2x * e2y - v2y * e2x);
        return -MathF.Sqrt(d) * MathF.Sign(Math.Min(Math.Min(s0, s1), s2));
    }

    private static float Dot(float ax, float ay, float bx, float by) => ax * bx + ay * by;

    private static float Min3(float a, float b, float c) => Math.Min(a, Math.Min(b, c));

    private readonly struct PointF
    {
        public PointF(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float X { get; }
        public float Y { get; }
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
            var d = DistSq(x, y, centroidX, centroidY);
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

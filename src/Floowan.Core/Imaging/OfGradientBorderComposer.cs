using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Derives official-OF-style frame templates with <b>multi-hue iridescent</b> outer
/// borders from solid <c>card_frame*</c> PNGs. Keeps the transparent art window, gold art
/// rim, and lore cream intact so Mirrorjade soft lore compose
/// (<see cref="OverFrameAutoArtComposer.TextBoxFrameOpacity"/>) continues to work.
/// <para>
/// Color intent (user feedback on PR #57): official OF rims are <em>not</em> white
/// brightness gradients. Linkage is teal/seafoam with rainbow shimmer (pink, yellow,
/// purple, cyan). Magician of Black Chaos uses cyan/teal corner light. Chaos Soldier
/// shafts stay chromatic pale blue — never pure white screen.
/// Multi-hue is produced by sampling a type-specific HSV stop table from the perimeter
/// angle (atan2 from canvas center) and keeping chroma; no white screen-blend.
/// </para>
/// </summary>
public static class OfGradientBorderComposer
{
    /// <summary>Outer rim band (px from canvas edge) where iridescence is strongest.</summary>
    public const int OuterRimPx = 64;

    /// <summary>Side light-shaft width (Chaos Soldier–style chromatic vertical glow).</summary>
    public const int LightShaftPx = 78;

    /// <summary>Corner radial glow radius (Magician of Black Chaos–style cyan/teal).</summary>
    public const float CornerRadiusPx = 190f;

    /// <summary>
    /// Builds an OF-gradient frame from a solid template. Caller owns the returned image.
    /// </summary>
    public static Image<Rgba32> Apply(Image<Rgba32> solidFrame, CardFrameStyle ofGradientStyle)
    {
        ArgumentNullException.ThrowIfNull(solidFrame);
        if (!CardFrameTemplates.IsOfGradientStyle(ofGradientStyle))
            throw new ArgumentException($"Expected an OfGradient* style, got {ofGradientStyle}.", nameof(ofGradientStyle));

        var baseStyle = CardFrameTemplates.GetSolidBaseStyle(ofGradientStyle);
        var pendulum = CardFrameTemplates.IsPendulumStyle(ofGradientStyle);
        var artWindow = pendulum
            ? OverFrameAutoArtComposer.PendulumArtWindow
            : OverFrameAutoArtComposer.ArtWindow;
        var loreCream = pendulum
            ? OverFrameAutoArtComposer.PendulumLoreCream
            : OverFrameAutoArtComposer.EffectLoreCream;

        var palette = ResolvePalette(baseStyle);
        var result = solidFrame.Clone();
        var w = result.Width;
        var h = result.Height;
        var cx = (w - 1) * 0.5f;
        var cy = (h - 1) * 0.5f;

        for (var y = 0; y < h; y++)
        {
            var row = result.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < w; x++)
            {
                var src = row[x];
                if (src.A <= OverFrameAutoArtComposer.VisibleAlphaThreshold)
                    continue;

                if (artWindow.Contains(x, y))
                    continue;

                if (loreCream.Contains(x, y) && LooksLikeLoreCream(src))
                    continue;

                if (LooksLikeGoldRim(src, artWindow, loreCream, x, y))
                    continue;

                var edgeDist = MinEdgeDistance(x, y, w, h);
                var rimT = EaseOut01(1f - Math.Clamp(edgeDist / (float)OuterRimPx, 0f, 1f));
                var shaftT = SideLightShaftStrength(x, w) * palette.ShaftBoost;
                var cornerT = CornerGlowStrength(x, y, w, h) * palette.CornerBoost;
                var marginT = Math.Clamp(rimT * 0.55f + shaftT * 0.55f + cornerT * 0.45f, 0f, 1f);

                var mix = Math.Clamp(
                    palette.BodyTint + (1f - palette.BodyTint) * marginT * palette.ReplaceBoost,
                    0f,
                    0.97f);
                if (mix < 0.08f)
                    continue;

                // Perimeter angle drives multi-hue (Linkage rainbow along the rim).
                var angle01 = PerimeterAngle01(x, y, cx, cy);
                var target = SampleIridescentColor(palette, angle01, rimT, shaftT, cornerT, x, y);

                var outPix = LerpRgb(src, target, mix);

                if (palette.UseHoloGrid)
                    outPix = ApplyHoloGrid(outPix, x, y, marginT, angle01);

                row[x] = outPix;
            }
        }

        return result;
    }

    /// <summary>
    /// Writes derived OF-gradient PNGs next to the solid templates (or <paramref name="outputDirectory"/>).
    /// </summary>
    public static int GenerateAllPresetPngs(string? outputDirectory = null)
    {
        var written = 0;
        foreach (CardFrameStyle style in Enum.GetValues<CardFrameStyle>())
        {
            if (!CardFrameTemplates.IsOfGradientStyle(style))
                continue;

            var baseStyle = CardFrameTemplates.GetSolidBaseStyle(style);
            using var solid = CardFrameTemplates.Load(baseStyle);
            using var derived = Apply(solid, style);
            var dir = outputDirectory
                ?? Path.GetDirectoryName(CardFrameTemplates.ResolveTemplatePath(baseStyle))
                ?? throw new InvalidOperationException("Could not resolve frames directory.");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, CardFrameTemplates.GetFileName(style));
            derived.SaveAsPng(path);
            written++;
        }

        return written;
    }

    /// <summary>HSV stop: H in degrees [0,360), S/V in [0,1].</summary>
    private readonly record struct HueStop(float H, float S, float V);

    /// <param name="BodyTint">Minimum mix toward OF color on inner plate chrome.</param>
    /// <param name="ReplaceBoost">Scales margin replacement.</param>
    /// <param name="ShaftBoost">Emphasize L/R chromatic shafts.</param>
    /// <param name="CornerBoost">Emphasize corner chromatic leaks.</param>
    /// <param name="ShimmerAmount">How hard perimeter angle hues mix onto the rim (Spell high).</param>
    private readonly record struct BorderPalette(
        Rgba32 Mid,
        HueStop[] Stops,
        bool UseHoloGrid,
        float BodyTint,
        float ReplaceBoost,
        float ShaftBoost,
        float CornerBoost,
        float ShimmerAmount);

    private static BorderPalette ResolvePalette(CardFrameStyle baseStyle) => baseStyle switch
    {
        // Linkage — teal/seafoam base + full rainbow shimmer (sampled perimeter hues).
        CardFrameStyle.Spell => new(
            Mid: new Rgba32(28, 130, 135, 255),
            Stops:
            [
                new(175f, 0.72f, 0.78f), // cyan-teal
                new(155f, 0.65f, 0.82f), // seafoam
                new(195f, 0.45f, 0.80f), // soft cyan
                new(280f, 0.35f, 0.78f), // purple shimmer
                new(330f, 0.40f, 0.85f), // pink
                new(45f, 0.45f, 0.88f),  // warm yellow
                new(150f, 0.70f, 0.70f), // green-teal
                new(185f, 0.55f, 0.75f), // aqua
            ],
            UseHoloGrid: true,
            BodyTint: 0.36f,
            ReplaceBoost: 1.08f,
            ShaftBoost: 0.95f,
            CornerBoost: 1.05f,
            ShimmerAmount: 0.92f),

        // Magician of Black Chaos — cyan/teal corners, deeper blue mid (colored, not white).
        CardFrameStyle.Ritual or CardFrameStyle.PendulumRitual => new(
            Mid: new Rgba32(28, 48, 78, 255),
            Stops:
            [
                new(195f, 0.55f, 0.55f), // blue-grey edge
                new(185f, 0.70f, 0.85f), // bright cyan
                new(170f, 0.65f, 0.80f), // teal
                new(210f, 0.40f, 0.70f), // periwinkle
                new(160f, 0.50f, 0.75f), // seafoam corner
                new(200f, 0.60f, 0.78f), // electric cyan
            ],
            UseHoloGrid: false,
            BodyTint: 0.26f,
            ReplaceBoost: 1.12f,
            ShaftBoost: 0.80f,
            CornerBoost: 1.40f,
            ShimmerAmount: 0.70f),

        // Chaos Soldier — chromatic pale blue/cyan shafts (not pure white).
        CardFrameStyle.Effect or CardFrameStyle.PendulumEffect => new(
            Mid: new Rgba32(75, 70, 95, 255),
            Stops:
            [
                new(210f, 0.35f, 0.70f), // pale blue
                new(190f, 0.45f, 0.78f), // cyan
                new(175f, 0.40f, 0.72f), // teal-blue
                new(40f, 0.30f, 0.75f),  // warm gold leak (art-integrated)
                new(220f, 0.30f, 0.68f), // periwinkle
                new(185f, 0.50f, 0.82f), // bright cyan shaft
            ],
            UseHoloGrid: false,
            BodyTint: 0.16f,
            ReplaceBoost: 1.10f,
            ShaftBoost: 1.25f,
            CornerBoost: 1.05f,
            ShimmerAmount: 0.65f),

        CardFrameStyle.Trap or CardFrameStyle.Token or CardFrameStyle.PendulumToken => new(
            Mid: new Rgba32(95, 35, 105, 255),
            Stops:
            [
                new(310f, 0.55f, 0.75f), // magenta
                new(280f, 0.50f, 0.70f), // purple
                new(330f, 0.45f, 0.82f), // pink
                new(200f, 0.30f, 0.70f), // cool cyan leak
                new(340f, 0.40f, 0.78f),
            ],
            UseHoloGrid: false,
            BodyTint: 0.30f,
            ReplaceBoost: 1.08f,
            ShaftBoost: 1.00f,
            CornerBoost: 1.15f,
            ShimmerAmount: 0.75f),

        CardFrameStyle.Fusion or CardFrameStyle.PendulumFusion => new(
            Mid: new Rgba32(85, 40, 125, 255),
            Stops:
            [
                new(280f, 0.55f, 0.75f),
                new(300f, 0.45f, 0.80f),
                new(260f, 0.40f, 0.70f),
                new(320f, 0.35f, 0.82f),
                new(200f, 0.25f, 0.72f),
            ],
            UseHoloGrid: false,
            BodyTint: 0.28f,
            ReplaceBoost: 1.08f,
            ShaftBoost: 1.00f,
            CornerBoost: 1.20f,
            ShimmerAmount: 0.70f),

        CardFrameStyle.Synchro or CardFrameStyle.PendulumSynchro => new(
            Mid: new Rgba32(140, 145, 160, 255),
            Stops:
            [
                new(210f, 0.25f, 0.85f), // cool silver-blue
                new(45f, 0.20f, 0.88f),  // warm silver
                new(280f, 0.15f, 0.82f), // lilac shimmer
                new(180f, 0.22f, 0.86f), // aqua silver
                new(0f, 0.12f, 0.90f),   // soft rose-silver
            ],
            UseHoloGrid: false,
            BodyTint: 0.26f,
            ReplaceBoost: 1.05f,
            ShaftBoost: 1.00f,
            CornerBoost: 1.10f,
            ShimmerAmount: 0.60f),

        CardFrameStyle.Xyz or CardFrameStyle.PendulumXyz => new(
            Mid: new Rgba32(18, 20, 32, 255),
            Stops:
            [
                new(210f, 0.55f, 0.75f),
                new(190f, 0.50f, 0.80f),
                new(260f, 0.35f, 0.70f),
                new(175f, 0.45f, 0.72f),
                new(220f, 0.40f, 0.78f),
            ],
            UseHoloGrid: true,
            BodyTint: 0.24f,
            ReplaceBoost: 1.10f,
            ShaftBoost: 1.10f,
            CornerBoost: 1.20f,
            ShimmerAmount: 0.68f),

        CardFrameStyle.Link => new(
            Mid: new Rgba32(22, 48, 72, 255),
            Stops:
            [
                new(185f, 0.70f, 0.80f),
                new(170f, 0.60f, 0.75f),
                new(200f, 0.50f, 0.78f),
                new(280f, 0.30f, 0.72f),
                new(150f, 0.55f, 0.70f),
                new(45f, 0.25f, 0.80f),
            ],
            UseHoloGrid: true,
            BodyTint: 0.32f,
            ReplaceBoost: 1.10f,
            ShaftBoost: 1.05f,
            CornerBoost: 1.15f,
            ShimmerAmount: 0.80f),

        CardFrameStyle.Normal or CardFrameStyle.PendulumNormal => new(
            Mid: new Rgba32(170, 135, 55, 255),
            Stops:
            [
                new(45f, 0.65f, 0.88f),  // gold
                new(35f, 0.55f, 0.85f),  // amber
                new(55f, 0.40f, 0.90f),  // yellow
                new(20f, 0.45f, 0.82f),  // peach
                new(200f, 0.20f, 0.75f), // cool leak
            ],
            UseHoloGrid: false,
            BodyTint: 0.26f,
            ReplaceBoost: 1.05f,
            ShaftBoost: 1.00f,
            CornerBoost: 1.10f,
            ShimmerAmount: 0.65f),

        _ => new(
            Mid: new Rgba32(55, 70, 100, 255),
            Stops:
            [
                new(190f, 0.45f, 0.75f),
                new(175f, 0.40f, 0.70f),
                new(210f, 0.35f, 0.72f),
                new(40f, 0.25f, 0.75f),
            ],
            UseHoloGrid: false,
            BodyTint: 0.22f,
            ReplaceBoost: 1.08f,
            ShaftBoost: 1.10f,
            CornerBoost: 1.10f,
            ShimmerAmount: 0.65f),
    };

    /// <summary>
    /// Multi-hue OF rim color: mid chrome → angle-sampled chromatic stop, with shaft/corner
    /// boosting the same hue family (never toward white).
    /// </summary>
    private static Rgba32 SampleIridescentColor(
        BorderPalette palette,
        float angle01,
        float rimT,
        float shaftT,
        float cornerT,
        int x,
        int y)
    {
        var shimmer = SampleHueStops(palette.Stops, angle01);
        // Secondary phase so adjacent rim pixels don't look banded.
        var shimmer2 = SampleHueStops(palette.Stops, Fract(angle01 + 0.17f + SoftMottle(x, y) * 0.08f));
        var iri = LerpRgb(shimmer, shimmer2, 0.35f);

        // Body mid stays type-colored; rim pulls strongly toward multi-hue.
        var rimPull = Math.Clamp(palette.ShimmerAmount * (0.35f + 0.65f * rimT), 0f, 1f);
        var c = LerpRgb(palette.Mid, iri, rimPull);

        // Shafts: same hue family, slightly higher value, keep saturation (chromatic, not white).
        if (shaftT > 0.05f)
        {
            var shaftHue = SampleHueStops(palette.Stops, Fract(angle01 + 0.08f));
            shaftHue = BoostChroma(shaftHue, satMul: 1.05f, valMul: 1.08f);
            c = LerpRgb(c, shaftHue, Math.Clamp(shaftT * 0.55f, 0f, 0.75f));
        }

        // Corners: Magician-style cyan/teal (or type stop) — colored light leak.
        if (cornerT > 0.05f)
        {
            var cornerHue = SampleHueStops(palette.Stops, Fract(angle01 * 0.5f + 0.25f));
            cornerHue = BoostChroma(cornerHue, satMul: 1.12f, valMul: 1.10f);
            c = LerpRgb(c, cornerHue, Math.Clamp(cornerT * 0.50f, 0f, 0.70f));
        }

        // Soft mottle for premium foil grain (value only, chroma preserved via HSV).
        var mottle = SoftMottle(x, y);
        c = BoostChroma(c, satMul: 1f, valMul: 0.94f + 0.10f * mottle);
        return c;
    }

    private static Rgba32 SampleHueStops(HueStop[] stops, float t01)
    {
        if (stops.Length == 0)
            return new Rgba32(128, 128, 128, 255);
        if (stops.Length == 1)
            return HsvToRgb(stops[0].H, stops[0].S, stops[0].V);

        t01 = Fract(t01);
        var scaled = t01 * stops.Length;
        var i0 = (int)scaled % stops.Length;
        var i1 = (i0 + 1) % stops.Length;
        var local = scaled - MathF.Floor(scaled);
        // Smoothstep for softer rainbow bands.
        local = local * local * (3f - 2f * local);

        var a = stops[i0];
        var b = stops[i1];
        // Lerp hue on shortest arc so pink↔teal transitions don't spin the long way.
        var h = LerpHue(a.H, b.H, local);
        var s = a.S + (b.S - a.S) * local;
        var v = a.V + (b.V - a.V) * local;
        return HsvToRgb(h, s, v);
    }

    private static float LerpHue(float a, float b, float t)
    {
        var d = b - a;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return FractDegrees(a + d * t);
    }

    private static Rgba32 BoostChroma(Rgba32 c, float satMul, float valMul)
    {
        RgbToHsv(c, out var h, out var s, out var v);
        s = Math.Clamp(s * satMul, 0f, 1f);
        v = Math.Clamp(v * valMul, 0f, 1f);
        // Floor saturation so we never collapse to grey/white glow.
        if (s < 0.12f)
            s = 0.12f;
        return HsvToRgb(h, s, v);
    }

    private static Rgba32 ApplyHoloGrid(Rgba32 c, int x, int y, float marginT, float angle01)
    {
        const int cell = 8;
        var onV = x % cell == 0;
        var onH = y % cell == 0;
        if (!onV && !onH)
            return c;

        // Grid lines pick up the same multi-hue (brighter, still chromatic).
        var grid = SampleHueStops(
            [
                new(175f, 0.35f, 0.92f),
                new(330f, 0.30f, 0.90f),
                new(50f, 0.30f, 0.92f),
                new(200f, 0.28f, 0.90f),
            ],
            angle01);
        var strength = (0.22f + 0.40f * marginT) * (onV && onH ? 1f : 0.70f);
        return LerpRgb(c, grid, strength);
    }

    private static float PerimeterAngle01(int x, int y, float cx, float cy)
    {
        var ang = MathF.Atan2(y - cy, x - cx); // -π..π
        return Fract((ang + MathF.PI) / (MathF.PI * 2f));
    }

    private static float SoftMottle(int x, int y)
    {
        var n = Hash2(x, y) * 0.55f + Hash2(x / 3, y / 3) * 0.30f + Hash2(x / 7, y / 7) * 0.15f;
        return n;
    }

    private static float Hash2(int x, int y)
    {
        unchecked
        {
            var n = (uint)(x * 374761393 + y * 668265263);
            n = (n ^ (n >> 13)) * 1274126177u;
            n ^= n >> 16;
            return (n & 0xFFFF) / 65535f;
        }
    }

    private static float SideLightShaftStrength(int x, int w)
    {
        var left = 1f - Math.Clamp(x / (float)LightShaftPx, 0f, 1f);
        var right = 1f - Math.Clamp((w - 1 - x) / (float)LightShaftPx, 0f, 1f);
        return EaseOut01(Math.Max(left, right));
    }

    private static float CornerGlowStrength(int x, int y, int w, int h)
    {
        float Best(int cx, int cy)
        {
            var dx = x - cx;
            var dy = y - cy;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            var t = 1f - Math.Clamp(d / CornerRadiusPx, 0f, 1f);
            return EaseOut01(t);
        }

        return Math.Max(
            Math.Max(Best(0, 0), Best(w - 1, 0)),
            Math.Max(Best(0, h - 1), Best(w - 1, h - 1)));
    }

    private static float EaseOut01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var u = 1f - t;
        return 1f - u * u;
    }

    private static float Fract(float v) => v - MathF.Floor(v);

    private static float FractDegrees(float deg)
    {
        deg %= 360f;
        if (deg < 0f) deg += 360f;
        return deg;
    }

    private static int MinEdgeDistance(int x, int y, int w, int h) =>
        Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y));

    private static bool LooksLikeLoreCream(Rgba32 p)
    {
        var max = Math.Max(p.R, Math.Max(p.G, p.B));
        var min = Math.Min(p.R, Math.Min(p.G, p.B));
        if (max < 150)
            return false;
        if (max - min > 70)
            return false;
        if (p.B > p.R + 40 && p.B > p.G + 20)
            return false;
        return true;
    }

    private static bool LooksLikeGoldRim(
        Rgba32 p,
        Rectangle artWindow,
        Rectangle loreCream,
        int x,
        int y)
    {
        if (p.R < 120 || p.R < p.B + 15)
            return false;
        if (p.G < 70 || p.G > p.R + 10)
            return false;

        var nearArt = DistanceOutsideRect(x, y, artWindow) <= 14;
        var nearLore = DistanceOutsideRect(x, y, loreCream) <= 10
            || (y >= loreCream.Top - 12 && y < loreCream.Top && x >= loreCream.Left - 4 && x < loreCream.Right + 4);
        return nearArt || nearLore;
    }

    private static int DistanceOutsideRect(int x, int y, Rectangle r)
    {
        if (r.Contains(x, y))
            return 0;
        var dx = x < r.Left ? r.Left - x : (x >= r.Right ? x - (r.Right - 1) : 0);
        var dy = y < r.Top ? r.Top - y : (y >= r.Bottom ? y - (r.Bottom - 1) : 0);
        return Math.Max(dx, dy);
    }

    private static Rgba32 LerpRgb(Rgba32 a, Rgba32 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var r = (byte)Math.Clamp((int)MathF.Round(a.R + (b.R - a.R) * t), 0, 255);
        var g = (byte)Math.Clamp((int)MathF.Round(a.G + (b.G - a.G) * t), 0, 255);
        var bl = (byte)Math.Clamp((int)MathF.Round(a.B + (b.B - a.B) * t), 0, 255);
        return new Rgba32(r, g, bl, a.A);
    }

    private static void RgbToHsv(Rgba32 c, out float h, out float s, out float v)
    {
        var r = c.R / 255f;
        var g = c.G / 255f;
        var b = c.B / 255f;
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        v = max;
        var d = max - min;
        s = max <= 1e-6f ? 0f : d / max;
        if (d <= 1e-6f)
        {
            h = 0f;
            return;
        }

        if (max == r)
            h = 60f * (((g - b) / d) % 6f);
        else if (max == g)
            h = 60f * (((b - r) / d) + 2f);
        else
            h = 60f * (((r - g) / d) + 4f);

        if (h < 0f)
            h += 360f;
    }

    private static Rgba32 HsvToRgb(float h, float s, float v)
    {
        h = FractDegrees(h);
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        var c = v * s;
        var x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
        var m = v - c;
        float r1, g1, b1;
        if (h < 60f) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120f) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180f) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240f) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300f) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        return new Rgba32(
            (byte)Math.Clamp((int)MathF.Round((r1 + m) * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round((g1 + m) * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round((b1 + m) * 255f), 0, 255),
            255);
    }
}

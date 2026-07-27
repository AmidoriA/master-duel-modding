using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Derives official-OF-style frame templates with a brighter gradient / light-leak outer
/// rim from the solid <c>card_frame*</c> PNGs. Keeps the transparent art window, gold art
/// rim, and lore cream intact so Mirrorjade soft lore compose (<see cref="OverFrameAutoArtComposer.TextBoxFrameOpacity"/>)
/// continues to work unchanged.
/// </summary>
public static class OfGradientBorderComposer
{
    /// <summary>Outer rim band (px from canvas edge) that receives the strongest light leak.</summary>
    public const int OuterRimPx = 36;

    /// <summary>Side light-shaft width (Chaos Soldier–style pale vertical glow).</summary>
    public const int LightShaftPx = 48;

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

        for (var y = 0; y < h; y++)
        {
            var row = result.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < w; x++)
            {
                var src = row[x];
                if (src.A <= OverFrameAutoArtComposer.VisibleAlphaThreshold)
                    continue;

                // Art hole stays empty (already A≈0); skip any residual hole pixels.
                if (artWindow.Contains(x, y))
                    continue;

                // Lore cream / mint / monster panels stay vanilla so Mirrorjade soft
                // underlay still blends against the same cream RGB.
                if (loreCream.Contains(x, y) && LooksLikeLoreCream(src))
                    continue;

                // Preserve warm gold art-window / lore rims (inner chrome).
                if (LooksLikeGoldRim(src, artWindow, loreCream, x, y))
                    continue;

                var edgeDist = MinEdgeDistance(x, y, w, h);
                var rimT = 1f - Math.Clamp(edgeDist / (float)OuterRimPx, 0f, 1f);
                rimT = rimT * rimT; // ease-in toward the outer edge

                var shaftT = SideLightShaftStrength(x, w);
                var cornerT = CornerGlowStrength(x, y, w, h);
                var gridT = palette.UseHoloGrid ? HoloGridModulation(x, y) : 0f;

                // How hard we push toward the luminous OF rim (0 = keep solid chrome).
                var mix = Math.Clamp(0.18f + 0.55f * rimT + 0.35f * shaftT + 0.40f * cornerT, 0f, 0.92f);
                if (mix < 0.05f)
                    continue;

                var target = SampleLuminousColor(palette, x, y, w, h, rimT, shaftT, cornerT, gridT);
                row[x] = LerpRgb(src, target, mix);
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

    private readonly record struct BorderPalette(
        Rgba32 Edge,
        Rgba32 Mid,
        Rgba32 HotCorner,
        Rgba32 Shaft,
        bool UseHoloGrid);

    private static BorderPalette ResolvePalette(CardFrameStyle baseStyle) => baseStyle switch
    {
        // Sky Striker Linkage–style: teal / iridescent with warm corner leaks + holo grid.
        CardFrameStyle.Spell => new(
            Edge: new Rgba32(120, 220, 210, 255),
            Mid: new Rgba32(40, 140, 145, 255),
            HotCorner: new Rgba32(255, 210, 180, 255),
            Shaft: new Rgba32(170, 245, 235, 255),
            UseHoloGrid: true),

        // Magician of Black Chaos–style: luminous cyan / teal corner glow.
        CardFrameStyle.Ritual or CardFrameStyle.PendulumRitual => new(
            Edge: new Rgba32(140, 230, 245, 255),
            Mid: new Rgba32(30, 90, 140, 255),
            HotCorner: new Rgba32(220, 250, 255, 255),
            Shaft: new Rgba32(180, 240, 255, 255),
            UseHoloGrid: false),

        // Chaos Soldier–style pale blue / white light shafts (also Effect monsters).
        CardFrameStyle.Effect or CardFrameStyle.PendulumEffect => new(
            Edge: new Rgba32(190, 215, 245, 255),
            Mid: new Rgba32(70, 85, 120, 255),
            HotCorner: new Rgba32(245, 250, 255, 255),
            Shaft: new Rgba32(210, 230, 255, 255),
            UseHoloGrid: false),

        CardFrameStyle.Trap or CardFrameStyle.Token or CardFrameStyle.PendulumToken => new(
            Edge: new Rgba32(220, 150, 210, 255),
            Mid: new Rgba32(90, 40, 100, 255),
            HotCorner: new Rgba32(255, 200, 230, 255),
            Shaft: new Rgba32(235, 180, 230, 255),
            UseHoloGrid: false),

        CardFrameStyle.Fusion or CardFrameStyle.PendulumFusion => new(
            Edge: new Rgba32(210, 160, 245, 255),
            Mid: new Rgba32(90, 50, 130, 255),
            HotCorner: new Rgba32(255, 220, 255, 255),
            Shaft: new Rgba32(230, 190, 255, 255),
            UseHoloGrid: false),

        CardFrameStyle.Synchro or CardFrameStyle.PendulumSynchro => new(
            Edge: new Rgba32(235, 240, 250, 255),
            Mid: new Rgba32(140, 145, 155, 255),
            HotCorner: new Rgba32(255, 255, 255, 255),
            Shaft: new Rgba32(245, 248, 255, 255),
            UseHoloGrid: false),

        CardFrameStyle.Xyz or CardFrameStyle.PendulumXyz => new(
            Edge: new Rgba32(160, 200, 255, 255),
            Mid: new Rgba32(20, 22, 35, 255),
            HotCorner: new Rgba32(230, 240, 255, 255),
            Shaft: new Rgba32(180, 210, 255, 255),
            UseHoloGrid: true),

        CardFrameStyle.Link => new(
            Edge: new Rgba32(100, 210, 230, 255),
            Mid: new Rgba32(25, 45, 70, 255),
            HotCorner: new Rgba32(200, 245, 255, 255),
            Shaft: new Rgba32(140, 230, 245, 255),
            UseHoloGrid: true),

        CardFrameStyle.Normal or CardFrameStyle.PendulumNormal => new(
            Edge: new Rgba32(255, 230, 160, 255),
            Mid: new Rgba32(170, 140, 70, 255),
            HotCorner: new Rgba32(255, 250, 220, 255),
            Shaft: new Rgba32(255, 240, 190, 255),
            UseHoloGrid: false),

        _ => new(
            Edge: new Rgba32(190, 215, 245, 255),
            Mid: new Rgba32(70, 85, 120, 255),
            HotCorner: new Rgba32(245, 250, 255, 255),
            Shaft: new Rgba32(210, 230, 255, 255),
            UseHoloGrid: false),
    };

    private static Rgba32 SampleLuminousColor(
        BorderPalette palette,
        int x,
        int y,
        int w,
        int h,
        float rimT,
        float shaftT,
        float cornerT,
        float gridT)
    {
        // Edge → mid body chrome, then lift with shaft / corner / grid.
        var t = Math.Clamp(0.35f + 0.65f * rimT, 0f, 1f);
        var c = LerpRgb(palette.Mid, palette.Edge, t);
        c = LerpRgb(c, palette.Shaft, shaftT * 0.75f);
        c = LerpRgb(c, palette.HotCorner, cornerT * 0.85f);
        if (gridT > 0f)
        {
            // Subtle teal lift on grid lines (Sky Striker holo mesh).
            var gridTint = new Rgba32(180, 255, 240, 255);
            c = LerpRgb(c, gridTint, gridT * 0.35f);
        }

        // Soft vertical falloff so top/bottom rims stay bright without washing the mid sides.
        var yNorm = y / (float)Math.Max(1, h - 1);
        var verticalLift = 1f - MathF.Abs(yNorm - 0.5f) * 0.25f;
        return ScaleRgb(c, verticalLift);
    }

    private static float SideLightShaftStrength(int x, int w)
    {
        var left = 1f - Math.Clamp(x / (float)LightShaftPx, 0f, 1f);
        var right = 1f - Math.Clamp((w - 1 - x) / (float)LightShaftPx, 0f, 1f);
        var s = Math.Max(left, right);
        return s * s;
    }

    private static float CornerGlowStrength(int x, int y, int w, int h)
    {
        const float radius = 110f;
        float Best(int cx, int cy)
        {
            var dx = x - cx;
            var dy = y - cy;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            var t = 1f - Math.Clamp(d / radius, 0f, 1f);
            return t * t;
        }

        return Math.Max(
            Math.Max(Best(0, 0), Best(w - 1, 0)),
            Math.Max(Best(0, h - 1), Best(w - 1, h - 1)));
    }

    private static float HoloGridModulation(int x, int y)
    {
        // Soft digital mesh (~12px cells) — strongest near edges where rimT already lifts.
        const int cell = 12;
        var onLine = (x % cell <= 1) || (y % cell <= 1);
        return onLine ? 1f : 0f;
    }

    private static int MinEdgeDistance(int x, int y, int w, int h) =>
        Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y));

    private static bool LooksLikeLoreCream(Rgba32 p)
    {
        // Cream / mint panels: bright, low-mid saturation, warm or pale-green.
        var max = Math.Max(p.R, Math.Max(p.G, p.B));
        var min = Math.Min(p.R, Math.Min(p.G, p.B));
        if (max < 150)
            return false;
        if (max - min > 70)
            return false;
        // Avoid treating bright cyan OF rim as cream.
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
        // Warm gold / bronze chrome near the art window or lore outer rim.
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

    private static Rgba32 ScaleRgb(Rgba32 c, float scale)
    {
        scale = Math.Clamp(scale, 0.75f, 1.15f);
        var r = (byte)Math.Clamp((int)MathF.Round(c.R * scale), 0, 255);
        var g = (byte)Math.Clamp((int)MathF.Round(c.G * scale), 0, 255);
        var b = (byte)Math.Clamp((int)MathF.Round(c.B * scale), 0, 255);
        return new Rgba32(r, g, b, c.A);
    }
}

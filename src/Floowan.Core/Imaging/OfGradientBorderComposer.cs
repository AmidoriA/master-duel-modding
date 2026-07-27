using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Derives official-OF-style frame templates with a bright gradient / light-leak outer
/// rim from the solid <c>card_frame*</c> PNGs. Keeps the transparent art window, gold art
/// rim, and lore cream intact so Mirrorjade soft lore compose
/// (<see cref="OverFrameAutoArtComposer.TextBoxFrameOpacity"/>) continues to work.
/// <para>
/// Visual target (sampled from official OF screenshots): Linkage teal/seafoam + holo grid
/// + iridescent shimmer; Magician of Black Chaos cyan corner leaks over deeper blue-grey
/// edges; Chaos Soldier pale blue/white vertical light shafts on the L/R margins.
/// </para>
/// </summary>
public static class OfGradientBorderComposer
{
    /// <summary>Outer rim band (px from canvas edge) with strongest light leak.</summary>
    public const int OuterRimPx = 64;

    /// <summary>Side light-shaft width (Chaos Soldier–style pale vertical glow).</summary>
    public const int LightShaftPx = 78;

    /// <summary>Corner radial glow radius (Magician of Black Chaos–style).</summary>
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

                // Lore cream / mint stay vanilla for Mirrorjade soft underlay.
                if (loreCream.Contains(x, y) && LooksLikeLoreCream(src))
                    continue;

                // Preserve warm gold art-window / lore rims.
                if (LooksLikeGoldRim(src, artWindow, loreCream, x, y))
                    continue;

                var edgeDist = MinEdgeDistance(x, y, w, h);
                var rimT = EaseOut01(1f - Math.Clamp(edgeDist / (float)OuterRimPx, 0f, 1f));
                var shaftT = SideLightShaftStrength(x, w) * palette.ShaftBoost;
                var cornerT = CornerGlowStrength(x, y, w, h) * palette.CornerBoost;
                // How "outer margin" this chrome is (vs recessed name-bar plate).
                var marginT = Math.Clamp(rimT * 0.55f + shaftT * 0.55f + cornerT * 0.45f, 0f, 1f);
                var bodyTint = palette.BodyTint;

                // Near-full replacement on the outer rim / shafts / corners; milder on
                // inner plate chrome so name-bar structure stays readable.
                var mix = Math.Clamp(
                    bodyTint + (1f - bodyTint) * marginT * palette.ReplaceBoost,
                    0f,
                    0.97f);
                if (mix < 0.08f)
                    continue;

                var target = SampleLuminousColor(palette, x, y, w, h, rimT, shaftT, cornerT);
                var outPix = LerpRgb(src, target, mix);

                // Soft screen-style glow (capped) — brightens without clipping to pure white.
                var glow = 0.06f * rimT + 0.10f * Math.Clamp(cornerT, 0f, 1f) + 0.07f * Math.Clamp(shaftT, 0f, 1f);
                outPix = ScreenTowardWhite(outPix, glow);

                if (palette.UseHoloGrid)
                    outPix = ApplyHoloGrid(outPix, x, y, marginT);

                if (palette.UseIridescence)
                    outPix = ApplyIridescence(outPix, x, y, w, h, marginT);

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

    /// <param name="BodyTint">Minimum mix toward OF color even on inner plate chrome.</param>
    /// <param name="ReplaceBoost">Scales margin replacement (1 = full; &gt;1 clamps).</param>
    /// <param name="ShaftBoost">Emphasize Chaos Soldier–style L/R shafts.</param>
    /// <param name="CornerBoost">Emphasize Magician of Black Chaos corner leaks.</param>
    private readonly record struct BorderPalette(
        Rgba32 Edge,
        Rgba32 Mid,
        Rgba32 HotCorner,
        Rgba32 Shaft,
        Rgba32 IridescentA,
        Rgba32 IridescentB,
        bool UseHoloGrid,
        bool UseIridescence,
        float BodyTint,
        float ReplaceBoost,
        float ShaftBoost,
        float CornerBoost);

    private static BorderPalette ResolvePalette(CardFrameStyle baseStyle) => baseStyle switch
    {
        // Linkage OF — bright seafoam/teal rim, warm iridescent corners, fine holo grid.
        // Rim samples from official screenshot lean ~ (160–220, 170–220, 160–200).
        CardFrameStyle.Spell => new(
            Edge: new Rgba32(168, 235, 220, 255),
            Mid: new Rgba32(36, 150, 155, 255),
            HotCorner: new Rgba32(255, 210, 175, 255),
            Shaft: new Rgba32(200, 250, 240, 255),
            IridescentA: new Rgba32(255, 190, 210, 255),
            IridescentB: new Rgba32(210, 230, 120, 255),
            UseHoloGrid: true,
            UseIridescence: true,
            BodyTint: 0.38f,
            ReplaceBoost: 1.10f,
            ShaftBoost: 1.00f,
            CornerBoost: 1.10f),

        // Magician of Black Chaos OF — luminous cyan corners over deeper blue-grey edges.
        CardFrameStyle.Ritual or CardFrameStyle.PendulumRitual => new(
            Edge: new Rgba32(90, 150, 175, 255),
            Mid: new Rgba32(28, 48, 72, 255),
            HotCorner: new Rgba32(160, 255, 250, 255),
            Shaft: new Rgba32(120, 220, 235, 255),
            IridescentA: new Rgba32(180, 255, 255, 255),
            IridescentB: new Rgba32(120, 180, 255, 255),
            UseHoloGrid: false,
            UseIridescence: false,
            BodyTint: 0.28f,
            ReplaceBoost: 1.20f,
            ShaftBoost: 0.85f,
            CornerBoost: 1.45f),

        // Chaos Soldier OF — pale blue/white vertical light shafts on L/R margins.
        // Keep mid blue-grey structure; avoid clipping shafts to pure white.
        CardFrameStyle.Effect or CardFrameStyle.PendulumEffect => new(
            Edge: new Rgba32(185, 210, 235, 255),
            Mid: new Rgba32(70, 85, 115, 255),
            HotCorner: new Rgba32(230, 245, 255, 255),
            Shaft: new Rgba32(200, 225, 245, 255),
            IridescentA: new Rgba32(190, 220, 245, 255),
            IridescentB: new Rgba32(255, 235, 200, 255),
            UseHoloGrid: false,
            UseIridescence: false,
            BodyTint: 0.18f,
            ReplaceBoost: 1.15f,
            ShaftBoost: 1.35f,
            CornerBoost: 1.10f),

        CardFrameStyle.Trap or CardFrameStyle.Token or CardFrameStyle.PendulumToken => new(
            Edge: new Rgba32(235, 170, 220, 255),
            Mid: new Rgba32(95, 35, 105, 255),
            HotCorner: new Rgba32(255, 220, 240, 255),
            Shaft: new Rgba32(245, 195, 235, 255),
            IridescentA: new Rgba32(255, 180, 220, 255),
            IridescentB: new Rgba32(200, 160, 255, 255),
            UseHoloGrid: false,
            UseIridescence: true,
            BodyTint: 0.32f,
            ReplaceBoost: 1.10f,
            ShaftBoost: 1.05f,
            CornerBoost: 1.20f),

        CardFrameStyle.Fusion or CardFrameStyle.PendulumFusion => new(
            Edge: new Rgba32(220, 175, 255, 255),
            Mid: new Rgba32(85, 40, 125, 255),
            HotCorner: new Rgba32(255, 230, 255, 255),
            Shaft: new Rgba32(235, 200, 255, 255),
            IridescentA: new Rgba32(255, 200, 255, 255),
            IridescentB: new Rgba32(180, 160, 255, 255),
            UseHoloGrid: false,
            UseIridescence: false,
            BodyTint: 0.30f,
            ReplaceBoost: 1.10f,
            ShaftBoost: 1.05f,
            CornerBoost: 1.25f),

        CardFrameStyle.Synchro or CardFrameStyle.PendulumSynchro => new(
            Edge: new Rgba32(245, 248, 255, 255),
            Mid: new Rgba32(150, 155, 165, 255),
            HotCorner: new Rgba32(255, 255, 255, 255),
            Shaft: new Rgba32(250, 252, 255, 255),
            IridescentA: new Rgba32(255, 255, 255, 255),
            IridescentB: new Rgba32(220, 230, 255, 255),
            UseHoloGrid: false,
            UseIridescence: false,
            BodyTint: 0.28f,
            ReplaceBoost: 1.10f,
            ShaftBoost: 1.10f,
            CornerBoost: 1.15f),

        CardFrameStyle.Xyz or CardFrameStyle.PendulumXyz => new(
            Edge: new Rgba32(170, 210, 255, 255),
            Mid: new Rgba32(18, 20, 32, 255),
            HotCorner: new Rgba32(235, 245, 255, 255),
            Shaft: new Rgba32(190, 220, 255, 255),
            IridescentA: new Rgba32(200, 230, 255, 255),
            IridescentB: new Rgba32(160, 180, 255, 255),
            UseHoloGrid: true,
            UseIridescence: false,
            BodyTint: 0.25f,
            ReplaceBoost: 1.15f,
            ShaftBoost: 1.20f,
            CornerBoost: 1.25f),

        CardFrameStyle.Link => new(
            Edge: new Rgba32(120, 230, 245, 255),
            Mid: new Rgba32(22, 48, 72, 255),
            HotCorner: new Rgba32(210, 250, 255, 255),
            Shaft: new Rgba32(160, 240, 250, 255),
            IridescentA: new Rgba32(180, 255, 255, 255),
            IridescentB: new Rgba32(140, 200, 255, 255),
            UseHoloGrid: true,
            UseIridescence: true,
            BodyTint: 0.35f,
            ReplaceBoost: 1.15f,
            ShaftBoost: 1.10f,
            CornerBoost: 1.20f),

        CardFrameStyle.Normal or CardFrameStyle.PendulumNormal => new(
            Edge: new Rgba32(255, 235, 175, 255),
            Mid: new Rgba32(175, 145, 70, 255),
            HotCorner: new Rgba32(255, 252, 230, 255),
            Shaft: new Rgba32(255, 245, 200, 255),
            IridescentA: new Rgba32(255, 240, 200, 255),
            IridescentB: new Rgba32(255, 220, 160, 255),
            UseHoloGrid: false,
            UseIridescence: false,
            BodyTint: 0.28f,
            ReplaceBoost: 1.10f,
            ShaftBoost: 1.05f,
            CornerBoost: 1.15f),

        _ => new(
            Edge: new Rgba32(210, 225, 245, 255),
            Mid: new Rgba32(55, 70, 100, 255),
            HotCorner: new Rgba32(245, 252, 255, 255),
            Shaft: new Rgba32(230, 240, 255, 255),
            IridescentA: new Rgba32(200, 230, 255, 255),
            IridescentB: new Rgba32(255, 240, 210, 255),
            UseHoloGrid: false,
            UseIridescence: false,
            BodyTint: 0.25f,
            ReplaceBoost: 1.15f,
            ShaftBoost: 1.20f,
            CornerBoost: 1.20f),
    };

    private static Rgba32 SampleLuminousColor(
        BorderPalette palette,
        int x,
        int y,
        int w,
        int h,
        float rimT,
        float shaftT,
        float cornerT)
    {
        // Deeper mid on straight edges; luminous edge toward the rim.
        var edgeMix = Math.Clamp(0.20f + 0.70f * rimT, 0f, 1f);
        var c = LerpRgb(palette.Mid, palette.Edge, edgeMix);

        // Chaos Soldier vertical shafts — lift L/R margins without erasing mid tone.
        c = LerpRgb(c, palette.Shaft, Math.Clamp(shaftT * 0.62f, 0f, 0.85f));

        // Magician of Black Chaos corner leaks — brightest at corners, not full replace.
        c = LerpRgb(c, palette.HotCorner, Math.Clamp(cornerT * 0.55f, 0f, 0.80f));

        // Soft top/bottom rim emphasis (official OFs glow along the card silhouette).
        var yNorm = y / (float)Math.Max(1, h - 1);
        var topBottom = MathF.Pow(1f - MathF.Abs(yNorm - 0.5f) * 2f, 1.6f);
        if (rimT > 0.2f)
            c = LerpRgb(c, palette.Edge, topBottom * rimT * 0.28f);

        // Soft procedural mottle so the rim isn't a flat fill.
        var mottle = SoftMottle(x, y);
        c = ScaleRgb(c, 0.94f + 0.12f * mottle);

        return c;
    }

    private static Rgba32 ApplyHoloGrid(Rgba32 c, int x, int y, float marginT)
    {
        // Fine square mesh (~8px) — Linkage digital holo feel.
        const int cell = 8;
        var onV = x % cell == 0;
        var onH = y % cell == 0;
        if (!onV && !onH)
            return c;

        var strength = (0.28f + 0.45f * marginT) * (onV && onH ? 1f : 0.75f);
        var gridTint = new Rgba32(210, 255, 245, 255);
        return LerpRgb(c, gridTint, strength);
    }

    private static Rgba32 ApplyIridescence(Rgba32 c, int x, int y, int w, int h, float marginT)
    {
        // Slow rainbow shimmer along the rim (pink ↔ yellow-green), Linkage-style.
        var ang = (x * 0.017f) + (y * 0.011f);
        var wave = 0.5f + 0.5f * MathF.Sin(ang);
        var a = new Rgba32(255, 185, 205, 255);
        var b = new Rgba32(215, 235, 130, 255);
        var shimmer = LerpRgb(a, b, wave);
        var amount = 0.12f + 0.28f * marginT;
        // Bias shimmer toward brighter channels without crushing teal mid.
        return LerpRgb(c, shimmer, amount * 0.55f);
    }

    private static float SoftMottle(int x, int y)
    {
        // Cheap value-noise stand-in (no allocations).
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
        var s = Math.Max(left, right);
        // Keep shafts strong through most of the band, then fall off.
        return EaseOut01(s);
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

    private static Rgba32 ScaleRgb(Rgba32 c, float scale)
    {
        scale = Math.Clamp(scale, 0.70f, 1.28f);
        var r = (byte)Math.Clamp((int)MathF.Round(c.R * scale), 0, 255);
        var g = (byte)Math.Clamp((int)MathF.Round(c.G * scale), 0, 255);
        var b = (byte)Math.Clamp((int)MathF.Round(c.B * scale), 0, 255);
        return new Rgba32(r, g, b, c.A);
    }

    /// <summary>Screen-blend toward white by <paramref name="amount"/> (0–1), preserving hue.</summary>
    private static Rgba32 ScreenTowardWhite(Rgba32 c, float amount)
    {
        amount = Math.Clamp(amount, 0f, 0.32f);
        if (amount <= 1e-5f)
            return c;
        var r = (byte)Math.Clamp((int)MathF.Round(c.R + (255 - c.R) * amount), 0, 255);
        var g = (byte)Math.Clamp((int)MathF.Round(c.G + (255 - c.G) * amount), 0, 255);
        var b = (byte)Math.Clamp((int)MathF.Round(c.B + (255 - c.B) * amount), 0, 255);
        return new Rgba32(r, g, b, c.A);
    }
}

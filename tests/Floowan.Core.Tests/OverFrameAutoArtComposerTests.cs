using Floowan.Core.Assets;
using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class OverFrameAutoArtComposerTests
{
    [Fact]
    public void Compose_PunchesFullRectangle_AndSilhouetteOutsideFrame()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(220, 30, 20, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        Assert.Equal(OverFrameConstants.Width, result.Width);
        Assert.Equal(OverFrameConstants.Height, result.Height);

        // Corner chrome far from the subject stays opaque frame.
        var chrome = result[20, 40];
        Assert.True(chrome.R > 180 && chrome.G > 180 && chrome.B < 80, $"chrome={chrome}");
        Assert.True(chrome.A >= 200);

        // Art window keeps the original illustration (foil-mask), not a black matte.
        var windowCorner = result[
            OverFrameAutoArtComposer.ArtWindow.Left + 8,
            OverFrameAutoArtComposer.ArtWindow.Top + 8];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, windowCorner.A);
        Assert.True(windowCorner.R > 100, $"expected source-art RGB in window, got {windowCorner}");

        // Subject inside the window is foil-mask alpha.
        var center = result[
            OverFrameAutoArtComposer.ArtWindow.Left + OverFrameAutoArtComposer.ArtWindow.Width / 2,
            OverFrameAutoArtComposer.ArtWindow.Top + OverFrameAutoArtComposer.ArtWindow.Height / 2];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, center.A);
        Assert.True(center.R > 100, $"center={center}");

        // Overflow above the art window: frame is punched (creature alpha), not opaque sticker on chrome.
        var overflowY = FindSubjectAboveArtWindow(result);
        Assert.True(overflowY >= 0, "expected subject overflow above art window");
        var overflow = result[result.Width / 2, overflowY];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, overflow.A);
        Assert.True(overflow.R > 100, $"overflow={overflow}");
    }

    [Fact]
    public void Compose_ScalesBackgroundWithSubject()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 80, 200, 255));
        for (var y = 0; y < 100; y++)
            source[35, y] = new Rgba32(0, 255, 255, 255); // cyan marker just left of subject
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
            source[x, y] = new Rgba32(220, 30, 20, 255);

        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var win = OverFrameAutoArtComposer.ArtWindow;
        var midY = win.Top + win.Height / 2;

        // Left edge of the red subject inside the art window.
        var subjectLeft = -1;
        for (var x = win.Left; x < win.Right; x++)
        {
            var p = result[x, midY];
            if (p.A == OverFrameAutoArtComposer.FoilMaskAlpha && p.R > 180 && p.G < 80)
            {
                subjectLeft = x;
                break;
            }
        }

        Assert.True(subjectLeft > win.Left, $"expected subject in window, left={subjectLeft}");

        // Marker is 5 source px left of subject — with matching scale it lands ~5*scale px left.
        var expectedGap = (int)MathF.Round(5 * ComputeExpectedScale(100, 100));
        Assert.True(expectedGap > 8, $"expected meaningful overflow scale gap, got {expectedGap}");

        var foundCyan = false;
        for (var dx = expectedGap - 4; dx <= expectedGap + 4; dx++)
        {
            var x = subjectLeft - dx;
            if (x < win.Left || x >= win.Right)
                continue;
            var marker = result[x, midY];
            if (marker.A == OverFrameAutoArtComposer.FoilMaskAlpha &&
                marker.G > 180 && marker.B > 180 && marker.R < 80)
            {
                foundCyan = true;
                break;
            }
        }

        Assert.True(foundCyan,
            $"expected cyan background ~{expectedGap}px left of subject (aligned scale), subjectLeft={subjectLeft}");
    }

    [Fact]
    public void Compose_FillsArtWindow_WithoutBlackGaps()
    {
        // Tall portrait source (character-like). Old subject-centered contain crop left
        // black side gaps in the square art window.
        using var source = new Image<Rgba32>(200, 400, new Rgba32(30, 180, 90, 255));
        for (var y = 40; y < 360; y++)
        for (var x = 70; x < 130; x++)
            source[x, y] = new Rgba32(220, 40, 30, 255);

        using var mask = new Image<L8>(200, 400, new L8(0));
        for (var y = 40; y < 360; y++)
        for (var x = 70; x < 130; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var win = OverFrameAutoArtComposer.ArtWindow;
        foreach (var (x, y) in new[]
                 {
                     (win.Left + 4, win.Top + 4),
                     (win.Right - 5, win.Top + 4),
                     (win.Left + 4, win.Bottom - 5),
                     (win.Right - 5, win.Bottom - 5)
                 })
        {
            var p = result[x, y];
            Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, p.A);
            Assert.False(p.R < 20 && p.G < 20 && p.B < 20,
                $"art window corner ({x},{y}) should not be black matte, got {p}");
            Assert.True(p.G > 80 || p.R > 80, $"expected source-derived color at ({x},{y}), got {p}");
        }
    }

    private static float ComputeExpectedScale(int sourceWidth, int sourceHeight)
    {
        var cover = Math.Max(
            OverFrameAutoArtComposer.ArtWindow.Width / (float)sourceWidth,
            OverFrameAutoArtComposer.ArtWindow.Height / (float)sourceHeight);
        return cover * OverFrameAutoArtComposer.OverflowScale;
    }

    [Fact]
    public void Compose_RejectsMaskWithoutVisibleSubject()
    {
        using var source = new Image<Rgba32>(32, 32, Color.White);
        using var mask = new Image<L8>(32, 32, new L8(0));
        using var frame = CreateSolidFrame();

        var error = Assert.Throws<InvalidOperationException>(
            () => OverFrameAutoArtComposer.Compose(source, mask, frame));

        Assert.Contains("visible subject", error.Message);
    }

    [Fact]
    public void Compose_RejectsNearlyOpaqueCutout()
    {
        using var source = new Image<Rgba32>(64, 64, Color.White);
        using var mask = new Image<L8>(64, 64, new L8(255));
        using var frame = CreateSolidFrame();

        var error = Assert.Throws<InvalidOperationException>(
            () => OverFrameAutoArtComposer.Compose(source, mask, frame));

        Assert.Contains("opaque", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetectArtWindow_ReadsRealEffectFrameHole()
    {
        var framesDir = Path.Combine(AppContext.BaseDirectory, "frames");
        if (!Directory.Exists(framesDir))
        {
            framesDir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "src", "Floowan.Core", "Resources", "frames"));
        }

        Assert.True(File.Exists(Path.Combine(framesDir, "Effect.png")), $"Effect.png missing under {framesDir}");

        using var effect = Image.Load<Rgba32>(Path.Combine(framesDir, "Effect.png"));
        var hole = OverFrameAutoArtComposer.DetectArtWindow(effect);

        Assert.False(hole.IsEmpty);
        Assert.InRange(hole.Width, 520, 560);
        Assert.InRange(hole.Height, 520, 560);
        Assert.True(hole.Left < 100 && hole.Top < 200, $"unexpected hole origin {hole}");
    }

    [Fact]
    public void Compose_FillsDetectedFrameHole_NotHardcoded512()
    {
        // Frame hole larger than old 512² ArtWindow — art must reach the hole edge.
        using var frame = CreateSolidFrame();
        var hole = new Rectangle(80, 180, 540, 540);
        ClearRect(frame, hole);

        using var source = new Image<Rgba32>(512, 512, new Rgba32(40, 200, 120, 255));
        using var mask = new Image<L8>(512, 512, new L8(0));
        for (var y = 80; y < 432; y++)
        for (var x = 180; x < 332; x++)
            mask[x, y] = new L8(255);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        foreach (var (x, y) in new[]
                 {
                     (hole.Left + 2, hole.Top + 2),
                     (hole.Right - 3, hole.Top + 2),
                     (hole.Left + 2, hole.Bottom - 3),
                     (hole.Right - 3, hole.Bottom - 3)
                 })
        {
            var p = result[x, y];
            Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, p.A);
            Assert.True(p.G > 100, $"hole edge ({x},{y}) should be filled art, got {p}");
        }
    }

    [Fact]
    public void DetectTextBox_FindsLorePanelBelowArtHole()
    {
        var framesDir = Path.Combine(AppContext.BaseDirectory, "frames");
        if (!Directory.Exists(framesDir))
        {
            framesDir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "src", "Floowan.Core", "Resources", "frames"));
        }

        using var effect = Image.Load<Rgba32>(Path.Combine(framesDir, "Effect.png"));
        var art = OverFrameAutoArtComposer.DetectArtWindow(effect);
        var text = OverFrameAutoArtComposer.DetectTextBox(effect, art);

        Assert.False(text.IsEmpty);
        Assert.True(text.Top > art.Bottom,
            $"lore panel should start below type-line strip: art={art} text={text}");
        // Real Effect lore cream starts ~48px under the hole; must not latch onto
        // sparse bright pixels in the gold border (~25px earlier).
        Assert.InRange(text.Top - art.Bottom, 40, 120);
        Assert.InRange(text.Height, 150, 280);
        // Cream lore is slightly wider than the art hole on real MD frames.
        Assert.True(text.Left <= art.Left, $"lore left {text.Left} should be ≤ art left {art.Left}");
        Assert.True(text.Right >= art.Right, $"lore right {text.Right} should be ≥ art right {art.Right}");

        var outer = OverFrameAutoArtComposer.DetectLoreOuterBorder(effect, text);
        Assert.True(outer.Left < text.Left, $"outer lore left {outer.Left} should be past cream {text.Left}");
        Assert.True(outer.Right > text.Right, $"outer lore right {outer.Right} should be past cream {text.Right}");
        Assert.InRange(text.Left - outer.Left, 4, 55);
        Assert.InRange(outer.Right - text.Right, 4, 55);

        var cutTop = OverFrameAutoArtComposer.DetectLoreOuterTop(effect, text, art);
        Assert.True(cutTop < text.Top, $"outer cut {cutTop} must be above cream {text.Top}");
        Assert.InRange(text.Top - cutTop, 4, 20);
    }

    [Fact]
    public void Compose_TypeLineStrip_MeetsOuterLoreBorder_NotJustArtHole()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 40; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(20, 200, 50, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        var hole = new Rectangle(89, 191, 527, 400);
        ClearRect(frame, hole);

        // Gold outer lore rim, then cream interior (wider than... cream starts inset).
        var gold = new Rgba32(180, 100, 50, 255);
        var cream = new Rgba32(233, 207, 183, 255);
        for (var y = hole.Bottom; y < hole.Bottom + 40; y++)
        for (var x = hole.Left - 30; x < hole.Right + 30; x++)
        {
            if (x >= 0 && x < frame.Width)
                frame[x, y] = new Rgba32(60, 40, 30, 255);
        }

        for (var y = hole.Bottom + 40; y < hole.Bottom + 200; y++)
        {
            for (var x = hole.Left - 30; x < hole.Left; x++)
                if (x >= 0) frame[x, y] = gold;
            for (var x = hole.Left; x < hole.Right; x++)
                frame[x, y] = cream;
            for (var x = hole.Right; x < hole.Right + 30; x++)
                if (x < frame.Width) frame[x, y] = gold;
        }

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        // Just left of the art hole, in the type-line band — must be foil art (outer lore width), not orange chrome.
        var wing = result[hole.Left - 20, hole.Bottom + 20];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, wing.A);
        Assert.True(wing.G > 80, $"expected foil art out to outer lore wing, got {wing}");
    }

    [Fact]
    public void DetectTextBox_IgnoresSparseBrightPixelsOnGoldBorder()
    {
        using var frame = CreateSolidFrame();
        var hole = new Rectangle(89, 191, 527, 400);
        ClearRect(frame, hole);

        // Type-line + gold border with a few bright speckles (old detector used these).
        for (var y = hole.Bottom; y < hole.Bottom + 40; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = new Rgba32(60, 40, 30, 255);
        for (var x = hole.Left; x < hole.Left + 10; x++)
            frame[x, hole.Bottom + 10] = new Rgba32(220, 200, 180, 255);

        var cream = new Rgba32(233, 207, 183, 255);
        for (var y = hole.Bottom + 40; y < hole.Bottom + 200; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = cream;

        var text = OverFrameAutoArtComposer.DetectTextBox(frame, hole);
        Assert.Equal(hole.Bottom + 40, text.Top);
    }

    [Fact]
    public void ExtractIllustrationSource_CropsOverFrameTextureTo512()
    {
        using var of = new Image<Rgba32>(704, 1024, new Rgba32(30, 30, 30, 255));
        var window = OverFrameAutoArtComposer.ArtWindow;
        for (var y = window.Top; y < window.Bottom; y++)
        for (var x = window.Left; x < window.Right; x++)
            of[x, y] = new Rgba32(10, 200, 20, 4);

        using var extracted = OverFrameAutoArtComposer.ExtractIllustrationSource(of);
        Assert.Equal(512, extracted.Width);
        Assert.Equal(512, extracted.Height);
        Assert.True(extracted[256, 256].G > 100, $"expected green art hole content, got {extracted[256, 256]}");
    }

    [Fact]
    public void Compose_PunchesTypeLineStrip_ButNotLorePanel()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 80, 200, 255));
        for (var y = 0; y < 100; y++)
        for (var x = 40; x < 60; x++)
            source[x, y] = new Rgba32(220, 30, 20, 255);

        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 100; y++)
        for (var x = 40; x < 60; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        var hole = new Rectangle(89, 191, 527, 400);
        ClearRect(frame, hole);
        // Dark type-line strip, then cream lore (as on real Effect frames).
        for (var y = hole.Bottom; y < hole.Bottom + 40; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = new Rgba32(60, 40, 30, 255);
        var cream = new Rgba32(233, 207, 183, 255);
        for (var y = hole.Bottom + 40; y < hole.Bottom + 200; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = cream;

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var midX = hole.Left + hole.Width / 2;
        // Type-line strip: foil art meets lore (not solid frame brown).
        var typeLine = result[midX, hole.Bottom + 20];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, typeLine.A);
        Assert.False(typeLine.R < 80 && typeLine.G < 60,
            $"type-line must not stay dark frame chrome, got {typeLine}");

        // Lore panel: stays cream / opaque.
        var lore = result[midX, hole.Bottom + 80];
        Assert.True(lore.A >= 200, $"lore must stay opaque, got {lore}");
        Assert.True(Math.Abs(lore.R - cream.R) < 40, $"expected lore cream, got {lore}");
    }

    [Fact]
    public void Compose_TypeLineStrip_AlwaysShowsArt_EvenWithoutSubjectThere()
    {
        // Subject only in the upper art hole — type-line must still be foil art, not chrome.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 30; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(20, 200, 50, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        var hole = new Rectangle(89, 191, 527, 400);
        ClearRect(frame, hole);
        for (var y = hole.Bottom; y < hole.Bottom + 40; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = new Rgba32(60, 40, 30, 255);
        var cream = new Rgba32(233, 207, 183, 255);
        for (var y = hole.Bottom + 40; y < hole.Bottom + 200; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = cream;

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var midX = hole.Left + hole.Width / 2;
        var typeLine = result[midX, hole.Bottom + 20];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, typeLine.A);
        Assert.True(typeLine.G > 100, $"expected foil art in type-line gap, got {typeLine}");

        var loreTop = result[midX, hole.Bottom + 40];
        Assert.True(loreTop.A >= 200, $"lore top must stay opaque, got {loreTop}");
        Assert.True(Math.Abs(loreTop.R - cream.R) < 40, $"expected cream at lore top, got {loreTop}");
    }

    [Fact]
    public void Compose_CutsSubjectAtLoreTop_NoSideBleedPastLore()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 80, 200, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        // Wide subject columns on both sides — without a lore-top cut they cover outer chrome.
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 100; x++)
        {
            if (x is >= 5 and < 25 or >= 75 and < 95)
            {
                source[x, y] = new Rgba32(240, 20, 20, 255);
                mask[x, y] = new L8(255);
            }
        }

        using var frame = CreateSolidFrame();
        var hole = new Rectangle(89, 191, 527, 400);
        ClearRect(frame, hole);
        var cream = new Rgba32(233, 207, 183, 255);
        for (var y = hole.Bottom + 40; y < hole.Bottom + 200; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = cream;
        for (var y = hole.Bottom; y < hole.Bottom + 40; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = new Rgba32(60, 40, 30, 255);

        // Outer chrome left of lore.
        var chrome = new Rgba32(180, 90, 40, 255);
        for (var y = hole.Bottom + 40; y < hole.Bottom + 200; y++)
        for (var x = 10; x < hole.Left - 5; x++)
            frame[x, y] = chrome;

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var loreTop = hole.Bottom + 40;
        var side = result[20, loreTop + 30];
        Assert.True(side.A >= 200, $"outer chrome below lore top must stay frame, got {side}");
        Assert.True(Math.Abs(side.R - chrome.R) < 40, $"expected outer chrome, got {side}");

        var lore = result[hole.Left + hole.Width / 2, loreTop + 30];
        Assert.True(lore.A >= 200, $"lore interior must stay opaque, got {lore}");
        Assert.True(Math.Abs(lore.R - cream.R) < 40, $"expected lore cream, got {lore}");
    }

    [Fact]
    public void Compose_CutsRembgOutOfLoreBox_NoHardVerticalBleed()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 80, 200, 255));
        // Bright vertical stripe in source (would look like a glitch line if cutout bled into lore).
        for (var y = 0; y < 100; y++)
            source[50, y] = new Rgba32(255, 0, 255, 255);
        for (var y = 0; y < 100; y++)
        for (var x = 40; x < 60; x++)
            if (x != 50)
                source[x, y] = new Rgba32(220, 30, 20, 255);

        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 100; y++)
        for (var x = 40; x < 60; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        var hole = new Rectangle(89, 191, 527, 400);
        ClearRect(frame, hole);
        var cream = new Rgba32(233, 207, 183, 255);
        for (var y = hole.Bottom; y < hole.Bottom + 200; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = cream;

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var midX = hole.Left + hole.Width / 2;
        var textY = hole.Bottom + 40;
        var lore = result[midX, textY];

        Assert.True(lore.A >= 200, $"lore must be opaque, got {lore}");
        // Panel-dominant: must not be pure magenta cutout stripe.
        Assert.True(lore.R < 250 || lore.B < 250, $"magenta cutout bled into lore: {lore}");
        Assert.True(Math.Abs(lore.R - cream.R) < 40 && Math.Abs(lore.G - cream.G) < 40,
            $"expected cream-dominant lore panel, got {lore}");

        var inArt = result[midX, hole.Top + hole.Height / 2];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, inArt.A);
    }

    [Fact]
    public void InferStyle_DetectsNormalAndFusionKeywords()
    {
        Assert.Equal(CardFrameStyle.Normal, CardFrameTemplates.InferStyle("Fish", "[Fish/Normal] lore"));
        Assert.Equal(CardFrameStyle.Fusion, CardFrameTemplates.InferStyle("Mirrorjade", "Fusion Monster text"));
        Assert.Equal(CardFrameStyle.Trap, CardFrameTemplates.InferStyle("Impulse", "[Trap] card text"));
        Assert.Equal(CardFrameStyle.Spell, CardFrameTemplates.InferStyle("Raigeki", "Spell Card"));
    }

    private static Image<Rgba32> CreateSolidFrame() =>
        new(OverFrameConstants.Width, OverFrameConstants.Height, new Rgba32(220, 200, 40, 255));

    private static void ClearRect(Image<Rgba32> image, Rectangle rect)
    {
        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
            image[x, y] = new Rgba32(0, 0, 0, 0);
    }

    private static int FindSubjectAboveArtWindow(Image<Rgba32> image)
    {
        var x = image.Width / 2;
        for (var y = 0; y < OverFrameAutoArtComposer.ArtWindow.Top; y++)
        {
            var p = image[x, y];
            if (p.A == OverFrameAutoArtComposer.FoilMaskAlpha && p.R > 100)
                return y;
        }

        return -1;
    }
}

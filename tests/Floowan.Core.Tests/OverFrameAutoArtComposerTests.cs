using System.IO;
using Floowan.Core.Assets;
using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class OverFrameAutoArtComposerTests
{
    [Fact]
    public void Compose_SubjectOffset_ShiftsSubjectOnly_LeavesFoilFixed()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(220, 30, 20, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        using var baseline = OverFrameAutoArtComposer.Compose(source, mask, frame);
        using var shifted = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame,
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 40,
            subjectOffsetY: 0);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var foilX = art.Left + art.Width / 2;
        var foilY = art.Top + art.Height / 2;
        Assert.Equal(baseline[foilX, foilY], shifted[foilX, foilY]);

        var overflowY = FindSubjectAboveArtWindow(baseline);
        Assert.True(overflowY >= 0, "expected baseline overflow above art window");

        var baselineX = -1;
        for (var x = 0; x < baseline.Width; x++)
        {
            var p = baseline[x, overflowY];
            if (p.A == OverFrameAutoArtComposer.FoilMaskAlpha && p.R > 100)
            {
                baselineX = x;
                break;
            }
        }

        Assert.True(baselineX >= 0, "expected baseline subject foil above art window");

        var shiftedX = -1;
        for (var x = 0; x < shifted.Width; x++)
        {
            var p = shifted[x, overflowY];
            if (p.A == OverFrameAutoArtComposer.FoilMaskAlpha && p.R > 100)
            {
                shiftedX = x;
                break;
            }
        }

        Assert.True(shiftedX >= 0, "expected shifted subject foil above art window");
        Assert.InRange(shiftedX - baselineX, 36, 44);
    }

    [Fact]
    public void Compose_SubjectOffset_KeepsEffectLoreCreamBehavior()
    {
        // subjectOffset must not skip or alter Mirrorjade lore paint (soft where art,
        // solid where uncovered). Custom OF drag-release recompose uses the same path.
        using var source = new Image<Rgba32>(512, 512, new Rgba32(40, 200, 255, 255));
        using var mask = new Image<L8>(512, 512, new L8(0));
        for (var y = 20; y < 492; y++)
        for (var x = 40; x < 472; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var baseline = OverFrameAutoArtComposer.Compose(source, mask, frame.Clone());
        using var shifted = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 40,
            subjectOffsetY: 60);
        using var fullEffect = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.Effect);
        using var baseOnly = OverFrameAutoArtComposer.ComposeBaseWithoutSubject(
            source, mask, CardFrameStyle.Effect);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;
        var coveredY = creamR.Top + 20;
        var uncoveredY = creamR.Bottom - 10;

        Assert.Equal(baseline[midX, coveredY], shifted[midX, coveredY]);
        Assert.Equal(baseline[midX, uncoveredY], shifted[midX, uncoveredY]);
        Assert.Equal(cream, baseline[midX, uncoveredY]);
        Assert.True(
            baseline[midX, coveredY].B > cream.B,
            $"covered lore must stay soft Mirrorjade underlay, got {baseline[midX, coveredY]}");

        // Drag base layer uses the same lore paint path as full compose (foil fixed).
        Assert.Equal(fullEffect[midX, coveredY], baseOnly[midX, coveredY]);
        Assert.Equal(fullEffect[midX, uncoveredY], baseOnly[midX, uncoveredY]);
    }

    [Fact]
    public void Compose_AlphaCutout_LoreCream_SolidWhereSourceTransparent()
    {
        // Custom OF alpha cutouts must not dim lore by soft-blending over zero-alpha RGB.
        using var source = new Image<Rgba32>(704, 1024, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(704, 1024, new L8(0));
        for (var y = 50; y < 900; y++)
        for (var x = 300; x < 400; x++)
        {
            source[x, y] = new Rgba32(40, 200, 255, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);
        using var shifted = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 50,
            subjectOffsetY: 40);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var leftX = creamR.Left + 30;
        var yTop = creamR.Top + 20;
        var left = result[leftX, yTop];
        Assert.Equal(cream, left);
        Assert.Equal(result[leftX, yTop], shifted[leftX, yTop]);
    }

    [Fact]
    public void LoadSubjectFromAlpha_UsesExistingAlphaAsMask()
    {
        var path = Path.Combine(Path.GetTempPath(), $"floowan-alpha-subject-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Image<Rgba32>(64, 64, new Rgba32(0, 0, 0, 0)))
            {
                for (var y = 8; y < 56; y++)
                for (var x = 20; x < 44; x++)
                    image[x, y] = new Rgba32(10, 200, 40, 255);
                image.SaveAsPng(path);
            }

            var (source, mask) = AutoOverFrameArtService.LoadSubjectFromAlpha(path);
            using (source)
            using (mask)
            {
                Assert.Equal(64, source.Width);
                Assert.Equal(64, mask.Width);
                Assert.Equal(255, mask[30, 30].PackedValue);
                Assert.Equal(0, mask[0, 0].PackedValue);
                Assert.Equal(10, source[30, 30].R);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ComposeBaseWithoutSubject_OmitsOverflowSilhouette()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(220, 30, 20, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
            mask[x, y] = new L8(255);

        using var full = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.Effect);
        using var baseOnly = OverFrameAutoArtComposer.ComposeBaseWithoutSubject(
            source, mask, CardFrameStyle.Effect);
        using var subjectLayer = OverFrameAutoArtComposer.RenderSubjectDragLayer(
            source, mask, CardFrameStyle.Effect);

        Assert.True(FindSubjectAboveArtWindow(full) >= 0, "full compose should overflow");
        Assert.True(FindSubjectAboveArtWindow(baseOnly) < 0, "base should omit overflow subject");
        Assert.True(FindSubjectAboveArtWindow(subjectLayer) >= 0, "subject layer should overflow");
    }

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
    public void HasOverframableOverflow_False_WhenSubjectFullyInsideArtHole()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        Assert.False(OverFrameAutoArtComposer.HasOverframableOverflow(
            art, art.Left + 40, art.Top + 40, art.Right - 40, art.Bottom - 40));
    }

    [Fact]
    public void HasOverframableOverflow_True_WhenAnySideExtendsPastArtHole()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        Assert.True(OverFrameAutoArtComposer.HasOverframableOverflow(
            art, art.Left - 1, art.Top + 10, art.Right - 10, art.Bottom - 10));
        Assert.True(OverFrameAutoArtComposer.HasOverframableOverflow(
            art, art.Left + 10, art.Top + 10, art.Right + 1, art.Bottom - 10));
        Assert.True(OverFrameAutoArtComposer.HasOverframableOverflow(
            art, art.Left + 10, art.Top - 1, art.Right - 10, art.Bottom - 10));
        Assert.True(OverFrameAutoArtComposer.HasOverframableOverflow(
            art, art.Left + 10, art.Top + 10, art.Right - 10, art.Bottom + 1));
    }

    [Fact]
    public void Compose_RejectsSubjectWithNoOverframableOverflow()
    {
        // Tiny centered rembg blob — after Cover×OverflowScale it still sits entirely
        // inside the art hole (no L/R/T/B overframe).
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 45; y < 55; y++)
        for (var x = 45; x < 55; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        var error = Assert.Throws<InvalidOperationException>(
            () => OverFrameAutoArtComposer.Compose(source, mask, frame));

        Assert.Equal(OverFrameAutoArtComposer.CannotDetectSubjectMessage, error.Message);
    }

    [Fact]
    public void Compose_CustomArtOnly_AllowsSubjectWithNoOverframableOverflow()
    {
        // Same tiny in-hole subject as Auto reject — Custom OF must still compose.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 45; y < 55; y++)
        for (var x = 45; x < 55; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame,
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly);

        Assert.Equal(OverFrameConstants.Width, result.Width);
        Assert.Equal(OverFrameConstants.Height, result.Height);
    }

    [Fact]
    public void Compose_AllowsSubjectWithAnySideOverframableOverflow()
    {
        // Tall center strip — Cover×OverflowScale breaks out above/below the art hole.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        Assert.Equal(OverFrameConstants.Width, result.Width);
        Assert.Equal(OverFrameConstants.Height, result.Height);
        Assert.True(FindSubjectAboveArtWindow(result) >= 0, "expected top overframe");
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
    public void Compose_UsesSharedEffectArtWindow_EvenWhenFrameHoleIsLarger()
    {
        // Oversized hole larger than Effect — compose must still use ArtWindow.
        using var frame = CreateSolidFrame();
        var largeHole = new Rectangle(76, 178, 555, 555);
        ClearRect(frame, largeHole);

        using var source = new Image<Rgba32>(512, 512, new Rgba32(40, 200, 120, 255));
        using var mask = new Image<L8>(512, 512, new L8(0));
        // Tall enough that Cover×OverflowScale overframes the art hole (fail-closed check).
        for (var y = 0; y < 512; y++)
        for (var x = 180; x < 332; x++)
            mask[x, y] = new L8(255);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var hole = OverFrameAutoArtComposer.ArtWindow;
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

        // Outside shared ArtWindow but inside the oversized template hole → frame chrome, not art.
        var ring = result[largeHole.Left + 2, largeHole.Top + 2];
        Assert.True(ring.A >= 200, $"expected patched chrome outside ArtWindow, got {ring}");
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
    public void Compose_TypeLineStrip_DoesNotForceOuterLoreWingsWithoutSubject()
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
        var hole = OverFrameAutoArtComposer.ArtWindow;
        ClearRect(frame, hole);
        var cream = new Rgba32(233, 207, 183, 255);
        var typeLine = new Rgba32(60, 40, 30, 255);
        PaintEffectStyleLore(frame, cream);

        var cut = OverFrameAutoArtComposer.EffectLoreCutTop;
        for (var y = hole.Bottom; y < cut; y++)
        {
            for (var x = hole.Left - 30; x < hole.Left; x++)
                if (x >= 0) frame[x, y] = typeLine;
            for (var x = hole.Right; x < hole.Right + 30; x++)
                if (x < frame.Width) frame[x, y] = typeLine;
        }

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var wing = result[hole.Left - 20, (hole.Bottom + cut) / 2];
        Assert.True(wing.A >= 200, $"expected frame chrome in wing, got {wing}");
        Assert.True(Math.Abs(wing.R - typeLine.R) < 40, $"expected type-line chrome in wing, got {wing}");

        var mid = result[hole.Left + hole.Width / 2, (hole.Bottom + cut) / 2];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, mid.A);
    }

    [Fact]
    public void DetectTextBox_IgnoresSparseBrightPixelsOnGoldBorder()
    {
        using var frame = CreateSolidFrame();
        var hole = new Rectangle(89, 191, 527, 400);
        ClearRect(frame, hole);

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
            of[x, y] = new Rgba32(10, 200, 20, 255);

        using var extracted = OverFrameAutoArtComposer.ExtractIllustrationSource(of);
        Assert.Equal(512, extracted.Width);
        Assert.Equal(512, extracted.Height);
        Assert.True(extracted[256, 256].G > 100, $"expected green art hole content, got {extracted[256, 256]}");
    }

    [Fact]
    public void ExtractIllustrationSource_CropsPendulumNativeCanvasToThreeByFour()
    {
        using var pend = new Image<Rgba32>(512, 1024, new Rgba32(20, 20, 20, 255));
        for (var y = 0; y < OverFrameAutoArtComposer.PendulumIllustArtHeight; y++)
        for (var x = 0; x < 512; x++)
            pend[x, y] = new Rgba32(40, 180, 220, 255);
        // Below the 3:4 band — must not remain after extract.
        for (var y = OverFrameAutoArtComposer.PendulumIllustArtHeight; y < 1024; y++)
        for (var x = 0; x < 512; x++)
            pend[x, y] = new Rgba32(233, 207, 183, 255);

        using var extracted = OverFrameAutoArtComposer.ExtractIllustrationSource(pend);
        Assert.Equal(512, extracted.Width);
        Assert.Equal(OverFrameAutoArtComposer.PendulumIllustArtHeight, extracted.Height);
        Assert.Equal(683, extracted.Height);
        Assert.True(extracted[256, 100].B > 150);
        Assert.True(OverFrameAutoArtComposer.IsLikelyOriginalIllustrationSize(512, 1024));
        Assert.True(OverFrameAutoArtComposer.IsLikelyOriginalIllustrationSize(512, 683));
    }

    [Fact]
    public void ExtractIllustrationSource_KeepsExactThreeByFourPendulumArt()
    {
        using var pend = new Image<Rgba32>(512, 683, new Rgba32(40, 180, 220, 255));
        using var extracted = OverFrameAutoArtComposer.ExtractIllustrationSource(pend);
        Assert.Equal(512, extracted.Width);
        Assert.Equal(683, extracted.Height);
    }

    [Theory]
    [InlineData(CardFrameStyle.PendulumNormal)]
    [InlineData(CardFrameStyle.PendulumEffect)]
    [InlineData(CardFrameStyle.PendulumFusion)]
    [InlineData(CardFrameStyle.PendulumSynchro)]
    [InlineData(CardFrameStyle.PendulumXyz)]
    [InlineData(CardFrameStyle.PendulumRitual)]
    [InlineData(CardFrameStyle.PendulumToken)]
    public void Compose_PendulumStyles_KeepNativeWiderArtWindow(CardFrameStyle style)
    {
        using var source = new Image<Rgba32>(512, 683, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(512, 683, new L8(0));
        for (var y = 100; y < 400; y++)
        for (var x = 150; x < 360; x++)
            mask[x, y] = new L8(255);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, style);
        Assert.Equal(OverFrameConstants.Width, result.Width);
        Assert.Equal(OverFrameConstants.Height, result.Height);

        using var frame = CardFrameTemplates.Load(style);
        var hole = OverFrameAutoArtComposer.DetectArtWindow(frame);
        Assert.False(hole.IsEmpty);
        Assert.True(hole.Width > OverFrameAutoArtComposer.ArtWindow.Width,
            $"{style} hole should be wider than Effect, got {hole}");
        Assert.True(hole.Height < OverFrameAutoArtComposer.ArtWindow.Height,
            $"{style} hole should be shorter than Effect, got {hole}");

        var sample = result[hole.Left + hole.Width / 2, hole.Top + hole.Height / 2];
        Assert.True(sample.A > 0, $"expected art in {style} hole, got {sample}");
    }

    [Fact]
    public void Compose_Pendulum_AppliesSubstantialDownwardVerticalOffset()
    {
        Assert.Equal(200, OverFrameAutoArtComposer.PendulumVerticalOffset);
        Assert.True(OverFrameAutoArtComposer.PendulumVerticalOffset >= 120,
            "Pendulum subject must sit a lot lower so the fixed mint/scale bar crosses lower on the figure.");
        Assert.True(OverFrameAutoArtComposer.PendulumVerticalOffset <= 280,
            "Pendulum vertical nudge should not exceed roughly half of the art-hole height.");

        // Thin horizontal subject bar at a known source Y — placement must include the
        // Pendulum-only downward bias (foil + rembg stay locked via bgY). Full-width so
        // L/R overframe passes the fail-closed subject check.
        const int srcW = 512;
        const int srcH = 683;
        const int barY = 200;
        using var source = new Image<Rgba32>(srcW, srcH, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(srcW, srcH, new L8(0));
        for (var x = 0; x < srcW; x++)
            mask[x, barY] = new L8(255);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.PendulumEffect);
        var hole = OverFrameAutoArtComposer.PendulumArtWindow;
        var cover = Math.Max(hole.Width / (float)srcW, hole.Height / (float)srcH);
        var scale = cover * OverFrameAutoArtComposer.OverflowScale;
        var scaledH = Math.Max(1, (int)MathF.Round(srcH * scale));
        var artCenterY = hole.Top + hole.Height / 2f;
        var bgYCentered = (int)MathF.Round(artCenterY - scaledH / 2f);
        var expectedY = bgYCentered + OverFrameAutoArtComposer.PendulumVerticalOffset +
                        (int)MathF.Round(barY * scale);

        var found = false;
        for (var x = 0; x < result.Width && !found; x++)
        {
            var p = result[x, expectedY];
            if (p.A == OverFrameAutoArtComposer.FoilMaskAlpha && p.G > 100)
                found = true;
        }

        Assert.True(found,
            $"expected Pendulum subject foil at y={expectedY} (centered bgY={bgYCentered} + offset {OverFrameAutoArtComposer.PendulumVerticalOffset})");

        // Without the offset, that same row would be empty of this green bar.
        var unshiftedY = bgYCentered + (int)MathF.Round(barY * scale);
        Assert.NotEqual(expectedY, unshiftedY);
        var greenAtUnshifted = false;
        for (var x = 0; x < result.Width; x++)
        {
            var p = result[x, unshiftedY];
            if (p.A == OverFrameAutoArtComposer.FoilMaskAlpha && p.G > 100 && p.R < 80)
            {
                greenAtUnshifted = true;
                break;
            }
        }

        Assert.False(greenAtUnshifted,
            $"subject bar must not remain at centered y={unshiftedY}; Pendulum offset should have moved it down");
    }

    [Fact]
    public void Compose_Pendulum_BottomMonsterLoreGetsMirrorjadeUnderlay()
    {
        // Saturated source so blended lore RGB diverges from opaque frame cream.
        // Pendulum keeps constant Mirrorjade soft (no Effect lore-top gradient).
        using var source = new Image<Rgba32>(512, 683, new Rgba32(220, 40, 200, 255));
        using var mask = new Image<L8>(512, 683, new L8(0));
        for (var y = 80; y < 500; y++)
        for (var x = 120; x < 390; x++)
            mask[x, y] = new L8(255);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.PendulumEffect);
        using var frame = CardFrameTemplates.Load(CardFrameStyle.PendulumEffect);

        var mint = OverFrameAutoArtComposer.PendulumMintTextBox;
        var bottom = OverFrameAutoArtComposer.PendulumMonsterLoreCream;
        var midX = bottom.Left + bottom.Width / 2;
        var mintY = mint.Top + mint.Height / 2;
        var bottomY = bottom.Top + bottom.Height / 2;

        var mintPix = result[midX, mintY];
        var bottomPix = result[midX, bottomY];
        var frameMint = frame[midX, mintY];
        var frameBottom = frame[midX, bottomY];

        // Both panels keep high alpha (frame chrome), but RGB must pull toward source art
        // — opaque full-frame paint would match the template cream exactly.
        Assert.True(mintPix.A >= 200, $"mint lore alpha, got {mintPix}");
        Assert.True(bottomPix.A >= 200, $"bottom lore alpha, got {bottomPix}");
        Assert.True(
            Math.Abs(bottomPix.R - frameBottom.R) > 5 ||
            Math.Abs(bottomPix.G - frameBottom.G) > 5 ||
            Math.Abs(bottomPix.B - frameBottom.B) > 5,
            $"bottom monster lore must blend art underlay, not stay pure frame chrome ({frameBottom} vs {bottomPix})");
        Assert.True(
            Math.Abs(mintPix.R - frameMint.R) > 5 ||
            Math.Abs(mintPix.G - frameMint.G) > 5 ||
            Math.Abs(mintPix.B - frameMint.B) > 5,
            $"mint strip must keep Mirrorjade blend ({frameMint} vs {mintPix})");
    }

    [Fact]
    public void Compose_Pendulum_DoesNotApplyEffectLoreGradient()
    {
        // Effect lore ramps soft→solid down the cream; Pendulum must keep constant
        // Mirrorjade soft across covered dual-lore (mint + lower monster stay similarly soft).
        using var source = new Image<Rgba32>(512, 683, new Rgba32(40, 200, 255, 255));
        using var mask = new Image<L8>(512, 683, new L8(0));
        for (var y = 80; y < 500; y++)
        for (var x = 120; x < 390; x++)
            mask[x, y] = new L8(255);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.PendulumEffect);
        using var frame = CardFrameTemplates.Load(CardFrameStyle.PendulumEffect);

        var mint = OverFrameAutoArtComposer.PendulumMintTextBox;
        var bottom = OverFrameAutoArtComposer.PendulumMonsterLoreCream;
        var midX = bottom.Left + bottom.Width / 2;
        var mintY = mint.Top + 8;
        var lowerMonsterY = bottom.Bottom - 16;

        var mintPix = result[midX, mintY];
        var lowerPix = result[midX, lowerMonsterY];
        var frameMint = frame[midX, mintY];
        var frameLower = frame[midX, lowerMonsterY];

        Assert.True(mintPix.B > frameMint.B, $"mint must soft-tint cyan, got {mintPix}");
        Assert.True(lowerPix.B > frameLower.B, $"lower monster lore must stay soft (not Effect solid), got {lowerPix}");

        // Constant opacity: upper and lower covered lore should tint similarly — not a
        // large soft→solid vertical ramp toward cream (Effect LoreArtUnderlayBlendHeight).
        var mintTint = ColorDistance(mintPix, frameMint);
        var lowerTint = ColorDistance(lowerPix, frameLower);
        Assert.True(mintTint > 5 && lowerTint > 5,
            $"both samples must stay Mirrorjade-soft (mintTint={mintTint}, lowerTint={lowerTint})");
        Assert.True(
            Math.Abs(mintTint - lowerTint) <= Math.Max(3, mintTint / 4),
            $"Pendulum must not apply Effect lore gradient (mintTint={mintTint} vs lowerTint={lowerTint})");
    }

    [Fact]
    public void PendulumGreenChromeRects_MatchMeasuredOuterBorders()
    {
        // Y starts at lore cut-top (640), not cream top (645), so left matches right punch
        // in the art-hole→mint junction band.
        Assert.Equal(new Rectangle(26, 640, 17, 358), OverFrameAutoArtComposer.PendulumGreenLeft);
        Assert.Equal(new Rectangle(662, 640, 16, 358), OverFrameAutoArtComposer.PendulumGreenRight);
        Assert.Equal(new Rectangle(26, 970, 652, 28), OverFrameAutoArtComposer.PendulumGreenBottom);
        Assert.Equal(OverFrameAutoArtComposer.PendulumLoreCutTop, OverFrameAutoArtComposer.PendulumGreenLeft.Y);
        Assert.Equal(OverFrameAutoArtComposer.PendulumGreenLeft.Y, OverFrameAutoArtComposer.PendulumGreenRight.Y);
        Assert.Equal(OverFrameAutoArtComposer.PendulumGreenLeft.Height, OverFrameAutoArtComposer.PendulumGreenRight.Height);

        Assert.True(OverFrameAutoArtComposer.IsPendulumGreenChromePunch(30, 850));
        Assert.True(OverFrameAutoArtComposer.IsPendulumGreenChromePunch(670, 850));
        Assert.True(OverFrameAutoArtComposer.IsPendulumGreenChromePunch(350, 980));
        // Junction band above mint/cream (y cut-top .. cream-1) is green punch on both sides.
        Assert.True(OverFrameAutoArtComposer.IsPendulumGreenChromePunch(30, OverFrameAutoArtComposer.PendulumLoreCutTop));
        Assert.True(OverFrameAutoArtComposer.IsPendulumGreenChromePunch(670, OverFrameAutoArtComposer.PendulumLoreCutTop));
        Assert.True(OverFrameAutoArtComposer.IsPendulumGreenChromePunch(30, 642));
        Assert.True(OverFrameAutoArtComposer.IsPendulumGreenChromePunch(670, 642));
        // Cream interior must not be treated as green punch.
        Assert.False(OverFrameAutoArtComposer.IsPendulumGreenChromePunch(350, 850));
    }

    [Fact]
    public void Compose_Pendulum_PunchesGreenSideAndBottomChrome_WhereSubjectPresent()
    {
        // Wide rembg that reaches canvas edges after Cover×overflow + Pendulum offset,
        // but leave margins clear so the >92% opaque guard does not fire.
        using var source = new Image<Rgba32>(512, 683, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(512, 683, new L8(0));
        for (var y = 20; y < 663; y++)
        for (var x = 20; x < 492; x++)
        {
            source[x, y] = new Rgba32(20, 220, 40, 255);
            mask[x, y] = new L8(255);
        }

        using var result = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.PendulumEffect);

        var left = OverFrameAutoArtComposer.PendulumGreenLeft;
        var right = OverFrameAutoArtComposer.PendulumGreenRight;
        var bottom = OverFrameAutoArtComposer.PendulumGreenBottom;

        var leftPix = result[left.Left + left.Width / 2, left.Top + left.Height / 2];
        var rightPix = result[right.Left + right.Width / 2, right.Top + right.Height / 2];
        var bottomPix = result[bottom.Left + bottom.Width / 2, bottom.Top + bottom.Height / 2];

        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, leftPix.A);
        Assert.True(leftPix.G > 100, $"left green chrome must punch subject, got {leftPix}");
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, rightPix.A);
        Assert.True(rightPix.G > 100, $"right green chrome must punch subject, got {rightPix}");
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, bottomPix.A);
        Assert.True(bottomPix.G > 100, $"bottom green chrome must punch subject, got {bottomPix}");

        // Cream interior stays Mirrorjade-opaque (not rembg foil).
        var cream = OverFrameAutoArtComposer.PendulumMonsterLoreCream;
        var creamPix = result[cream.Left + cream.Width / 2, cream.Top + cream.Height / 2];
        Assert.True(creamPix.A >= 200, $"Pendulum lore cream must stay blended chrome, got {creamPix}");
    }

    [Fact]
    public void Compose_Pendulum_PunchesLeftGreen_AtArtMintJunction_LikeRight()
    {
        // Regression: left green sits inside cream x-span, so without punch from lore
        // cut-top the art-hole→mint band (y≈640–644) stayed chrome while the right
        // outer margin punched cleanly at the same Y.
        using var source = new Image<Rgba32>(512, 683, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(512, 683, new L8(0));
        for (var y = 20; y < 663; y++)
        for (var x = 20; x < 492; x++)
        {
            source[x, y] = new Rgba32(20, 220, 40, 255);
            mask[x, y] = new L8(255);
        }

        using var result = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.PendulumEffect);

        var left = OverFrameAutoArtComposer.PendulumGreenLeft;
        var right = OverFrameAutoArtComposer.PendulumGreenRight;
        var cut = OverFrameAutoArtComposer.PendulumLoreCutTop;
        var creamTop = OverFrameAutoArtComposer.PendulumLoreCream.Top;
        Assert.True(cut < creamTop, "junction band is between cut-top and cream top");

        var lx = left.Left + left.Width / 2;
        var rx = right.Left + right.Width / 2;
        // Sample mid-junction (above blue/red scale chrome) and assert left matches right.
        var junctionY = cut + (creamTop - cut) / 2;
        var leftJunction = result[lx, junctionY];
        var rightJunction = result[rx, junctionY];

        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, leftJunction.A);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, rightJunction.A);
        Assert.True(leftJunction.G > 100, $"left junction must punch subject, got {leftJunction}");
        Assert.True(rightJunction.G > 100, $"right junction must punch subject, got {rightJunction}");

        // Also punch at cut-top itself (first row of the former dead zone).
        var leftCut = result[lx, cut];
        var rightCut = result[rx, cut];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, leftCut.A);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, rightCut.A);
        Assert.True(leftCut.G > 100, $"left at cut-top must punch subject, got {leftCut}");
        Assert.True(rightCut.G > 100, $"right at cut-top must punch subject, got {rightCut}");
    }

    [Fact]
    public void Compose_Pendulum_KeepsGreenChrome_WithoutSubject()
    {
        // Upper-center strip — overframes above the art hole (fail-closed) but stays
        // clear of outer green side/bottom chrome.
        using var source = new Image<Rgba32>(512, 683, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(512, 683, new L8(0));
        for (var y = 0; y < 200; y++)
        for (var x = 230; x < 280; x++)
            mask[x, y] = new L8(255);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.PendulumEffect);
        using var frame = CardFrameTemplates.Load(CardFrameStyle.PendulumEffect);

        var left = OverFrameAutoArtComposer.PendulumGreenLeft;
        var right = OverFrameAutoArtComposer.PendulumGreenRight;
        var bottom = OverFrameAutoArtComposer.PendulumGreenBottom;
        var lx = left.Left + left.Width / 2;
        var ly = left.Top + 80;
        var rx = right.Left + right.Width / 2;
        var bx = bottom.Left + bottom.Width / 2;
        var by = bottom.Top + bottom.Height / 2;

        var leftPix = result[lx, ly];
        var rightPix = result[rx, ly];
        var bottomPix = result[bx, by];
        var frameLeft = frame[lx, ly];
        var frameRight = frame[rx, ly];
        var frameBottom = frame[bx, by];

        Assert.True(leftPix.A >= 200, $"left green must stay chrome without subject, got {leftPix}");
        Assert.True(rightPix.A >= 200, $"right green must stay chrome without subject, got {rightPix}");
        Assert.True(bottomPix.A >= 200, $"bottom green must stay chrome without subject, got {bottomPix}");
        Assert.True(Math.Abs(leftPix.G - frameLeft.G) < 40, $"expected frame green left, got {leftPix} vs {frameLeft}");
        Assert.True(Math.Abs(rightPix.G - frameRight.G) < 40, $"expected frame green right, got {rightPix} vs {frameRight}");
        Assert.True(Math.Abs(bottomPix.G - frameBottom.G) < 40, $"expected frame green bottom, got {bottomPix} vs {frameBottom}");
    }

    [Fact]
    public void Compose_Effect_DoesNotPunchPendulumGreenBottomAsSubjectFoil()
    {
        // Non-Pendulum: cream-span pixels below lore must not use Pendulum green punch.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 5; x < 95; x++)
        {
            source[x, y] = new Rgba32(20, 220, 40, 255);
            mask[x, y] = new L8(255);
        }

        using var result = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.Effect);
        var cream = OverFrameAutoArtComposer.EffectLoreCream;
        var lore = result[cream.Left + cream.Width / 2, cream.Top + 40];
        Assert.True(lore.A >= 200, $"Effect lore cream must stay opaque, got {lore}");

        // Bottom strip under cream within cream x — still no rembg foil on Effect.
        var belowCreamY = cream.Bottom + 10;
        Assert.True(belowCreamY < OverFrameConstants.Height);
        var below = result[cream.Left + cream.Width / 2, belowCreamY];
        Assert.True(below.A >= 200,
            $"Effect must keep frame chrome below lore (no Pendulum green punch), got {below}");
    }

    [Fact]
    public void RequireCleanIllustrationSource_ExtractsWithoutFramedHeuristic()
    {
        // Cream lore-like panel previously false-triggered LooksLikeFramedCardArt.
        using var withLore = new Image<Rgba32>(512, 512, new Rgba32(40, 80, 120, 255));
        var cream = new Rgba32(233, 207, 183, 255);
        for (var y = 320; y < 500; y++)
        for (var x = 40; x < 470; x++)
            withLore[x, y] = cream;

        using var cleaned = OverFrameAutoArtComposer.RequireCleanIllustrationSource(withLore);
        Assert.Equal(512, cleaned.Width);
        Assert.Equal(512, cleaned.Height);
        Assert.Equal(cream, cleaned[100, 400]);
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
        var hole = OverFrameAutoArtComposer.ArtWindow;
        ClearRect(frame, hole);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var midX = hole.Left + hole.Width / 2;
        var cut = OverFrameAutoArtComposer.EffectLoreCutTop;
        var typeLine = result[midX, (hole.Bottom + cut) / 2];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, typeLine.A);
        Assert.False(typeLine.R < 80 && typeLine.G < 60,
            $"type-line must not stay dark frame chrome, got {typeLine}");

        var lore = result[midX, OverFrameAutoArtComposer.EffectLoreCream.Top + 40];
        AssertLoreCreamChrome(lore, cream, "lore");
    }

    [Fact]
    public void Compose_TypeLineStrip_AlwaysShowsArt_EvenWithoutSubjectThere()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 30; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(20, 200, 50, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        var hole = OverFrameAutoArtComposer.ArtWindow;
        ClearRect(frame, hole);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var midX = hole.Left + hole.Width / 2;
        var cut = OverFrameAutoArtComposer.EffectLoreCutTop;
        var typeLinePix = result[midX, (hole.Bottom + cut) / 2];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, typeLinePix.A);
        Assert.True(typeLinePix.G > 100, $"expected foil art in type-line gap, got {typeLinePix}");

        var loreTop = result[midX, OverFrameAutoArtComposer.EffectLoreCream.Top + 4];
        AssertLoreCreamChrome(loreTop, cream, "lore top");
    }

    [Fact]
    public void Compose_LoreDarkMargins_KeepChrome_WithoutSubject()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        // Centered subject only — does not reach dark lore margins (x 0–25 / 678–703).
        for (var y = 0; y < 100; y++)
        for (var x = 40; x < 60; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        var chrome = new Rgba32(40, 30, 90, 255);
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
            frame[x, y] = chrome;
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);
        var outer = OverFrameAutoArtComposer.EffectLoreOuter;
        var (left, right) = OverFrameAutoArtComposer.ResolveLoreDarkMargins(
            outer, OverFrameConstants.Width);

        Assert.Equal(new Rectangle(0, 766, 26, 196), left);
        Assert.Equal(new Rectangle(678, 766, 26, 196), right);

        var loreY = outer.Top + 40;
        var leftPix = result[left.Left + left.Width / 2, loreY];
        var rightPix = result[right.Left + right.Width / 2, loreY];
        Assert.True(leftPix.A >= 200, $"left dark margin must stay chrome without subject, got {leftPix}");
        Assert.True(rightPix.A >= 200, $"right dark margin must stay chrome without subject, got {rightPix}");
        Assert.True(Math.Abs(leftPix.R - chrome.R) < 40, $"expected frame chrome in left margin, got {leftPix}");
        Assert.True(Math.Abs(rightPix.R - chrome.R) < 40, $"expected frame chrome in right margin, got {rightPix}");

        // Cream interior stays Mirrorjade chrome (soft over art underlay, not foil A≈4).
        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var lore = result[creamR.Left + creamR.Width / 2, loreY];
        AssertLoreCreamChrome(lore, cream, "lore cream");
    }

    [Fact]
    public void Compose_LoreDarkMargins_PunchOnlyWhereSubjectPresent()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        // Wide subject that reaches into the dark lore margins.
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 100; x++)
        {
            if (x is >= 0 and < 20 or >= 80 and < 100)
            {
                source[x, y] = new Rgba32(20, 200, 50, 255);
                mask[x, y] = new L8(255);
            }
        }

        using var frame = CreateSolidFrame();
        var chrome = new Rgba32(40, 30, 90, 255);
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
            frame[x, y] = chrome;
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);
        var outer = OverFrameAutoArtComposer.EffectLoreOuter;
        var (left, right) = OverFrameAutoArtComposer.ResolveLoreDarkMargins(
            outer, OverFrameConstants.Width);
        var loreY = outer.Top + 40;

        var leftPix = result[left.Left + left.Width / 2, loreY];
        var rightPix = result[right.Left + right.Width / 2, loreY];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, leftPix.A);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, rightPix.A);
        Assert.True(leftPix.G > 80, $"left dark margin must punch subject art, got {leftPix}");
        Assert.True(rightPix.G > 80, $"right dark margin must punch subject art, got {rightPix}");

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var lore = result[creamR.Left + creamR.Width / 2, loreY];
        AssertLoreCreamChrome(lore, cream, "lore cream");
    }

    [Fact]
    public void Compose_Effect_LoreCream_SolidWhereUncovered_SoftWhereArtUnderlay()
    {
        // Cover×overflow for square sources ends ~y 819; cream runs through 962.
        // Saturated cyan underlay must Mirrorjade-blend near lore top, while uncovered
        // lore well past the footprint feather stays exact solid cream (no dimming).
        using var source = new Image<Rgba32>(512, 512, new Rgba32(40, 200, 255, 255));
        using var mask = new Image<L8>(512, 512, new L8(0));
        for (var y = 20; y < 492; y++)
        for (var x = 40; x < 472; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);
        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;
        var covered = result[midX, creamR.Top + 20];
        var uncovered = result[midX, creamR.Bottom - 10];

        Assert.True(covered.A >= 200, $"covered lore alpha, got {covered}");
        Assert.True(
            Math.Abs(covered.R - cream.R) > 5 ||
            Math.Abs(covered.G - cream.G) > 5 ||
            Math.Abs(covered.B - cream.B) > 5,
            $"covered lore must soft-blend art underlay, not stay pure cream ({cream} vs {covered})");
        Assert.True(covered.B > cream.B, $"covered lore should pull toward cyan underlay, got {covered}");

        Assert.Equal(cream, uncovered);
    }

    [Fact]
    public void Compose_Effect_LoreCream_VerticalSoftToSolidFromLoreTop()
    {
        // Hold 0.80 until 40px before art bottom, ramp to 1.0, then opaque cream below.
        using var source = new Image<Rgba32>(512, 512, new Rgba32(40, 200, 255, 255));
        using var mask = new Image<L8>(512, 512, new L8(0));
        for (var y = 20; y < 492; y++)
        for (var x = 40; x < 472; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var art = OverFrameAutoArtComposer.ArtWindow;
        const int size = 512;
        var scale = Math.Max(art.Width / (float)size, art.Height / (float)size)
            * OverFrameAutoArtComposer.OverflowScale;
        var scaledH = Math.Max(1, (int)MathF.Round(size * scale));
        var bgY = (int)MathF.Round(art.Top + art.Height / 2f - scaledH / 2f);
        var artBottom = bgY + scaledH;
        var blendH = OverFrameAutoArtComposer.LoreArtUnderlayBlendHeight;
        var radius = OverFrameAutoArtComposer.LoreArtUnderlayBlendRadius;
        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;
        var ramp = OverFrameAutoArtComposer.LoreCreamRampHeight;

        Assert.Equal(creamR.Height, blendH);
        Assert.Equal(blendH, radius);
        Assert.True(artBottom > creamR.Top + ramp && artBottom < creamR.Bottom,
            $"artBottom={artBottom} should leave a ramp band inside lore {creamR}");

        var plateau = result[midX, creamR.Top + 4];
        var latePlateau = result[midX, artBottom - ramp - 2];
        var inRamp = result[midX, artBottom - 4];
        var belowArt = result[midX, Math.Min(creamR.Bottom - 1, artBottom + 8)];
        var lowerLore = result[midX, creamR.Bottom - 10];

        Assert.True(plateau.B > cream.B, $"lore-top plateau should peek underlay, got {plateau}");
        Assert.True(
            Math.Abs(ColorDistance(plateau, cream) - ColorDistance(latePlateau, cream)) < 12,
            $"0.80 plateau should hold until ramp ({plateau} vs {latePlateau})");
        Assert.True(
            ColorDistance(inRamp, cream) < ColorDistance(latePlateau, cream),
            $"ramp should be closer to cream than plateau ({latePlateau} vs {inRamp})");
        Assert.Equal(cream, belowArt);
        Assert.Equal(cream, lowerLore);
    }

    [Fact]
    public void ComputeEffectLoreCreamCover_Hold80ThenRamp40pxAtArtBottom()
    {
        var cream = OverFrameAutoArtComposer.EffectLoreCream;
        var artBottom = cream.Top + 54;
        var ramp = OverFrameAutoArtComposer.LoreCreamRampHeight;

        float Cover(int y) =>
            OverFrameAutoArtComposer.ComputeEffectLoreCreamCover(y, cream, artBottom);

        Assert.Equal(OverFrameAutoArtComposer.TextBoxFrameOpacity, Cover(cream.Top), 3);
        Assert.Equal(OverFrameAutoArtComposer.TextBoxFrameOpacity, Cover(artBottom - ramp - 1), 3);

        var midRamp = Cover(artBottom - ramp / 2);
        Assert.True(
            midRamp > OverFrameAutoArtComposer.TextBoxFrameOpacity + 0.05f && midRamp < 1f,
            $"mid-ramp should be between 0.80 and 1, got {midRamp}");

        Assert.Equal(1f, Cover(artBottom), 3);
        Assert.Equal(1f, Cover(cream.Bottom - 1), 3);
        Assert.Equal(40, ramp);
    }

    [Fact]
    public void Compose_Effect_LoreCream_PastArtBottomCloserToCreamThanUpper()
    {
        using var source = new Image<Rgba32>(512, 512, new Rgba32(40, 200, 255, 255));
        using var mask = new Image<L8>(512, 512, new L8(0));
        for (var y = 20; y < 492; y++)
        for (var x = 40; x < 472; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var art = OverFrameAutoArtComposer.ArtWindow;
        const int size = 512;
        var scale = Math.Max(art.Width / (float)size, art.Height / (float)size)
            * OverFrameAutoArtComposer.OverflowScale;
        var scaledH = Math.Max(1, (int)MathF.Round(size * scale));
        var bgY = (int)MathF.Round(art.Top + art.Height / 2f - scaledH / 2f);
        var artBottom = bgY + scaledH;
        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;

        var upper = result[midX, creamR.Top + 8];
        var lower = result[midX, Math.Min(creamR.Bottom - 1, artBottom + 12)];
        var upperTint = ColorDistance(upper, cream);

        Assert.True(upperTint > 5, $"upper lore should peek underlay through 0.80 cream, got {upper} tint={upperTint}");
        Assert.Equal(cream, lower);
    }

    [Fact]
    public void Compose_Effect_LoreCream_NoHardOpacityCliffAcrossFootprint()
    {
        using var source = new Image<Rgba32>(512, 512, new Rgba32(40, 200, 255, 255));
        using var mask = new Image<L8>(512, 512, new L8(0));
        for (var y = 20; y < 492; y++)
        for (var x = 40; x < 472; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var art = OverFrameAutoArtComposer.ArtWindow;
        const int size = 512;
        var scale = Math.Max(art.Width / (float)size, art.Height / (float)size)
            * OverFrameAutoArtComposer.OverflowScale;
        var scaledH = Math.Max(1, (int)MathF.Round(size * scale));
        var bgY = (int)MathF.Round(art.Top + art.Height / 2f - scaledH / 2f);
        var artBottom = bgY + scaledH;
        var edgeY = artBottom - 1;
        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;
        var rampStart = artBottom - OverFrameAutoArtComposer.LoreCreamRampHeight;

        var maxStep = 0;
        void Accumulate(int fromInclusive, int toInclusive)
        {
            for (var y = fromInclusive + 1; y <= toInclusive; y++)
            {
                var step = ColorDistance(result[midX, y - 1], result[midX, y]);
                if (step > maxStep)
                    maxStep = step;
            }
        }

        Accumulate(creamR.Top, Math.Min(edgeY, rampStart));
        Accumulate(Math.Max(creamR.Top, rampStart), edgeY);
        Accumulate(artBottom, creamR.Bottom - 1);

        Assert.True(maxStep <= 5,
            $"cream cover steps must stay continuous (maxStep={maxStep})");
        Assert.Equal(cream, result[midX, creamR.Bottom - 1]);
    }

    [Fact]
    public void Compose_LoreUnderlay_DoesNotVerticallySmearSubjectPastFootprint()
    {
        // Square sources cover-scale short of lore bottom (~y 819 vs cream through 962).
        // Past-footprint clamp-to-edge used to repeat the last source row down the cream
        // as vertical ghost streaks under subject "feet" (pink slime eyes, Mirrorjade claws).
        // Past the footprint must stay solid cream immediately — no foot-column tint.
        const int size = 512;
        using var source = new Image<Rgba32>(size, size, new Rgba32(20, 30, 50, 255));
        using var mask = new Image<L8>(size, size, new L8(0));
        // Tall centered subject so rembg overframes; paint hot magenta only on the
        // bottom source rows (the edge that would be clamp-repeated into lore).
        var footColor = new Rgba32(255, 16, 200, 255);
        for (var y = 40; y < size - 8; y++)
        for (var x = 80; x < size - 80; x++)
            mask[x, y] = new L8(255);
        for (var y = size - 8; y < size; y++)
        for (var x = 180; x < 220; x++)
        {
            source[x, y] = footColor;
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var art = OverFrameAutoArtComposer.ArtWindow;
        var scale = Math.Max(
            art.Width / (float)size,
            art.Height / (float)size) * OverFrameAutoArtComposer.OverflowScale;
        var scaledW = Math.Max(1, (int)MathF.Round(size * scale));
        var scaledH = Math.Max(1, (int)MathF.Round(size * scale));
        var bgX = (int)MathF.Round(art.Left + art.Width / 2f - scaledW / 2f);
        var bgY = (int)MathF.Round(art.Top + art.Height / 2f - scaledH / 2f);
        var edgeY = bgY + scaledH - 1;
        Assert.True(edgeY >= creamR.Top && edgeY < creamR.Bottom,
            $"footprint edge y={edgeY} should fall inside lore cream");

        // Source feet columns mapped onto the canvas.
        var footCanvasX = bgX + (int)MathF.Round(200 * scale);
        Assert.True(footCanvasX >= creamR.Left && footCanvasX < creamR.Right,
            $"foot column x={footCanvasX} should land in lore cream");

        // Immediately past the footprint — the old clamp feather smeared here hardest.
        var justPast = result[footCanvasX, Math.Min(creamR.Bottom - 1, edgeY + 1)];
        var midPast = result[footCanvasX, Math.Min(creamR.Bottom - 1, edgeY + 24)];
        var lowerLore = result[footCanvasX, creamR.Bottom - 20];
        Assert.Equal(cream, justPast);
        Assert.Equal(cream, midPast);
        Assert.Equal(cream, lowerLore);
        Assert.Equal(cream, result[creamR.Left + creamR.Width / 2, Math.Min(creamR.Bottom - 1, edgeY + 1)]);
    }

    [Fact]
    public void Compose_LoreSideWings_KeepChrome_WithoutSubject()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 100; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(20, 200, 50, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var gold = new Rgba32(180, 100, 50, 255);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream, gold);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var outer = OverFrameAutoArtComposer.EffectLoreOuter;
        var loreY = creamR.Top + 40;
        var leftWing = result[(outer.Left + creamR.Left) / 2, loreY];
        Assert.True(leftWing.A >= 200, $"left wing must stay chrome without subject, got {leftWing}");
        Assert.True(Math.Abs(leftWing.R - gold.R) < 40, $"expected gold left wing, got {leftWing}");

        var rightWing = result[(creamR.Right + outer.Right) / 2, loreY];
        Assert.True(rightWing.A >= 200, $"right wing must stay chrome without subject, got {rightWing}");
        Assert.True(Math.Abs(rightWing.R - gold.R) < 40, $"expected gold right wing, got {rightWing}");

        var lore = result[creamR.Left + creamR.Width / 2, loreY];
        AssertLoreCreamChrome(lore, cream, "lore interior");
    }

    [Fact]
    public void Compose_LoreSideWings_PunchOnlyWhereSubjectPresent()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 100; y++)
        for (var x = 5; x < 95; x++)
        {
            source[x, y] = new Rgba32(20, 200, 50, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var gold = new Rgba32(180, 100, 50, 255);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream, gold);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var outer = OverFrameAutoArtComposer.EffectLoreOuter;
        var loreY = creamR.Top + 40;
        var leftWing = result[(outer.Left + creamR.Left) / 2, loreY];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, leftWing.A);
        Assert.True(leftWing.G > 80, $"expected subject punch in left lore wing, got {leftWing}");

        var rightWing = result[(creamR.Right + outer.Right) / 2, loreY];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, rightWing.A);
        Assert.True(rightWing.G > 80, $"expected subject punch in right lore wing, got {rightWing}");

        var lore = result[creamR.Left + creamR.Width / 2, loreY];
        AssertLoreCreamChrome(lore, cream, "lore interior");
    }

    [Fact]
    public void Compose_CutsSubjectAtLoreTop_AllowsDarkMarginPunch()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 80, 200, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        // Reach the canvas edge so rembg lands in dark lore margins (0–25 / 678–703).
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 100; x++)
        {
            if (x is >= 0 and < 15 or >= 85 and < 100)
            {
                source[x, y] = new Rgba32(240, 20, 20, 255);
                mask[x, y] = new L8(255);
            }
        }

        using var frame = CreateSolidFrame();
        var chrome = new Rgba32(40, 30, 90, 255);
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
            frame[x, y] = chrome;

        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var (left, _) = OverFrameAutoArtComposer.ResolveLoreDarkMargins(
            OverFrameAutoArtComposer.EffectLoreOuter, OverFrameConstants.Width);
        var side = result[left.Left + left.Width / 2, creamR.Top + 30];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, side.A);
        Assert.True(side.R > 150, $"expected rembg subject punch in dark lore margin, got {side}");

        var lore = result[creamR.Left + creamR.Width / 2, creamR.Top + 30];
        AssertLoreCreamChrome(lore, cream, "lore interior");
    }

    [Fact]
    public void Compose_CutsRembgOutOfLoreBox_NoHardVerticalBleed()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 80, 200, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 100; y++)
        for (var x = 48; x < 52; x++)
        {
            source[x, y] = new Rgba32(241, 12, 164, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;
        var lore = result[midX, creamR.Top + 40];
        // Soft underlay may tint cream, but rembg magenta must not punch the cream interior.
        AssertLoreCreamChrome(lore, cream, "lore");
        Assert.True(lore.R < 250 || lore.B < 250, $"magenta cutout bled into lore: {lore}");

        var hole = OverFrameAutoArtComposer.ArtWindow;
        var inArt = result[midX, hole.Top + hole.Height / 2];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, inArt.A);
    }

    [Fact]
    public void InferStyle_UsesTypeLineNotEffectBody()
    {
        Assert.Equal(CardFrameStyle.Link, CardFrameTemplates.InferStyle("Accesscode", "[Cyberse/Link/Effect] Link-4"));
        Assert.Equal(CardFrameStyle.Synchro, CardFrameTemplates.InferStyle("Stardust", "[Dragon/Synchro/Effect]"));
        Assert.Equal(CardFrameStyle.Effect, CardFrameTemplates.InferStyle("Some Effect", "[Fiend/Effect]"));
        Assert.Equal(CardFrameStyle.Normal, CardFrameTemplates.InferStyle("Fish", "[Fish/Normal] lore"));
        Assert.Equal(CardFrameStyle.Fusion, CardFrameTemplates.InferStyle("Mirrorjade", "[Wyrm/Fusion/Effect] Fusion Monster text"));
        Assert.Equal(CardFrameStyle.Xyz, CardFrameTemplates.InferStyle("Utopia", "[Warrior/Xyz/Effect]"));
        Assert.Equal(CardFrameStyle.Ritual, CardFrameTemplates.InferStyle("Relinquished", "[Spellcaster/Ritual/Effect]"));
        Assert.Equal(CardFrameStyle.Trap, CardFrameTemplates.InferStyle("Impulse", "[Trap] card text"));
        Assert.Equal(CardFrameStyle.Spell, CardFrameTemplates.InferStyle("Raigeki", "Spell Card"));
        Assert.Equal(CardFrameStyle.Token, CardFrameTemplates.InferStyle("Token", "[Warrior/Token]"));
        Assert.Equal(CardFrameStyle.Token, CardFrameTemplates.InferStyle("Sheep Token", "[Beast/Token]"));
        Assert.Equal(
            CardFrameStyle.Token,
            CardFrameTemplates.InferStyle("Ojama Token", "[Beast/Token/Normal] This card cannot be Tributed."));
        Assert.Equal(
            CardFrameStyle.PendulumEffect,
            CardFrameTemplates.InferStyle("Odd-Eyes", "[Dragon/Pendulum/Effect]"));
        Assert.Equal(
            CardFrameStyle.PendulumNormal,
            CardFrameTemplates.InferStyle("Performapal", "[Spellcaster/Pendulum/Normal]"));
        Assert.Equal(
            CardFrameStyle.PendulumSynchro,
            CardFrameTemplates.InferStyle("Supreme King", "[Dragon/Synchro/Pendulum/Effect]"));
        Assert.Equal(
            CardFrameStyle.PendulumXyz,
            CardFrameTemplates.InferStyle("Odd-Eyes Absolute", "[Dragon/Xyz/Pendulum/Effect]"));
        Assert.Equal(
            CardFrameStyle.PendulumFusion,
            CardFrameTemplates.InferStyle("Odd-Eyes Vortex", "[Dragon/Fusion/Pendulum/Effect]"));
        Assert.Equal(
            CardFrameStyle.PendulumRitual,
            CardFrameTemplates.InferStyle("Odd-Eyes Pendulumgraph", "[Dragon/Ritual/Pendulum/Effect]"));
        Assert.Equal(
            CardFrameStyle.PendulumRitual,
            CardFrameTemplates.InferStyle("Nekroz of Metaltron", "[Wyrm/Pendulum/Ritual/Effect]"));
        Assert.Equal(
            CardFrameStyle.PendulumEffect,
            CardFrameTemplates.InferStyle("Amorphage", "[Pendulum Effect]"));
        Assert.Equal(
            CardFrameStyle.PendulumToken,
            CardFrameTemplates.InferStyle("Token Pend", "[Fiend/Pendulum/Token]"));

        Assert.Equal(
            CardFrameStyle.Effect,
            CardFrameTemplates.InferStyle(
                "Some Effect",
                "[Fiend/Effect] You can Special Summon 1 Link Monster from your Extra Deck."));

        Assert.Null(CardFrameTemplates.InferStyle("No Type Line", "A monster with no bracket type line."));
        Assert.Null(
            CardFrameTemplates.InferStyle(
                "False Spell",
                "Destroy 1 monster on the field. This card is treated as a Spell Card while face-up."));
    }

    [Fact]
    public void ClampSubjectScale_ClampsToSharedCustomArtRange()
    {
        Assert.Equal(0.5f, OverFrameAutoArtComposer.ClampSubjectScale(0.1f));
        Assert.Equal(4f, OverFrameAutoArtComposer.ClampSubjectScale(9f));
        Assert.Equal(1f, OverFrameAutoArtComposer.ClampSubjectScale(1f));
        Assert.Equal(1.5f, OverFrameAutoArtComposer.ClampSubjectScale(1.5f));
        Assert.Equal(3f, OverFrameAutoArtComposer.ClampSubjectScale(3f));
    }

    [Fact]
    public void ClampBackgroundScale_ClampsToSharedCustomArtRange()
    {
        Assert.Equal(0.5f, OverFrameAutoArtComposer.ClampBackgroundScale(0.1f));
        Assert.Equal(4f, OverFrameAutoArtComposer.ClampBackgroundScale(9f));
        Assert.Equal(1f, OverFrameAutoArtComposer.ClampBackgroundScale(1f));
        Assert.Equal(1.5f, OverFrameAutoArtComposer.ClampBackgroundScale(1.5f));
        Assert.Equal(4f, OverFrameAutoArtComposer.ClampBackgroundScale(4f));
        Assert.Equal(
            OverFrameAutoArtComposer.CustomArtScaleMin,
            OverFrameAutoArtComposer.BackgroundScaleMin);
        Assert.Equal(
            OverFrameAutoArtComposer.CustomArtScaleMax,
            OverFrameAutoArtComposer.SubjectScaleMax);
    }

    [Fact]
    public void GetBackgroundPanLimits_GrowsWithBackgroundScale()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        // Square source matching hole aspect → Cover has little overflow at ×1.
        var (max1X, max1Y) = OverFrameAutoArtComposer.GetBackgroundPanLimits(
            art.Width, art.Height, art, backgroundScale: 1f);
        var (max2X, max2Y) = OverFrameAutoArtComposer.GetBackgroundPanLimits(
            art.Width, art.Height, art, backgroundScale: 2f);

        Assert.True(max1X <= 1 && max1Y <= 1,
            $"×1 Cover on matching aspect should have near-zero pan, got {max1X},{max1Y}");
        Assert.True(max2X > max1X || max2Y > max1Y,
            $"×2 Cover must allow more pan than ×1 ({max1X},{max1Y} vs {max2X},{max2Y})");
        Assert.True(max2X > 50 && max2Y > 50,
            $"×2 Cover should have substantial pan room, got {max2X},{max2Y}");

        var (max4X, max4Y) = OverFrameAutoArtComposer.GetBackgroundPanLimits(
            art.Width, art.Height, art, backgroundScale: 4f);
        Assert.True(max4X > max2X || max4Y > max2Y,
            $"×4 Cover must allow more pan than ×2 ({max2X},{max2Y} vs {max4X},{max4Y})");
    }

    [Theory]
    [InlineData(CardFrameStyle.Effect, 12, -34, 12, -34)]
    [InlineData(CardFrameStyle.Normal, 0, 0, 0, 0)]
    [InlineData(CardFrameStyle.PendulumEffect, 12, -34, 12, -34 + 200)]
    [InlineData(CardFrameStyle.PendulumNormal, 0, 0, 0, 200)]
    [InlineData(CardFrameStyle.OfGradientPendulumEffect, 5, 10, 5, 10 + 200)]
    [InlineData(CardFrameStyle.OfGradientEffect, 5, 10, 5, 10)]
    public void ResolveMatchedBackgroundPan_AddsPendulumVerticalBiasOnly(
        CardFrameStyle style,
        int subjectX,
        int subjectY,
        int expectedPanX,
        int expectedPanY)
    {
        Assert.Equal(
            OverFrameAutoArtComposer.PendulumVerticalOffset,
            200);

        var (panX, panY) = OverFrameAutoArtComposer.ResolveMatchedBackgroundPan(
            subjectX, subjectY, style);
        Assert.Equal(expectedPanX, panX);
        Assert.Equal(expectedPanY, panY);

        var bias = OverFrameAutoArtComposer.GetSubjectPlacementBiasY(style);
        Assert.Equal(
            CardFrameTemplates.IsPendulumStyle(style)
                ? OverFrameAutoArtComposer.PendulumVerticalOffset
                : 0,
            bias);
    }

    [Fact]
    public void MatchBackgroundToSubject_Pendulum_AppliesBiasWithinPanLimits()
    {
        // Typical MD Pendulum illust into PendulumArtWindow — ×1.5 leaves room for +200 Y.
        const int bgW = 512;
        const int bgH = 683;
        var hole = OverFrameAutoArtComposer.PendulumArtWindow;
        var (maxX, maxY) = OverFrameAutoArtComposer.GetBackgroundPanLimits(
            bgW, bgH, hole, backgroundScale: 1.5f);
        Assert.True(maxY >= OverFrameAutoArtComposer.PendulumVerticalOffset,
            $"test needs pan room ≥ {OverFrameAutoArtComposer.PendulumVerticalOffset}, got {maxY}");

        var (scale, panX, panY) = OverFrameAutoArtComposer.MatchBackgroundToSubject(
            subjectScale: 1.5f,
            subjectOffsetX: 18,
            subjectOffsetY: -40,
            CardFrameStyle.PendulumEffect,
            bgW,
            bgH);

        Assert.Equal(1.5f, scale);
        Assert.Equal(OverFrameAutoArtComposer.ClampBackgroundPan(18, maxX), panX);
        Assert.Equal(
            OverFrameAutoArtComposer.ClampBackgroundPan(
                -40 + OverFrameAutoArtComposer.PendulumVerticalOffset, maxY),
            panY);
    }

    [Fact]
    public void MatchBackgroundToSubject_Effect_CopiesOffsetWithoutBias()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        var (scale, panX, panY) = OverFrameAutoArtComposer.MatchBackgroundToSubject(
            subjectScale: 2f,
            subjectOffsetX: -30,
            subjectOffsetY: 45,
            CardFrameStyle.Effect,
            backgroundWidth: art.Width,
            backgroundHeight: art.Height);

        Assert.Equal(2f, scale);
        var (maxX, maxY) = OverFrameAutoArtComposer.GetBackgroundPanLimits(
            art.Width, art.Height, art, backgroundScale: 2f);
        Assert.Equal(OverFrameAutoArtComposer.ClampBackgroundPan(-30, maxX), panX);
        Assert.Equal(OverFrameAutoArtComposer.ClampBackgroundPan(45, maxY), panY);
    }

    [Fact]
    public void Compose_CustomArtOnly_DoesNotFillArtHoleWithRectangularFoil()
    {
        // Narrow center strip subject — art-hole corners must stay empty (no full-source foil).
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 200, 40, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 45; x < 55; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        using var custom = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly);
        using var auto = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var corner = custom[art.Left + 8, art.Top + 8];
        Assert.Equal(0, corner.A);

        var autoCorner = auto[art.Left + 8, art.Top + 8];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, autoCorner.A);

        var mid = custom[art.Left + art.Width / 2, art.Top + art.Height / 2];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, mid.A);
        Assert.True(mid.R > 100, $"expected subject in art hole, got {mid}");
    }

    [Fact]
    public void Compose_CustomArtOnly_SubjectScale_EnlargesCardArt()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        // CustomArtOnly ×1 = Cover (no Auto OverflowScale); use ×1.5 vs ×3 so both overframe.
        using var at15 = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1.5f,
            OverFrameComposeMode.CustomArtOnly);
        using var at3 = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 3f,
            OverFrameComposeMode.CustomArtOnly);

        var y15 = FindSubjectAboveArtWindow(at15);
        var y3 = FindSubjectAboveArtWindow(at3);
        Assert.True(y15 >= 0, "scale 1.5 should overframe above art window");
        Assert.True(y3 >= 0, "scale 3 should overframe above art window");
        Assert.True(y3 < y15, $"×3 subject should reach higher (y={y3}) than ×1.5 (y={y15})");
    }

    [Fact]
    public void Compose_CustomArtOnly_SubjectScale_UsesCoverWithoutOverflowScale()
    {
        // Narrow tall strip: Auto (Cover×OverflowScale) overframes at ×1; Custom Cover×1 does not.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        using var custom = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly);
        using var auto = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.AutoFoilAndSubject);

        Assert.True(
            FindSubjectAboveArtWindow(custom) < 0,
            "CustomArtOnly ×1 is Cover fit and should not overframe this strip");
        Assert.True(
            FindSubjectAboveArtWindow(auto) >= 0,
            "Auto ×1 still applies OverflowScale and should overframe");

        // Same Cover multiplier as background: Custom ×OverflowScale matches Auto ×1 reach.
        using var customMatched = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: OverFrameAutoArtComposer.OverflowScale,
            OverFrameComposeMode.CustomArtOnly);
        Assert.Equal(
            FindSubjectAboveArtWindow(auto),
            FindSubjectAboveArtWindow(customMatched));
    }

    [Fact]
    public void Compose_CustomArtOnly_LoreSoftWhereSubjectCovers_SolidElsewhere()
    {
        using var source = new Image<Rgba32>(512, 512, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(512, 512, new L8(0));
        for (var y = 20; y < 492; y++)
        for (var x = 200; x < 312; x++)
        {
            source[x, y] = new Rgba32(40, 200, 255, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var result = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame,
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            // Cover×OverflowScale matches prior Custom ×1 (which baked OverflowScale in).
            subjectScale: OverFrameAutoArtComposer.OverflowScale,
            OverFrameComposeMode.CustomArtOnly);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;
        var leftX = creamR.Left + 30;
        var coveredY = creamR.Top + 20;
        var uncoveredY = creamR.Bottom - 10;

        Assert.Equal(cream, result[leftX, coveredY]);
        Assert.Equal(cream, result[midX, uncoveredY]);
        Assert.True(
            result[midX, coveredY].B > cream.B,
            $"covered lore must stay soft Mirrorjade underlay, got {result[midX, coveredY]}");
    }

    [Fact]
    public void Compose_CustomArtOnly_SubjectOffset_MovesLoreSoftUnderlay()
    {
        using var source = new Image<Rgba32>(512, 512, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(512, 512, new L8(0));
        for (var y = 20; y < 492; y++)
        for (var x = 200; x < 312; x++)
        {
            source[x, y] = new Rgba32(40, 200, 255, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var baseline = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: OverFrameAutoArtComposer.OverflowScale,
            OverFrameComposeMode.CustomArtOnly);
        using var shifted = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 120,
            subjectOffsetY: 0,
            subjectScale: OverFrameAutoArtComposer.OverflowScale,
            OverFrameComposeMode.CustomArtOnly);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;
        var coveredY = creamR.Top + 20;
        Assert.True(baseline[midX, coveredY].B > cream.B);
        // CustomArtOnly lore soft footprint follows Card Art offset (unlike Auto foil).
        Assert.NotEqual(baseline[midX, coveredY], shifted[midX, coveredY]);
    }

    [Fact]
    public void Compose_CustomArtOnly_KeepsArtLoreGapChrome_WithoutSubjectThere()
    {
        // Subject only in the upper art hole — must not auto-punch / soft-fill the
        // type-line strip or side margins between art bottom and lore top.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 25; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        var hole = OverFrameAutoArtComposer.ArtWindow;
        ClearRect(frame, hole);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        var gapChrome = new Rgba32(60, 40, 30, 255);
        var cut = OverFrameAutoArtComposer.EffectLoreCutTop;
        for (var y = hole.Bottom; y < cut; y++)
        {
            for (var x = hole.Left; x < hole.Right; x++)
                frame[x, y] = gapChrome;
            for (var x = hole.Left - 30; x < hole.Left; x++)
                if (x >= 0) frame[x, y] = gapChrome;
            for (var x = hole.Right; x < hole.Right + 30; x++)
                if (x < frame.Width) frame[x, y] = gapChrome;
        }

        using var result = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame,
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly);

        var gapY = (hole.Bottom + cut) / 2;
        var mid = result[hole.Left + hole.Width / 2, gapY];
        Assert.True(mid.A >= 200, $"Custom OF must keep type-line chrome, got {mid}");
        Assert.True(
            Math.Abs(mid.R - gapChrome.R) < 40 && Math.Abs(mid.G - gapChrome.G) < 40,
            $"type-line strip must stay frame chrome (no auto punch), got {mid}");

        var leftWing = result[hole.Left - 20, gapY];
        Assert.True(leftWing.A >= 200, $"side margin must stay chrome, got {leftWing}");
        Assert.True(
            Math.Abs(leftWing.R - gapChrome.R) < 40,
            $"side margin must not soft-fill without subject, got {leftWing}");
    }

    [Fact]
    public void Compose_CustomArtOnly_PunchesArtLoreGap_WhereSubjectPresent()
    {
        // Tall subject that reaches the art–lore type-line strip — intentional overframe.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(40, 200, 255, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        var hole = OverFrameAutoArtComposer.ArtWindow;
        ClearRect(frame, hole);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        var gapChrome = new Rgba32(60, 40, 30, 255);
        var cut = OverFrameAutoArtComposer.EffectLoreCutTop;
        for (var y = hole.Bottom; y < cut; y++)
        for (var x = hole.Left; x < hole.Right; x++)
            frame[x, y] = gapChrome;

        using var result = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame,
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 2f,
            OverFrameComposeMode.CustomArtOnly);

        var midX = hole.Left + hole.Width / 2;
        var gapY = (hole.Bottom + cut) / 2;
        var punched = result[midX, gapY];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, punched.A);
        Assert.True(punched.B > 100,
            $"subject must punch type-line strip where present, got {punched}");

        // Empty wing beside the strip stays chrome.
        var wing = result[hole.Left - 20, gapY];
        Assert.True(wing.A >= 200, $"empty side wing must stay chrome, got {wing}");
    }

    [Fact]
    public void Compose_CustomArtOnly_PunchesBottomChromeBelowLore_WhereSubjectPresent()
    {
        // Tall ×2 subject so Card Art reaches the dark strip under Effect lore cream.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var cream = new Rgba32(233, 207, 183, 255);
        PaintEffectStyleLore(frame, cream);

        using var custom = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            // Former Custom ×2 included OverflowScale; Cover×(2×Overflow) reaches bottom chrome.
            subjectScale: 2f * OverFrameAutoArtComposer.OverflowScale,
            OverFrameComposeMode.CustomArtOnly);
        using var auto = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 2f,
            composeMode: OverFrameComposeMode.AutoFoilAndSubject);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;
        var belowY = creamR.Bottom + 10;
        Assert.True(belowY < OverFrameConstants.Height);

        var customBelow = custom[midX, belowY];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, customBelow.A);
        Assert.True(customBelow.R > 100,
            $"Custom OF must punch subject into bottom chrome, got {customBelow}");

        var autoBelow = auto[midX, belowY];
        Assert.True(autoBelow.A >= 200,
            $"Auto-create must keep Effect bottom chrome opaque, got {autoBelow}");

        // Lore rect still paints cream on top of Card Art (soft underlay, not hard punch).
        var loreY = creamR.Top + 40;
        Assert.True(custom[midX, loreY].A >= 200,
            $"lore must stay opaque over art, got {custom[midX, loreY]}");
    }

    [Fact]
    public void Compose_CustomArtOnly_Background_CoverFillsArtHoleOnly()
    {
        // Narrow subject strip; wide background Cover-fills the hole corners.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 45; x < 55; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        // Wide 4:1 image — Cover must crop sides and fill the full art hole height/width.
        using var background = new Image<Rgba32>(400, 100, new Rgba32(10, 180, 240, 255));

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);

        using var result = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame,
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly,
            background);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var corner = result[art.Left + 8, art.Top + 8];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, corner.A);
        Assert.True(corner.B > 200, $"art-hole corner must show Cover background, got {corner}");
        Assert.True(corner.R < 40, $"background should be cyan, not subject red, got {corner}");

        var mid = result[art.Left + art.Width / 2, art.Top + art.Height / 2];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, mid.A);
        Assert.True(mid.R > 100, $"subject must still paint over background, got {mid}");

        // Immediately outside the art hole (away from the center subject strip): frame
        // chrome only — Cover background must not spill past the hole clip.
        var aboveNearLeft = result[art.Left + 8, art.Top - 4];
        Assert.True(aboveNearLeft.A >= 200,
            $"chrome above hole must stay opaque, got {aboveNearLeft}");
        Assert.True(aboveNearLeft.B < 100,
            $"background must not punch outside art window, got {aboveNearLeft}");

        var leftOfHole = result[art.Left - 4, art.Top + art.Height / 2];
        Assert.True(leftOfHole.A >= 200, $"chrome left of hole must stay opaque, got {leftOfHole}");
        Assert.True(leftOfHole.B < 100,
            $"background must not overflow left of art window, got {leftOfHole}");
    }

    [Fact]
    public void Compose_CustomArtOnly_Background_CoverFillsPendulumArtHoleOnly()
    {
        // Square subject; tall portrait background Cover-fills the wide/short Pendulum hole.
        using var source = new Image<Rgba32>(80, 80, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(80, 80, new L8(0));
        for (var y = 10; y < 70; y++)
        for (var x = 35; x < 45; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        // Tall 1:4 image — Cover crops top/bottom equally into PendulumArtWindow (no Auto nudge).
        using var background = new Image<Rgba32>(100, 400);
        for (var y = 0; y < background.Height; y++)
        for (var x = 0; x < background.Width; x++)
        {
            // Vertical gradient: top=magenta, bottom=cyan — centered Cover must show mid tones
            // at the hole vertical center (not the Auto PendulumVerticalOffset-shifted crop).
            var t = y / (float)(background.Height - 1);
            background[x, y] = new Rgba32(
                (byte)Math.Round(220 * (1 - t)),
                40,
                (byte)Math.Round(220 * t),
                255);
        }

        using var frame = CreateSolidFrame();
        var pendHole = OverFrameAutoArtComposer.PendulumArtWindow;
        ClearRect(frame, pendHole);

        using var result = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame,
            useSharedEffectLayout: false,
            pendulumLayout: OverFrameAutoArtComposer.GetPendulumLayout(CardFrameStyle.PendulumEffect),
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly,
            background);

        // Corners of the Pendulum hole (outside the narrow subject) show Cover background.
        var corner = result[pendHole.Left + 10, pendHole.Top + 10];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, corner.A);
        Assert.True(corner.R > 80 || corner.B > 80,
            $"Pendulum art-hole corner must show Cover background, got {corner}");

        // Vertical center of hole should be near mid-gradient (R≈B), not top-heavy magenta
        // from a +200 PendulumVerticalOffset Cover shift.
        var mid = result[pendHole.Left + 20, pendHole.Top + pendHole.Height / 2];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, mid.A);
        Assert.True(Math.Abs(mid.R - mid.B) < 50,
            $"Pendulum Cover must be vertically centered in hole (mid R≈B), got {mid}");

        // Below Pendulum hole: Effect square extends lower — must stay frame chrome, not Cover.
        var belowPendHole = result[pendHole.Left + pendHole.Width / 2, pendHole.Bottom + 6];
        Assert.True(belowPendHole.A >= 200,
            $"chrome below Pendulum hole must stay opaque, got {belowPendHole}");
        Assert.True(belowPendHole.B < 100,
            $"Cover must not spill below Pendulum art window, got {belowPendHole}");

        // Mint / dual-lore band must not receive Cover fill.
        var mint = OverFrameAutoArtComposer.PendulumMintTextBox;
        var mintSample = result[mint.Left + mint.Width / 2, mint.Top + 20];
        Assert.True(mintSample.A >= 200, $"Pendulum mint band must stay chrome, got {mintSample}");
        Assert.True(mintSample.B < 100,
            $"Cover must not fill Pendulum dual-lore, got {mintSample}");
    }

    [Fact]
    public void Compose_CustomArtOnly_Background_IgnoresSubjectOffsetAndScale()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var background = new Image<Rgba32>(80, 80, new Rgba32(15, 200, 90, 255));
        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);

        using var baseline = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly,
            background);
        using var moved = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 80,
            subjectOffsetY: -40,
            subjectScale: 2f,
            OverFrameComposeMode.CustomArtOnly,
            background);

        var art = OverFrameAutoArtComposer.ArtWindow;
        // Corner stays background-only (narrow subject does not cover it even at ×2 offset).
        var cornerX = art.Left + 6;
        var cornerY = art.Top + 6;
        Assert.Equal(baseline[cornerX, cornerY], moved[cornerX, cornerY]);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, baseline[cornerX, cornerY].A);
        Assert.True(baseline[cornerX, cornerY].G > 150);
    }

    [Fact]
    public void Compose_CustomArtOnly_BackgroundScale_EnlargesCoverCrop()
    {
        // Horizontal gradient background — ×2 Cover zooms in so edge samples differ from ×1.
        using var source = new Image<Rgba32>(40, 40, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(40, 40, new L8(0));
        // Tiny subject in the far corner so most of the hole stays background-only.
        source[2, 2] = new Rgba32(255, 0, 0, 255);
        mask[2, 2] = new L8(255);

        using var background = new Image<Rgba32>(200, 200);
        for (var y = 0; y < background.Height; y++)
        for (var x = 0; x < background.Width; x++)
        {
            var t = x / (float)(background.Width - 1);
            background[x, y] = new Rgba32(
                (byte)Math.Round(20 + 200 * t),
                40,
                (byte)Math.Round(220 * (1 - t)),
                255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);

        using var at1 = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly,
            background,
            backgroundScale: 1f);
        using var at2 = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly,
            background,
            backgroundScale: 2f);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var sampleX = art.Left + 12;
        var sampleY = art.Top + art.Height / 2;
        Assert.NotEqual(at1[sampleX, sampleY], at2[sampleX, sampleY]);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, at1[sampleX, sampleY].A);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, at2[sampleX, sampleY].A);

        // Still clipped: chrome left of hole must not pick up Cover cyan.
        var leftOfHole = at2[art.Left - 4, sampleY];
        Assert.True(leftOfHole.A >= 200);
        Assert.True(leftOfHole.B < 100,
            $"×2 Cover must not overflow art window, got {leftOfHole}");
    }

    [Fact]
    public void Compose_CustomArtOnly_BackgroundPan_ShiftsCoverInsideHole()
    {
        using var source = new Image<Rgba32>(40, 40, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(40, 40, new L8(0));
        source[2, 2] = new Rgba32(255, 0, 0, 255);
        mask[2, 2] = new L8(255);

        using var background = new Image<Rgba32>(200, 200);
        for (var y = 0; y < background.Height; y++)
        for (var x = 0; x < background.Width; x++)
        {
            var t = x / (float)(background.Width - 1);
            background[x, y] = new Rgba32(
                (byte)Math.Round(20 + 200 * t),
                40,
                (byte)Math.Round(220 * (1 - t)),
                255);
        }

        using var frame = CreateSolidFrame();
        ClearRect(frame, OverFrameAutoArtComposer.ArtWindow);
        var art = OverFrameAutoArtComposer.ArtWindow;
        var (maxPanX, _) = OverFrameAutoArtComposer.GetBackgroundPanLimits(
            background.Width, background.Height, art, backgroundScale: 2f);
        Assert.True(maxPanX > 10, $"need pan room for test, got {maxPanX}");

        using var centered = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly,
            background,
            backgroundScale: 2f,
            backgroundOffsetX: 0,
            backgroundOffsetY: 0);
        using var panned = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frame.Clone(),
            useSharedEffectLayout: true,
            pendulumLayout: null,
            subjectOffsetX: 0,
            subjectOffsetY: 0,
            subjectScale: 1f,
            OverFrameComposeMode.CustomArtOnly,
            background,
            backgroundScale: 2f,
            backgroundOffsetX: maxPanX,
            backgroundOffsetY: 0);

        var sampleX = art.Left + art.Width / 2;
        var sampleY = art.Top + art.Height / 2;
        Assert.NotEqual(centered[sampleX, sampleY], panned[sampleX, sampleY]);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, panned[sampleX, sampleY].A);

        // Pan still clipped to the hole (Cover cyan must not appear above).
        var above = panned[art.Left + 8, art.Top - 4];
        Assert.True(above.A >= 200);
        Assert.True(above.B < 100,
            $"panned Cover must not spill above art window, got {above}");
    }

    [Fact]
    public void ComposeCustomBackgroundOnly_RespectsBackgroundScaleAndPan()
    {
        using var background = new Image<Rgba32>(200, 200);
        for (var y = 0; y < background.Height; y++)
        for (var x = 0; x < background.Width; x++)
        {
            var t = y / (float)(background.Height - 1);
            background[x, y] = new Rgba32(
                40,
                (byte)Math.Round(20 + 200 * t),
                (byte)Math.Round(220 * (1 - t)),
                255);
        }

        var art = OverFrameAutoArtComposer.ArtWindow;
        var (_, maxPanY) = OverFrameAutoArtComposer.GetBackgroundPanLimits(
            background.Width, background.Height, art, backgroundScale: 2f);

        using var centered = OverFrameAutoArtComposer.ComposeCustomBackgroundOnly(
            CardFrameStyle.Effect,
            background: background,
            backgroundScale: 2f);
        using var panned = OverFrameAutoArtComposer.ComposeCustomBackgroundOnly(
            CardFrameStyle.Effect,
            background: background,
            backgroundScale: 2f,
            backgroundOffsetY: maxPanY);

        var mid = art.Left + art.Width / 2;
        var sampleY = art.Top + art.Height / 2;
        Assert.NotEqual(centered[mid, sampleY], panned[mid, sampleY]);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, panned[mid, sampleY].A);
    }

    [Fact]
    public void ComposeBaseWithoutSubject_CustomArtOnly_IncludesFixedBackground()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var background = new Image<Rgba32>(64, 64, new Rgba32(30, 40, 220, 255));
        using var baseLayer = OverFrameAutoArtComposer.ComposeBaseWithoutSubject(
            source,
            mask,
            CardFrameStyle.Effect,
            subjectScale: 1f,
            composeMode: OverFrameComposeMode.CustomArtOnly,
            background: background);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var hole = baseLayer[art.Left + 20, art.Top + 20];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, hole.A);
        Assert.True(hole.B > 180, $"drag base must keep Cover background, got {hole}");

        // No subject on the base layer.
        var mid = baseLayer[art.Left + art.Width / 2, art.Top + art.Height / 2];
        Assert.True(mid.R < 80, $"base must not include Card Art subject, got {mid}");
    }

    [Fact]
    public void ComposeCustomBackgroundOnly_CoverFillsArtHole_WithoutSubject()
    {
        using var background = new Image<Rgba32>(64, 64, new Rgba32(30, 40, 220, 255));
        using var preview = OverFrameAutoArtComposer.ComposeCustomBackgroundOnly(
            CardFrameStyle.Effect,
            background: background);

        Assert.Equal(OverFrameConstants.Width, preview.Width);
        Assert.Equal(OverFrameConstants.Height, preview.Height);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var hole = preview[art.Left + 20, art.Top + 20];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, hole.A);
        Assert.True(hole.B > 180, $"Cover background must fill art hole, got {hole}");

        var cream = OverFrameAutoArtComposer.EffectLoreCream;
        var lore = preview[cream.Left + cream.Width / 2, cream.Top + 40];
        Assert.True(lore.A > 200, $"lore must be solid cream without subject, got {lore}");
        Assert.InRange(lore.R, 200, 255);
    }

    [Fact]
    public void RenderSubjectDragLayer_CustomArtOnly_IncludesBottomChromePunch()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(0, 0, 0, 0));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(40, 200, 255, 255);
            mask[x, y] = new L8(255);
        }

        using var layer = OverFrameAutoArtComposer.RenderSubjectDragLayer(
            source,
            mask,
            CardFrameStyle.Effect,
            subjectScale: 2f * OverFrameAutoArtComposer.OverflowScale,
            composeMode: OverFrameComposeMode.CustomArtOnly);

        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = creamR.Left + creamR.Width / 2;
        var belowY = creamR.Bottom + 10;
        var below = layer[midX, belowY];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, below.A);
        Assert.True(below.B > 100, $"drag layer must show subject below lore, got {below}");

        // Cream interior stays clear on the drag layer (lore lives on the base).
        var loreY = creamR.Top + 40;
        Assert.Equal(0, layer[midX, loreY].A);
    }

    private static Image<Rgba32> CreateSolidFrame() =>
        new(OverFrameConstants.Width, OverFrameConstants.Height, new Rgba32(220, 200, 40, 255));

    private static int ColorDistance(Rgba32 a, Rgba32 b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    /// <summary>
    /// Covered lore is soft-blended (≈80% cream via
    /// <see cref="OverFrameAutoArtComposer.TextBoxFrameOpacity"/>), so RGB may drift
    /// from exact frame cream while staying chrome-opaque and cream-dominant.
    /// </summary>
    private static void AssertLoreCreamChrome(Rgba32 lore, Rgba32 cream, string label)
    {
        Assert.True(lore.A >= 200, $"{label} must stay opaque, got {lore}");
        Assert.True(
            Math.Abs(lore.R - cream.R) < 55 &&
            Math.Abs(lore.G - cream.G) < 55 &&
            Math.Abs(lore.B - cream.B) < 55,
            $"expected cream-dominant {label}, got {lore}");
    }

    private static void PaintEffectStyleLore(
        Image<Rgba32> frame,
        Rgba32 cream,
        Rgba32? gold = null)
    {
        var goldC = gold ?? new Rgba32(180, 100, 50, 255);
        var art = OverFrameAutoArtComposer.ArtWindow;
        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var outer = OverFrameAutoArtComposer.EffectLoreOuter;
        var cut = OverFrameAutoArtComposer.EffectLoreCutTop;

        for (var y = art.Bottom; y < cut; y++)
        for (var x = art.Left; x < art.Right; x++)
            frame[x, y] = new Rgba32(60, 40, 30, 255);

        for (var y = cut; y < creamR.Bottom; y++)
        for (var x = outer.Left; x < outer.Right; x++)
            frame[x, y] = goldC;

        for (var y = creamR.Top; y < creamR.Bottom; y++)
        for (var x = creamR.Left; x < creamR.Right; x++)
            frame[x, y] = cream;
    }

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

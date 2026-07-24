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
        // Foil + rembg must share one scale. Narrow subjects also get bilateral side-bias
        // scale-up (fills L/R chrome), so verify via subject canvas width rather than a
        // foil marker that side-bias may push outside the art hole.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 80, 200, 255));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
            source[x, y] = new Rgba32(220, 30, 20, 255);

        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 40; x < 60; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var midY = OverFrameAutoArtComposer.ArtWindow.Top + OverFrameAutoArtComposer.ArtWindow.Height / 2;
        var subjectLeft = FindSubjectLeftAtY(result, midY);
        var subjectRight = FindSubjectRightExclusiveAtY(result, midY);
        Assert.True(subjectLeft >= 0 && subjectRight > subjectLeft, "expected rembg subject");

        var scale = ComputeExpectedScale(100, 100);
        var expectedW = Math.Max(1, (int)MathF.Round(20 * scale));
        Assert.True(scale > 1.5f, $"expected meaningful overflow scale, got {scale}");
        Assert.InRange(subjectRight - subjectLeft, expectedW - 4, expectedW + 4);
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

    [Fact]
    public void ResolveSideOverframeBias_MissingLeft_ShiftsLeft()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        var target = OverFrameAutoArtComposer.GetSideOverframeCorrectionTargetPx(art);
        // Subject sits inside the hole on the left but past the right edge.
        var subjectLeft = art.Left + 40;
        var subjectRight = art.Right + 80;
        var bias = OverFrameAutoArtComposer.ResolveSideOverframeBias(art, subjectLeft, subjectRight);

        Assert.Equal(1f, bias.ScaleMultiplier);
        Assert.True(bias.HorizontalOffset < 0, $"expected left shift, got {bias.HorizontalOffset}");
        Assert.Equal((art.Left - target) - subjectLeft, bias.HorizontalOffset);
    }

    [Fact]
    public void ResolveSideOverframeBias_MissingRight_ShiftsRight()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        var target = OverFrameAutoArtComposer.GetSideOverframeCorrectionTargetPx(art);
        var subjectLeft = art.Left - 80;
        var subjectRight = art.Right - 40;
        var bias = OverFrameAutoArtComposer.ResolveSideOverframeBias(art, subjectLeft, subjectRight);

        Assert.Equal(1f, bias.ScaleMultiplier);
        Assert.True(bias.HorizontalOffset > 0, $"expected right shift, got {bias.HorizontalOffset}");
        Assert.Equal((art.Right + target) - subjectRight, bias.HorizontalOffset);
    }

    [Fact]
    public void ResolveSideOverframeBias_AlreadyOverflowsBoth_NoChange()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        var min = OverFrameAutoArtComposer.SideOverframeMinPx;
        var bias = OverFrameAutoArtComposer.ResolveSideOverframeBias(
            art, art.Left - min - 10, art.Right + min + 10);

        Assert.Equal(0, bias.HorizontalOffset);
        Assert.Equal(1f, bias.ScaleMultiplier);
    }

    [Fact]
    public void ResolveSideOverframeBias_MissingBoth_BilateralScaleBoost_NoShift()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        // Subject hugs the art-hole width — spell/landscape rembg that only overframes
        // top/bottom. Legacy fixed 1.08× left teal gutters; need a dynamic boost.
        var subjectLeft = art.Left + 2;
        var subjectRight = art.Right - 2;
        var bias = OverFrameAutoArtComposer.ResolveSideOverframeBias(art, subjectLeft, subjectRight);

        Assert.Equal(0, bias.HorizontalOffset);
        var expected = OverFrameAutoArtComposer.ComputeScaleMultiplierForMissingSides(
            art, subjectLeft, subjectRight, fixLeft: true, fixRight: true);
        Assert.Equal(expected, bias.ScaleMultiplier);
        Assert.True(bias.ScaleMultiplier > 1.08f,
            $"expected boost > legacy 1.08, got {bias.ScaleMultiplier}");
    }

    [Fact]
    public void ComputeScaleMultiplierForMissingSides_HoleWidthSubject_ReachesCorrectionTarget()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        var subjectLeft = art.Left;
        var subjectRight = art.Right;
        var m = OverFrameAutoArtComposer.ComputeScaleMultiplierForMissingSides(
            art, subjectLeft, subjectRight, fixLeft: true, fixRight: true);
        var artCenterX = art.Left + art.Width / 2f;
        var target = OverFrameAutoArtComposer.GetSideOverframeCorrectionTargetPx(art);
        var newLeft = artCenterX + (subjectLeft - artCenterX) * m;
        var newRight = artCenterX + (subjectRight - artCenterX) * m;

        Assert.True(newLeft <= art.Left - target + 0.5f,
            $"left {newLeft} should be ≤ {art.Left - target}");
        Assert.True(newRight >= art.Right + target - 0.5f,
            $"right {newRight} should be ≥ {art.Right + target}");
    }

    [Fact]
    public void ClampHorizontalBiasForArtCover_RejectsAbsurdRightShift()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        // Cover slack is ~100px for a square 100x100 @ OverflowScale — 400px is smear territory.
        const int centeredBgX = -12;
        const int scaledW = 729;
        var clamped = OverFrameAutoArtComposer.ClampHorizontalBiasForArtCover(
            400, centeredBgX, scaledW, art);

        Assert.True(clamped < 400, $"expected clamp below raw 400, got {clamped}");
        Assert.True(centeredBgX + clamped <= art.Left);
        Assert.True(centeredBgX + clamped + scaledW >= art.Right);
    }

    [Fact]
    public void ClampHorizontalBiasForArtCover_RejectsAbsurdLeftShift()
    {
        var art = OverFrameAutoArtComposer.ArtWindow;
        const int centeredBgX = -12;
        const int scaledW = 729;
        var clamped = OverFrameAutoArtComposer.ClampHorizontalBiasForArtCover(
            -400, centeredBgX, scaledW, art);

        Assert.True(clamped > -400, $"expected clamp above raw -400, got {clamped}");
        Assert.True(centeredBgX + clamped <= art.Left);
        Assert.True(centeredBgX + clamped + scaledW >= art.Right);
    }

    [Fact]
    public void Compose_MissingLeftOverframe_ShiftsCompositionLeft()
    {
        // Subject already overframes right when centered; sits slightly inside on the left.
        // Shift and/or Cover-safe scale rescue must reach SideOverframeMinPx (no edge-smear).
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 15; x < 100; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var midY = art.Top + art.Height / 2;
        var subjectLeft = FindSubjectLeftAtY(result, midY);
        Assert.True(subjectLeft >= 0, "expected rembg subject in art band");
        Assert.True(
            subjectLeft <= art.Left - OverFrameAutoArtComposer.SideOverframeMinPx,
            $"expected left overframe >={OverFrameAutoArtComposer.SideOverframeMinPx}px, subjectLeft={subjectLeft}, art.Left={art.Left}");
    }

    [Fact]
    public void Compose_MissingRightOverframe_ShiftsCompositionRight()
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 0; x < 85; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var midY = art.Top + art.Height / 2;
        var subjectRight = FindSubjectRightExclusiveAtY(result, midY);
        Assert.True(subjectRight > 0, "expected rembg subject in art band");
        Assert.True(
            subjectRight >= art.Right + OverFrameAutoArtComposer.SideOverframeMinPx,
            $"expected right overframe >={OverFrameAutoArtComposer.SideOverframeMinPx}px, subjectRight={subjectRight}, art.Right={art.Right}");
    }

    [Fact]
    public void Compose_MissingBothSideOverframes_ScalesToFillLeftAndRight()
    {
        // Spell-like repro: rembg spans most of the source width so Cover placement leaves
        // subject ≈ art-hole wide (only T/B overflow). Legacy 1.08× left teal L/R gutters;
        // dynamic scale must punch both sides. Keep <92% opaque so the full-mask guard passes.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 5; y < 95; y++)
        for (var x = 15; x < 85; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var midY = art.Top + art.Height / 2;
        var subjectLeft = FindSubjectLeftAtY(result, midY);
        var subjectRight = FindSubjectRightExclusiveAtY(result, midY);
        Assert.True(subjectLeft >= 0 && subjectRight > subjectLeft, "expected rembg subject");
        Assert.True(
            subjectLeft <= art.Left - OverFrameAutoArtComposer.SideOverframeMinPx,
            $"expected left overframe, subjectLeft={subjectLeft}, art.Left={art.Left}");
        Assert.True(
            subjectRight >= art.Right + OverFrameAutoArtComposer.SideOverframeMinPx,
            $"expected right overframe, subjectRight={subjectRight}, art.Right={art.Right}");
        // Smear coverage lives in Compose_FarLeftSubject_DoesNotHorizontalEdgeSmear
        // (uniform red subjects false-trigger the leftmost-column run check).
    }

    [Fact]
    public void Compose_FarLeftSubject_DoesNotHorizontalEdgeSmear()
    {
        // Spell/landscape repro: rembg blob hugs the left, missing right overframe by hundreds
        // of px. Pre-fix bias applied a huge +X and FillRegion clamped sx->0 (left-column smear).
        using var source = new Image<Rgba32>(120, 80);
        using var mask = new Image<L8>(120, 80, new L8(0));
        for (var y = 0; y < 80; y++)
        for (var x = 0; x < 120; x++)
        {
            // Horizontal gradient — edge-repeat would flatten R across a wide run.
            source[x, y] = new Rgba32((byte)(x * 2), (byte)(40 + y), 180, 255);
        }

        for (var y = 8; y < 72; y++)
        for (var x = 4; x < 22; x++)
            mask[x, y] = new L8(255);

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var midY = art.Top + art.Height / 2;
        AssertNoHorizontalEdgeSmear(result, art, midY);

        // Art window must stay filled with varying foil (not black matte gaps from OOB skip).
        var left = result[art.Left + 2, midY];
        var mid = result[art.Left + art.Width / 2, midY];
        var right = result[art.Right - 3, midY];
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, left.A);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, mid.A);
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, right.A);
        Assert.True(left.R != mid.R || mid.R != right.R,
            "expected horizontal color variation across art window (Cover still spans hole)");
    }

    /// <summary>
    /// Fails when the left &gt;=55% of the art window is a constant color matching classic
    /// edge-clamp smear (leftmost column stretched).
    /// </summary>
    private static void AssertNoHorizontalEdgeSmear(Image<Rgba32> image, Rectangle art, int y)
    {
        var first = image[art.Left, y];
        var run = 1;
        for (var x = art.Left + 1; x < art.Right; x++)
        {
            var px = image[x, y];
            if (px.R == first.R && px.G == first.G && px.B == first.B && px.A == first.A)
                run++;
            else
                break;
        }

        Assert.True(
            run < art.Width * 0.55,
            $"horizontal edge-smear: leftmost column repeated for {run}/{art.Width} art-window px");
    }

    [Fact]
    public void Compose_AlreadyOverflowsBothSides_DoesNotApplyUnnecessaryShift()
    {
        // Wide subject that already punches past both art-hole edges when centered.
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 40, 80, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 10; y < 90; y++)
        for (var x = 5; x < 95; x++)
        {
            source[x, y] = new Rgba32(220, 30, 20, 255);
            mask[x, y] = new L8(255);
        }

        var art = OverFrameAutoArtComposer.ArtWindow;
        var cover = Math.Max(art.Width / 100f, art.Height / 100f);
        var scale = cover * OverFrameAutoArtComposer.OverflowScale;
        var scaledW = Math.Max(1, (int)MathF.Round(100 * scale));
        var artCenterX = art.Left + art.Width / 2f;
        var bgX = (int)MathF.Round(artCenterX - scaledW / 2f);
        var expectedLeft = bgX + (int)MathF.Round(5 * scale);
        var expectedRight = expectedLeft + Math.Max(1, (int)MathF.Round(90 * scale));

        // Confirm the unshifted placement already overframes both sides.
        var probe = OverFrameAutoArtComposer.ResolveSideOverframeBias(art, expectedLeft, expectedRight);
        Assert.Equal(0, probe.HorizontalOffset);
        Assert.Equal(1f, probe.ScaleMultiplier);

        using var frame = CreateSolidFrame();
        using var result = OverFrameAutoArtComposer.Compose(source, mask, frame);

        var midY = art.Top + art.Height / 2;
        var subjectLeft = FindSubjectLeftAtY(result, midY);
        var subjectRight = FindSubjectRightExclusiveAtY(result, midY);
        Assert.True(subjectLeft >= 0 && subjectRight > subjectLeft);
        // Placement must stay at the centered (no-bias) X — allow 2px resize rounding.
        Assert.InRange(subjectLeft, expectedLeft - 2, expectedLeft + 2);
        Assert.InRange(subjectRight, expectedRight - 2, expectedRight + 2);
    }

    private static int FindSubjectLeftAtY(Image<Rgba32> image, int y)
    {
        for (var x = 0; x < image.Width; x++)
        {
            var p = image[x, y];
            if (p.A == OverFrameAutoArtComposer.FoilMaskAlpha && p.R > 180 && p.G < 80)
                return x;
        }

        return -1;
    }

    private static int FindSubjectRightExclusiveAtY(Image<Rgba32> image, int y)
    {
        for (var x = image.Width - 1; x >= 0; x--)
        {
            var p = image[x, y];
            if (p.A == OverFrameAutoArtComposer.FoilMaskAlpha && p.R > 180 && p.G < 80)
                return x + 1;
        }

        return -1;
    }

    private static float ComputeExpectedScale(int sourceWidth, int sourceHeight)
    {
        var cover = Math.Max(
            OverFrameAutoArtComposer.ArtWindow.Width / (float)sourceWidth,
            OverFrameAutoArtComposer.ArtWindow.Height / (float)sourceHeight);
        var scale = cover * OverFrameAutoArtComposer.OverflowScale;
        // Narrow centered subjects trigger bilateral both-missing boost.
        var art = OverFrameAutoArtComposer.ArtWindow;
        var scaledW = Math.Max(1, (int)MathF.Round(sourceWidth * scale));
        var artCenterX = art.Left + art.Width / 2f;
        var bgX = (int)MathF.Round(artCenterX - scaledW / 2f);
        // Compose_ScalesBackgroundWithSubject uses subject x=40..60 on a square source.
        var subjectLeft = bgX + (int)MathF.Round(40 * scale);
        var subjectRight = subjectLeft + Math.Max(1, (int)MathF.Round(20 * scale));
        var bias = OverFrameAutoArtComposer.ResolveSideOverframeBias(art, subjectLeft, subjectRight);
        return scale * bias.ScaleMultiplier;
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
    public void Compose_UsesSharedEffectArtWindow_EvenWhenFrameHoleIsLarger()
    {
        // Oversized hole larger than Effect — compose must still use ArtWindow.
        using var frame = CreateSolidFrame();
        var largeHole = new Rectangle(76, 178, 555, 555);
        ClearRect(frame, largeHole);

        using var source = new Image<Rgba32>(512, 512, new Rgba32(40, 200, 120, 255));
        using var mask = new Image<L8>(512, 512, new L8(0));
        for (var y = 80; y < 432; y++)
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

        // Outside shared ArtWindow but inside the oversized template hole.
        // Side-bias may rembg-punch this ring (foil A≈4); unpatched hole would be black matte.
        var ring = result[largeHole.Left + 2, largeHole.Top + 2];
        Assert.False(
            ring.A == OverFrameAutoArtComposer.FoilMaskAlpha && ring.R < 20 && ring.G < 20 && ring.B < 20,
            $"expected patched chrome or rembg overframe outside ArtWindow, got {ring}");
        Assert.True(
            ring.A >= 200 || ring.A == OverFrameAutoArtComposer.FoilMaskAlpha,
            $"expected patched chrome outside ArtWindow, got {ring}");
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
        Assert.True(lore.A >= 200, $"lore must stay opaque, got {lore}");
        Assert.True(Math.Abs(lore.R - cream.R) < 40, $"expected lore cream, got {lore}");
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

        var loreTop = result[midX, OverFrameAutoArtComposer.EffectLoreCream.Top];
        Assert.True(loreTop.A >= 200, $"lore top must stay opaque, got {loreTop}");
        Assert.True(Math.Abs(loreTop.R - cream.R) < 40, $"expected cream at lore top, got {loreTop}");
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

        // Cream interior still Mirrorjade-opaque (not foil).
        var creamR = OverFrameAutoArtComposer.EffectLoreCream;
        var lore = result[creamR.Left + creamR.Width / 2, loreY];
        Assert.True(lore.A >= 200, $"lore cream must stay blended chrome, got {lore}");
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
        Assert.True(lore.A >= 200, $"lore cream must stay blended chrome, got {lore}");
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
        Assert.True(lore.A >= 200, $"lore interior must stay opaque, got {lore}");
        Assert.True(Math.Abs(lore.R - cream.R) < 40, $"expected lore cream, got {lore}");
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
        Assert.True(lore.A >= 200, $"lore interior must stay opaque, got {lore}");
        Assert.True(Math.Abs(lore.R - cream.R) < 40, $"expected lore cream, got {lore}");
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
        Assert.True(lore.A >= 200, $"lore interior must stay opaque, got {lore}");
        Assert.True(Math.Abs(lore.R - cream.R) < 40, $"expected lore cream, got {lore}");
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
        Assert.True(lore.A >= 200, $"lore must be opaque, got {lore}");
        Assert.True(lore.R < 250 || lore.B < 250, $"magenta cutout bled into lore: {lore}");
        Assert.True(Math.Abs(lore.R - cream.R) < 40 && Math.Abs(lore.G - cream.G) < 40,
            $"expected cream-dominant lore panel, got {lore}");

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

    private static Image<Rgba32> CreateSolidFrame() =>
        new(OverFrameConstants.Width, OverFrameConstants.Height, new Rgba32(220, 200, 40, 255));

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

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Floowan.Core.Imaging;

/// <summary>
/// Converts a source image plus a subject mask into a 704×1024 over-frame canvas.
/// Official OF arts keep the full illustration as a foil-mask (A≈4) base — never a
/// solid black matte. Game illusts are Cover-scaled into the real frame hole, then
/// overflowed. The type-line strip under the art hole always shows foil art so it
/// meets the cream lore panel with no chrome gap. Overflow silhouette is hard-cut
/// at the lore panel top (full width) so it cannot bleed past the lore left/right
/// into the outer card border. Only the cream panel itself stays opaque below that.
/// Rembg is used for silhouette punching outside that panel.
/// </summary>
public static class OverFrameAutoArtComposer
{
    public const byte VisibleAlphaThreshold = 12;
    public const byte MaskKeepThreshold = 140;

    /// <summary>
    /// Near-transparent coverage mask used by official over-frame arts and required
    /// by the Nexus guide comments (foil / royal finish reads this value).
    /// </summary>
    public const byte FoilMaskAlpha = 4;

    /// <summary>
    /// Lore panel: mostly opaque frame chrome over a soft full-art underlay.
    /// High opacity hides hard cutout edges; a little underlay keeps Mirrorjade depth.
    /// </summary>
    public const float TextBoxFrameOpacity = 0.92f;

    /// <summary>
    /// Fallback art window matching Master Duel <c>card_frame</c> Effect (and most)
    /// templates. Not 512×512 at (96,168) — that under-filled the real ~527×528 hole
    /// and left the black gap inside the white art border.
    /// </summary>
    public static Rectangle ArtWindow { get; } = new(89, 191, 527, 528);

    /// <summary>
    /// Scale relative to the art window. Values &gt; 1 make the subject break out
    /// of the frame when of_card_asset is enabled.
    /// </summary>
    public const float OverflowScale = 1.38f;

    public static Image<Rgba32> Compose(
        Image<Rgba32> source,
        Image<L8> mask,
        CardFrameStyle frameStyle = CardFrameStyle.EffectExt,
        string? frameDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);

        using var frame = CardFrameTemplates.Load(frameStyle, frameDirectory);
        return Compose(source, mask, frame);
    }

    public static Image<Rgba32> Compose(
        Image<Rgba32> source,
        Image<L8> mask,
        Image<Rgba32> frameTemplate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(frameTemplate);

        using var alignedMask = mask.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = source.Size,
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
        using var cutout = source.Clone();

        var opaque = 0;
        for (var y = 0; y < cutout.Height; y++)
        {
            var pixels = cutout.DangerousGetPixelRowMemory(y).Span;
            var maskPixels = alignedMask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < pixels.Length; x++)
            {
                var pixel = pixels[x];
                var keep = maskPixels[x].PackedValue >= MaskKeepThreshold;
                if (keep)
                {
                    opaque++;
                    pixel.A = 255;
                }
                else
                {
                    pixel.A = 0;
                }

                pixels[x] = pixel;
            }
        }

        var total = cutout.Width * cutout.Height;
        if (total > 0 && opaque / (float)total > 0.92f)
        {
            throw new InvalidOperationException(
                "Background removal left almost the entire image opaque. " +
                "Pick a cleaner source art or edit the PNG manually so the subject has a transparent background.");
        }

        var bounds = FindVisibleBounds(cutout);
        if (bounds.IsEmpty)
            throw new InvalidOperationException("Background removal did not find a visible subject.");

        using var subject = cutout.Clone(ctx => ctx.Crop(bounds));

        using var frame = frameTemplate.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(Assets.OverFrameConstants.Width, Assets.OverFrameConstants.Height),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        // Use the frame's real transparent hole (Effect≈527, EffectExt≈555), not a
        // hardcoded 512² — game illusts are 512×512 and must Cover into that hole.
        var artWindow = ResolveArtWindow(frame);

        var cover = Math.Max(
            artWindow.Width / (float)source.Width,
            artWindow.Height / (float)source.Height);
        var scale = cover * OverflowScale;
        var scaledSourceW = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var scaledSourceH = Math.Max(1, (int)MathF.Round(source.Height * scale));

        var artCenterX = artWindow.Left + artWindow.Width / 2f;
        var artCenterY = artWindow.Top + artWindow.Height / 2f;
        var bgX = (int)MathF.Round(artCenterX - scaledSourceW / 2f);
        var bgY = (int)MathF.Round(artCenterY - scaledSourceH / 2f);

        var targetWidth = Math.Max(1, (int)MathF.Round(subject.Width * scale));
        var targetHeight = Math.Max(1, (int)MathF.Round(subject.Height * scale));
        using var resized = subject.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(targetWidth, targetHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        var xOffset = bgX + (int)MathF.Round(bounds.Left * scale);
        var yOffset = bgY + (int)MathF.Round(bounds.Top * scale);

        // 1) Art window + type-line strip (meets outer lore border) + soft lore underlay.
        var textBox = ResolveTextBox(frame, artWindow);
        var loreCutTop = ResolveLoreCutTop(frame, textBox, artWindow);
        var loreOuter = ResolveLoreOuterBorder(frame, textBox, artWindow);
        var typeLineStrip = ResolveTypeLineStrip(artWindow, loreCutTop, loreOuter);
        var canvas = CreateFoilMaskArtBase(source, artWindow, scaledSourceW, scaledSourceH, bgX, bgY);

        // Fill out to the OUTER lore border (not just the art-hole / cream width) so the
        // illustration meets the lore box's outer gold edge with no orange side gaps.
        FillRegionWithScaledArt(canvas, typeLineStrip, source, scaledSourceW, scaledSourceH, bgX, bgY);

        // Soft continuation of the FULL illustration under the lore box (Mirrorjade).
        // Do NOT blit the rembg cutout here — cutout edges (sleeve panels, etc.) created
        // hard vertical “glitch” lines inside the lore panel.
        FillRegionWithScaledArt(canvas, textBox, source, scaledSourceW, scaledSourceH, bgX, bgY);

        // 2) Overflow silhouette — hard-cut at the OUTER lore top (above the gold rim),
        //    not the cream inner edge (that covered the top border).
        var occupied = new bool[canvas.Width * canvas.Height];
        BlitSubjectFoilMask(canvas, resized, xOffset, yOffset, occupied, loreCutTop);

        // 3) Frame chrome + mostly-opaque lore panel over the soft underlay.
        EnsureArtWindowHole(frame, artWindow);
        DrawFramePunchedByRectangleAndSilhouette(canvas, frame, artWindow, textBox, typeLineStrip, occupied);
        PaintLorePanel(canvas, frame, textBox);

        return canvas;
    }

    /// <summary>
    /// Reads the transparent art hole from a 704×1024 frame template.
    /// </summary>
    public static Rectangle DetectArtWindow(Image<Rgba32> frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var left = frame.Width;
        var top = frame.Height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < frame.Height; y++)
        {
            var pixels = frame.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < pixels.Length; x++)
            {
                if (pixels[x].A > VisibleAlphaThreshold)
                    continue;

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
            return Rectangle.Empty;

        var hole = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        // Ignore stray transparent edge pixels; real MD holes are ~500px+.
        return hole.Width >= 400 && hole.Height >= 400 ? hole : Rectangle.Empty;
    }

    private static Rectangle ResolveArtWindow(Image<Rgba32> frame)
    {
        var detected = DetectArtWindow(frame);
        return detected.IsEmpty ? ArtWindow : detected;
    }

    /// <summary>
    /// Cream / lavender lore panel only (not the type-line strip under the art hole).
    /// Overflow may punch between <see cref="ArtWindow"/> bottom and this rect's top;
    /// it must not punch inside this rect. Horizontal bounds follow the real cream
    /// panel (often slightly wider than the art hole).
    /// </summary>
    public static Rectangle DetectTextBox(Image<Rgba32> frame, Rectangle artWindow)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bandLeft = Math.Max(0, artWindow.Left);
        var bandRight = Math.Min(frame.Width, artWindow.Right);
        if (bandRight <= bandLeft)
            return Rectangle.Empty;

        // Require a majority-cream row inside the art-hole band. Sparse bright pixels
        // on the gold lore border (lum≥155) used to pull Top up ~20px.
        const float creamRowFraction = 0.50f;
        var panelTop = -1;
        for (var y = Math.Max(0, artWindow.Bottom); y < frame.Height; y++)
        {
            var pixels = frame.DangerousGetPixelRowMemory(y).Span;
            var cream = 0;
            var counted = 0;
            for (var x = bandLeft; x < bandRight; x++)
            {
                var p = pixels[x];
                if (p.A <= 200)
                    continue;

                counted++;
                if (IsLorePanelPixel(p))
                    cream++;
            }

            if (counted > 0 && cream / (float)counted >= creamRowFraction)
            {
                panelTop = y;
                break;
            }
        }

        if (panelTop < 0)
            return Rectangle.Empty;

        var left = frame.Width;
        var top = panelTop;
        var right = -1;
        var bottom = -1;
        // Cream can be slightly wider than the art hole, but must not latch onto the
        // cool outer card margin (which can also be lum≥155).
        var scanLeft = Math.Max(0, bandLeft - 60);
        var scanRight = Math.Min(frame.Width, bandRight + 60);

        for (var y = panelTop; y < frame.Height; y++)
        {
            var pixels = frame.DangerousGetPixelRowMemory(y).Span;
            var creamInBand = 0;
            var countedInBand = 0;
            for (var x = scanLeft; x < scanRight; x++)
            {
                var p = pixels[x];
                if (p.A <= 200)
                    continue;

                if (x >= bandLeft && x < bandRight)
                {
                    countedInBand++;
                    if (IsLorePanelPixel(p))
                        creamInBand++;
                }

                if (!IsLorePanelPixel(p))
                    continue;

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }

            // Stop at the first mostly-non-cream row after the panel has started.
            if (countedInBand > 0 && creamInBand / (float)countedInBand < creamRowFraction && bottom >= panelTop)
                break;
        }

        if (right < left || bottom < top)
            return Rectangle.Empty;

        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    /// <summary>
    /// Expands the cream lore panel to include its outer gold/orange decorative border.
    /// Art should meet this outer edge; <paramref name="creamPanel"/> stays the opaque text area.
    /// </summary>
    public static Rectangle DetectLoreOuterBorder(Image<Rgba32> frame, Rectangle creamPanel)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (creamPanel.IsEmpty || creamPanel.Width <= 0 || creamPanel.Height <= 0)
            return creamPanel;

        var left = creamPanel.Left;
        var right = creamPanel.Right - 1;
        var y0 = Math.Clamp(creamPanel.Top + Math.Max(1, creamPanel.Height / 5), 0, frame.Height - 1);
        var y1 = Math.Clamp(creamPanel.Top + (creamPanel.Height * 4) / 5, y0, frame.Height - 1);
        // Double-line gold rims have 1px dark gaps; allow a tiny skip but do not jump
        // across to unrelated card-edge chrome.
        const int maxGap = 2;
        const int maxRim = 28;

        for (var y = y0; y <= y1; y++)
        {
            var row = frame.DangerousGetPixelRowMemory(y).Span;
            var l = ExpandLoreRimLeft(row, creamPanel.Left, maxRim, maxGap);
            var r = ExpandLoreRimRight(row, creamPanel.Right - 1, frame.Width, maxRim, maxGap);
            left = Math.Min(left, l);
            right = Math.Max(right, r);
        }

        return Rectangle.FromLTRB(left, creamPanel.Top, right + 1, creamPanel.Bottom);
    }

    private static int ExpandLoreRimLeft(ReadOnlySpan<Rgba32> row, int creamLeft, int maxRim, int maxGap)
    {
        var left = creamLeft;
        var gap = 0;
        var limit = Math.Max(0, creamLeft - maxRim);
        for (var x = creamLeft - 1; x >= limit; x--)
        {
            if (IsLoreChromePixel(row[x]))
            {
                left = x;
                gap = 0;
            }
            else if (++gap > maxGap)
            {
                break;
            }
        }

        return left;
    }

    private static int ExpandLoreRimRight(ReadOnlySpan<Rgba32> row, int creamRight, int width, int maxRim, int maxGap)
    {
        var right = creamRight;
        var gap = 0;
        var limit = Math.Min(width - 1, creamRight + maxRim);
        for (var x = creamRight + 1; x <= limit; x++)
        {
            if (IsLoreChromePixel(row[x]))
            {
                right = x;
                gap = 0;
            }
            else if (++gap > maxGap)
            {
                break;
            }
        }

        return right;
    }

    /// <summary>
    /// Top of the lore box's outer gold rim (above the cream). Art must hard-cut here
    /// so it meets the outer border instead of covering it.
    /// </summary>
    public static int DetectLoreOuterTop(Image<Rgba32> frame, Rectangle creamPanel, Rectangle artWindow)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (creamPanel.IsEmpty)
            return Math.Min(artWindow.Bottom + 48, frame.Height - 1);

        var cut = creamPanel.Top;
        var x0 = creamPanel.Left + Math.Max(1, creamPanel.Width / 5);
        var x1 = creamPanel.Right - Math.Max(1, creamPanel.Width / 5);
        if (x1 <= x0)
        {
            x0 = creamPanel.Left;
            x1 = creamPanel.Right;
        }

        for (var y = creamPanel.Top - 1; y >= Math.Max(0, artWindow.Bottom); y--)
        {
            var row = frame.DangerousGetPixelRowMemory(y).Span;
            var chrome = 0;
            var counted = 0;
            for (var x = x0; x < x1; x++)
            {
                var p = row[x];
                if (p.A <= 200)
                    continue;

                counted++;
                if (IsLoreChromePixel(p) || IsLorePanelPixel(p))
                    chrome++;
            }

            if (counted > 0 && chrome / (float)counted >= 0.40f)
                cut = y;
            else
                break;
        }

        return cut;
    }

    private static bool IsLorePanelPixel(Rgba32 p)
    {
        var lum = (p.R + p.G + p.B) / 3;
        if (lum < 155)
            return false;

        // Reject cool blue-gray outer card margin (also bright, but not lore cream).
        if (p.B > p.R + 15 && p.B >= p.G)
            return false;

        return true;
    }

    /// <summary>
    /// Gold/orange lore-box rim (warm mid-tones). Excludes cool outer card margin.
    /// </summary>
    private static bool IsLoreChromePixel(Rgba32 p)
    {
        if (p.A <= 200)
            return false;
        if (IsLorePanelPixel(p))
            return true;

        var lum = (p.R + p.G + p.B) / 3;
        if (lum is < 35 or >= 155)
            return false;
        // Outer card margin is blue-gray (B ≥ R); lore rim is warm (R dominant).
        if (p.B > p.R + 10)
            return false;

        return p.R >= 90 && p.R >= p.G - 5;
    }

    /// <summary>
    /// Master Duel illustrations are 512×512. An existing over-frame texture is
    /// 704×1024; Cover-scaling that full card face into the art hole misaligns the
    /// lore cut and produces frame-in-frame. Re-extract the art-hole pixels as 512×512.
    /// </summary>
    public static Image<Rgba32> ExtractIllustrationSource(Image<Rgba32> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Width == Assets.OverFrameConstants.Width &&
            source.Height == Assets.OverFrameConstants.Height)
        {
            var window = Rectangle.Intersect(ArtWindow, source.Bounds);
            if (window.Width < 400 || window.Height < 400)
                window = new Rectangle(0, 0, source.Width, source.Height);

            return source.Clone(ctx =>
            {
                ctx.Crop(window);
                ctx.Resize(new ResizeOptions
                {
                    Size = new Size(512, 512),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3
                });
            });
        }

        return source.Clone();
    }

    public static bool IsOverFrameTextureSize(int width, int height) =>
        width == Assets.OverFrameConstants.Width && height == Assets.OverFrameConstants.Height;

    private static Rectangle ResolveTextBox(Image<Rgba32> frame, Rectangle artWindow)
    {
        var detected = DetectTextBox(frame, artWindow);
        if (!detected.IsEmpty)
            return detected;

        // Fallback: skip ~48px type-line strip under the art hole.
        var top = Math.Min(artWindow.Bottom + 48, Assets.OverFrameConstants.Height - 80);
        var bottom = Math.Min(Assets.OverFrameConstants.Height - 48, top + 200);
        return Rectangle.FromLTRB(artWindow.Left, top, artWindow.Right, bottom);
    }

    private static Rectangle ResolveLoreOuterBorder(
        Image<Rgba32> frame,
        Rectangle creamPanel,
        Rectangle artWindow)
    {
        var outer = DetectLoreOuterBorder(frame, creamPanel);
        if (outer.IsEmpty)
        {
            return Rectangle.FromLTRB(
                Math.Max(0, artWindow.Left - 24),
                creamPanel.Top,
                Math.Min(frame.Width, artWindow.Right + 24),
                creamPanel.Bottom);
        }

        return outer;
    }

    private static int ResolveLoreCutTop(Image<Rgba32> frame, Rectangle creamPanel, Rectangle artWindow)
    {
        var cut = DetectLoreOuterTop(frame, creamPanel, artWindow);
        // Always keep the gold top rim free of subject punch / type-line fill.
        if (creamPanel.Top - cut < 2)
            cut = Math.Max(artWindow.Bottom, creamPanel.Top - 8);

        return cut;
    }

    /// <summary>
    /// Band between art-hole bottom and the OUTER lore top, spanning the outer lore
    /// width so art meets the lore box's outer gold edge.
    /// </summary>
    private static Rectangle ResolveTypeLineStrip(
        Rectangle artWindow,
        int loreCutTop,
        Rectangle loreOuter)
    {
        if (loreCutTop <= artWindow.Bottom)
            return Rectangle.Empty;

        var left = loreOuter.Width > 0 ? loreOuter.Left : artWindow.Left;
        var right = loreOuter.Width > 0 ? loreOuter.Right : artWindow.Right;
        return Rectangle.FromLTRB(left, artWindow.Bottom, right, loreCutTop);
    }

    private static Image<Rgba32> CreateFoilMaskArtBase(
        Image<Rgba32> source,
        Rectangle artWindow,
        int scaledSourceW,
        int scaledSourceH,
        int bgX,
        int bgY)
    {
        var canvas = new Image<Rgba32>(
            Assets.OverFrameConstants.Width,
            Assets.OverFrameConstants.Height,
            new Rgba32(0, 0, 0, FoilMaskAlpha));

        FillRegionWithScaledArt(canvas, artWindow, source, scaledSourceW, scaledSourceH, bgX, bgY);
        return canvas;
    }

    /// <summary>
    /// Writes scaled source art into a rectangle at foil-mask alpha (art window or lore underlay).
    /// </summary>
    private static void FillRegionWithScaledArt(
        Image<Rgba32> canvas,
        Rectangle region,
        Image<Rgba32> source,
        int scaledSourceW,
        int scaledSourceH,
        int bgX,
        int bgY)
    {
        if (region.Width <= 0 || region.Height <= 0)
            return;

        using var scaled = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(scaledSourceW, scaledSourceH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        for (var y = 0; y < region.Height; y++)
        {
            var dy = region.Top + y;
            if ((uint)dy >= (uint)canvas.Height)
                continue;

            var sy = Math.Clamp(dy - bgY, 0, scaled.Height - 1);
            var srcRow = scaled.DangerousGetPixelRowMemory(sy).Span;
            var dstRow = canvas.DangerousGetPixelRowMemory(dy).Span;
            for (var x = 0; x < region.Width; x++)
            {
                var dx = region.Left + x;
                if ((uint)dx >= (uint)canvas.Width)
                    continue;

                var sx = Math.Clamp(dx - bgX, 0, scaled.Width - 1);
                var src = srcRow[sx];
                dstRow[dx] = new Rgba32(src.R, src.G, src.B, FoilMaskAlpha);
            }
        }
    }

    private static void BlitSubjectFoilMask(
        Image<Rgba32> canvas,
        Image<Rgba32> subject,
        int xOffset,
        int yOffset,
        bool[] occupied,
        int loreCutTop)
    {
        for (var y = 0; y < subject.Height; y++)
        {
            var srcRow = subject.DangerousGetPixelRowMemory(y).Span;
            var dy = yOffset + y;
            if ((uint)dy >= (uint)canvas.Height)
                continue;

            var dstRow = canvas.DangerousGetPixelRowMemory(dy).Span;
            for (var x = 0; x < srcRow.Length; x++)
            {
                var src = srcRow[x];
                if (src.A <= VisibleAlphaThreshold)
                    continue;

                var dx = xOffset + x;
                if ((uint)dx >= (uint)canvas.Width)
                    continue;

                // Hard-cut at the outer lore top so the gold rim stays visible.
                if (dy >= loreCutTop)
                    continue;

                dstRow[dx] = new Rgba32(src.R, src.G, src.B, FoilMaskAlpha);
                occupied[dy * canvas.Width + dx] = true;
            }
        }
    }

    private static void DrawFramePunchedByRectangleAndSilhouette(
        Image<Rgba32> canvas,
        Image<Rgba32> frame,
        Rectangle artWindow,
        Rectangle textBox,
        Rectangle typeLineStrip,
        bool[] occupied)
    {
        for (var y = 0; y < canvas.Height; y++)
        {
            var frameRow = frame.DangerousGetPixelRowMemory(y).Span;
            var dstRow = canvas.DangerousGetPixelRowMemory(y).Span;
            var rowOffset = y * canvas.Width;
            for (var x = 0; x < canvas.Width; x++)
            {
                if (artWindow.Contains(x, y))
                    continue;
                // Type-line strip keeps foil art (meets lore); no chrome gap.
                if (typeLineStrip.Contains(x, y))
                    continue;
                // Lore panel is painted in a dedicated pass — skip here.
                if (textBox.Contains(x, y))
                    continue;
                if (occupied[rowOffset + x])
                    continue;

                var fp = frameRow[x];
                if (fp.A <= VisibleAlphaThreshold)
                    continue;

                dstRow[x] = fp;
            }
        }
    }

    /// <summary>
    /// Paints the lore panel over the soft full-art underlay. Never uses the rembg
    /// cutout (those hard sleeve/panel edges caused the vertical-line glitch).
    /// </summary>
    private static void PaintLorePanel(
        Image<Rgba32> canvas,
        Image<Rgba32> frame,
        Rectangle textBox)
    {
        if (textBox.Width <= 0 || textBox.Height <= 0)
            return;

        for (var y = textBox.Top; y < textBox.Bottom && y < canvas.Height; y++)
        {
            if (y < 0) continue;
            var frameRow = frame.DangerousGetPixelRowMemory(y).Span;
            var dstRow = canvas.DangerousGetPixelRowMemory(y).Span;
            var x0 = Math.Max(0, textBox.Left);
            var x1 = Math.Min(canvas.Width, textBox.Right);
            for (var x = x0; x < x1; x++)
            {
                var fp = frameRow[x];
                if (fp.A <= VisibleAlphaThreshold)
                    continue;

                dstRow[x] = BlendFrameOverArt(dstRow[x], fp, TextBoxFrameOpacity);
            }
        }
    }

    private static Rgba32 BlendFrameOverArt(Rgba32 art, Rgba32 frame, float frameOpacity)
    {
        frameOpacity = Math.Clamp(frameOpacity, 0f, 1f);
        var artOpacity = 1f - frameOpacity;
        var r = (byte)Math.Clamp((int)MathF.Round(art.R * artOpacity + frame.R * frameOpacity), 0, 255);
        var g = (byte)Math.Clamp((int)MathF.Round(art.G * artOpacity + frame.G * frameOpacity), 0, 255);
        var b = (byte)Math.Clamp((int)MathF.Round(art.B * artOpacity + frame.B * frameOpacity), 0, 255);
        var a = frame.A > VisibleAlphaThreshold ? frame.A : (byte)255;
        return new Rgba32(r, g, b, a);
    }

    /// <summary>
    /// Makes foil-mask (A≈4) pixels opaque for on-screen preview only. The game file
    /// must keep A≈4; WPF would otherwise show those regions as nearly invisible.
    /// </summary>
    public static Image<Rgba32> FlattenFoilMaskForPreview(Image<Rgba32> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = source.Clone();
        for (var y = 0; y < copy.Height; y++)
        {
            var row = copy.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < row.Length; x++)
            {
                var p = row[x];
                if (p.A > 0 && p.A <= VisibleAlphaThreshold)
                {
                    p.A = 255;
                    row[x] = p;
                }
            }
        }

        return copy;
    }

    /// <summary>
    /// Forces the rectangular art window to fully transparent.
    /// </summary>
    public static void EnsureArtWindowHole(Image<Rgba32> frame) =>
        EnsureArtWindowHole(frame, ArtWindow);

    public static void EnsureArtWindowHole(Image<Rgba32> frame, Rectangle artWindow)
    {
        var window = ScaleRect(
            artWindow,
            Assets.OverFrameConstants.Width,
            Assets.OverFrameConstants.Height,
            frame.Width,
            frame.Height);
        for (var y = window.Top; y < window.Bottom && y < frame.Height; y++)
        {
            if (y < 0) continue;
            var pixels = frame.DangerousGetPixelRowMemory(y).Span;
            var x0 = Math.Max(0, window.Left);
            var x1 = Math.Min(frame.Width, window.Right);
            for (var x = x0; x < x1; x++)
            {
                var pixel = pixels[x];
                pixel.A = 0;
                pixels[x] = pixel;
            }
        }
    }

    private static Rectangle ScaleRect(Rectangle src, int srcW, int srcH, int dstW, int dstH)
    {
        var left = (int)MathF.Round(src.Left * (dstW / (float)srcW));
        var top = (int)MathF.Round(src.Top * (dstH / (float)srcH));
        var width = (int)MathF.Round(src.Width * (dstW / (float)srcW));
        var height = (int)MathF.Round(src.Height * (dstH / (float)srcH));
        return new Rectangle(left, top, Math.Max(1, width), Math.Max(1, height));
    }

    private static Rectangle FindVisibleBounds(Image<Rgba32> image)
    {
        var left = image.Width;
        var top = image.Height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < image.Height; y++)
        {
            var pixels = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < pixels.Length; x++)
            {
                if (pixels[x].A <= VisibleAlphaThreshold)
                    continue;

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left || bottom < top
            ? Rectangle.Empty
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
}

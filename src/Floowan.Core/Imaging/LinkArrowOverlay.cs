using Floowan.Core.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Redraws Master Duel Link arrow markers as the topmost OF layer so subject/frame
/// punches cannot erase them. Arrow art is cropped from the bundled <c>Link.png</c>
/// template (<c>card_frame18</c>) — only directions set in <see cref="LinkMarkerMask"/>
/// are composited.
/// </summary>
public static class LinkArrowOverlay
{
    /// <summary>
    /// Crop rectangles on the 704×1024 Link frame for each direction (measured from
    /// dark arrow pixels on <c>card_frame18</c> / <c>Link.png</c>, padded for AA).
    /// </summary>
    private static readonly (LinkMarkerMask Bit, Rectangle Crop)[] ArrowCrops =
    [
        (LinkMarkerMask.UpLeft, new Rectangle(56, 157, 93, 97)),
        (LinkMarkerMask.Up, new Rectangle(296, 147, 113, 47)),
        (LinkMarkerMask.UpRight, new Rectangle(551, 156, 100, 98)),
        (LinkMarkerMask.Left, new Rectangle(45, 396, 46, 118)),
        (LinkMarkerMask.Right, new Rectangle(613, 396, 48, 118)),
        (LinkMarkerMask.DownLeft, new Rectangle(41, 656, 108, 107)),
        (LinkMarkerMask.Down, new Rectangle(296, 715, 113, 48)),
        (LinkMarkerMask.DownRight, new Rectangle(551, 656, 108, 107)),
    ];

    /// <summary>
    /// True when OF compose should redraw Link arrows for this frame style.
    /// Floowan has no separate Link Pendulum template today — only <see cref="CardFrameStyle.Link"/>.
    /// </summary>
    public static bool NeedsArrowOverlay(CardFrameStyle frameStyle) =>
        frameStyle == CardFrameStyle.Link;

    /// <summary>
    /// Blits active arrow sprites from the Link frame template onto
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
            BlitArrowRegion(canvas, linkTemplate, crop);
        }
    }

    /// <summary>
    /// Copies arrow-looking pixels from <paramref name="src"/> crop onto
    /// <paramref name="dst"/> at the same absolute coordinates (topmost layer).
    /// Keeps near-black arrow body plus mid-tone AA; skips bright frame chrome.
    /// </summary>
    private static void BlitArrowRegion(Image<Rgba32> dst, Image<Rgba32> src, Rectangle crop)
    {
        var x0 = Math.Clamp(crop.X, 0, src.Width - 1);
        var y0 = Math.Clamp(crop.Y, 0, src.Height - 1);
        var x1 = Math.Clamp(crop.Right, 0, src.Width);
        var y1 = Math.Clamp(crop.Bottom, 0, src.Height);

        for (var y = y0; y < y1; y++)
        {
            var srcRow = src.DangerousGetPixelRowMemory(y).Span;
            var dstRow = dst.DangerousGetPixelRowMemory(y).Span;
            for (var x = x0; x < x1; x++)
            {
                var c = srcRow[x];
                if (!IsArrowPixel(c))
                    continue;
                dstRow[x] = AlphaOver(dstRow[x], c);
            }
        }
    }

    private static bool IsArrowPixel(Rgba32 c)
    {
        if (c.A < 20)
            return false;
        // Dark arrow body + soft grey AA fringe on the Link face.
        var lum = (c.R + c.G + c.B) / 3;
        return lum <= 120;
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

using Floowan.Core.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Redraws Master Duel Link arrow markers as the topmost OF layer so subject/frame
/// punches cannot erase them. Arrow silhouettes are cropped from the bundled
/// <c>Link.png</c> template (<c>card_frame18</c>); directions set in
/// <see cref="LinkMarkerMask"/> are recolored to lit orange/red so they read as
/// active against the dark inactive markers left on the frame.
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

    // Master Duel–style lit marker: deep red-orange body → brighter amber AA fringe.
    private const byte ActiveBodyR = 228;
    private const byte ActiveBodyG = 52;
    private const byte ActiveBodyB = 18;
    private const byte ActiveFringeR = 255;
    private const byte ActiveFringeG = 168;
    private const byte ActiveFringeB = 64;

    /// <summary>
    /// True when OF compose should redraw Link arrows for this frame style.
    /// Floowan has no separate Link Pendulum template today — only <see cref="CardFrameStyle.Link"/>.
    /// </summary>
    public static bool NeedsArrowOverlay(CardFrameStyle frameStyle) =>
        frameStyle == CardFrameStyle.Link;

    /// <summary>
    /// Blits lit (orange/red) active arrow sprites from the Link frame template onto
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
            BlitLitArrowRegion(canvas, linkTemplate, crop);
        }
    }

    /// <summary>
    /// Copies arrow-looking pixels from <paramref name="src"/> crop onto
    /// <paramref name="dst"/> at the same absolute coordinates (topmost layer),
    /// recolored to lit orange/red. Skips bright frame chrome.
    /// </summary>
    private static void BlitLitArrowRegion(Image<Rgba32> dst, Image<Rgba32> src, Rectangle crop)
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
                dstRow[x] = AlphaOver(dstRow[x], ToLitArrowPixel(c));
            }
        }
    }

    /// <summary>
    /// Maps a dark inactive arrow sample from <c>Link.png</c> onto a lit orange/red
    /// pixel. Lower luminance (solid body) → deep red-orange; higher (AA fringe) →
    /// brighter amber so the silhouette stays crisp.
    /// </summary>
    public static Rgba32 ToLitArrowPixel(Rgba32 dark)
    {
        var lum = (dark.R + dark.G + dark.B) / 3f;
        var t = Math.Clamp(lum / 120f, 0f, 1f);
        var r = (byte)Math.Clamp(MathF.Round(Lerp(ActiveBodyR, ActiveFringeR, t)), 0, 255);
        var g = (byte)Math.Clamp(MathF.Round(Lerp(ActiveBodyG, ActiveFringeG, t)), 0, 255);
        var b = (byte)Math.Clamp(MathF.Round(Lerp(ActiveBodyB, ActiveFringeB, t)), 0, 255);
        return new Rgba32(r, g, b, dark.A);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

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

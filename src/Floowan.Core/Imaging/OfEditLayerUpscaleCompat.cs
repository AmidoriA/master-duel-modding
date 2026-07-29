using Floowan.Core.Backup;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Compatibility for reopening Custom OF edit stages (<c>of_edit_layer</c>) that were
/// composed/stored at 512-class illustration sizes before Real-ESRGAN ×2 became the
/// default OF path. Bitmaps are upscaled; Cover-relative scales and 704×1024 canvas
/// offsets stay numerically the same so layout matches.
/// </summary>
public static class OfEditLayerUpscaleCompat
{
    /// <summary>
    /// True when any persisted layer is still 512-class (or near-512) and Real-ESRGAN
    /// OF upscale is enabled.
    /// </summary>
    public static bool NeedsLayerUpscale(
        Image<Rgba32>? subject,
        Image<L8>? mask,
        Image<Rgba32>? background)
    {
        if (subject is not null &&
            ArtUpscaleService.NeedsOverFrameArtUpscale(subject.Width, subject.Height))
            return true;

        if (mask is not null &&
            ArtUpscaleService.NeedsOverFrameArtUpscale(mask.Width, mask.Height))
            return true;

        if (background is not null &&
            ArtUpscaleService.NeedsOverFrameArtUpscale(background.Width, background.Height))
            return true;

        return false;
    }

    /// <summary>
    /// Bitmap spatial factor for a 512-class layer (usually <see cref="ArtUpscaleService.TargetScale"/>),
    /// or 1 when upscale does not apply.
    /// </summary>
    public static int ResolveSpatialUpscaleFactor(int width, int height)
    {
        if (!ArtUpscaleService.NeedsOverFrameArtUpscale(width, height))
            return 1;

        var (tw, th) = ArtUpscaleService.GetOverFrameUpscaleTargetSize(width, height);
        if (width <= 0 || height <= 0)
            return ArtUpscaleService.TargetScale;

        var fx = tw / (double)width;
        var fy = th / (double)height;
        // Uniform ×2 for MD sizes; round to nearest int for the rare odd encode.
        return Math.Max(1, (int)Math.Round(Math.Min(fx, fy)));
    }

    /// <summary>
    /// Cover-fit pixel size of an illustration into <paramref name="artWindow"/> after
    /// applying Custom OF scale. Used to prove 512→1024 bitmap upscale keeps the same
    /// hole footprint when transform multipliers stay unchanged.
    /// </summary>
    public static (int ScaledW, int ScaledH) CoverFitScaledSize(
        int sourceWidth,
        int sourceHeight,
        Rectangle artWindow,
        float scaleMultiplier)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || artWindow.Width <= 0 || artWindow.Height <= 0)
            return (0, 0);

        scaleMultiplier = OverFrameAutoArtComposer.ClampSubjectScale(scaleMultiplier);
        var cover = Math.Max(
            artWindow.Width / (float)sourceWidth,
            artWindow.Height / (float)sourceHeight);
        var scale = cover * scaleMultiplier;
        return (
            Math.Max(1, (int)MathF.Round(sourceWidth * scale)),
            Math.Max(1, (int)MathF.Round(sourceHeight * scale)));
    }

    /// <summary>
    /// After layer bitmaps are upscaled by <paramref name="spatialFactor"/> (e.g. 2 for
    /// 512→1024), adjust persisted stage transforms for visual continuity.
    /// <para>
    /// Custom OF stores <see cref="CustomOverframeStageState.SubjectScale"/> /
    /// <see cref="CustomOverframeStageState.BackgroundScale"/> as Cover-relative
    /// multipliers (×1 = object-fit cover into the art hole) and offsets in 704×1024
    /// canvas pixels. Doubling bitmap dimensions halves the Cover factor, so the same
    /// multipliers and canvas offsets keep the composed layout — no numeric change.
    /// </para>
    /// </summary>
    /// <returns>True when a compat pass ran (spatial factor &gt; 1).</returns>
    public static bool AdjustTransformsAfterLayerUpscale(
        CustomOverframeStageState state,
        int spatialFactor)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (spatialFactor <= 1)
            return false;

        // Cover-relative scales + canvas-space pans/offsets: intentionally unchanged.
        // Callers must still invoke this after bitmap upscale so the storage contract
        // stays explicit (source-pixel offsets would need ×spatialFactor instead).
        return true;
    }

    /// <summary>
    /// Snapshot of transform fields after a spatial upscale pass (for tests / callers
    /// that assert layout continuity).
    /// </summary>
    public static (
        float SubjectScale,
        int SubjectOffsetX,
        int SubjectOffsetY,
        float BackgroundScale,
        int BackgroundOffsetX,
        int BackgroundOffsetY) CaptureTransforms(CustomOverframeStageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return (
            state.SubjectScale,
            state.SubjectOffsetX,
            state.SubjectOffsetY,
            state.BackgroundScale,
            state.BackgroundOffsetX,
            state.BackgroundOffsetY);
    }

    /// <summary>
    /// Resolves the reference illustration size used to pick the spatial upscale factor
    /// (prefer subject, then mask, then background).
    /// </summary>
    public static bool TryGetReferenceLayerSize(
        Image<Rgba32>? subject,
        Image<L8>? mask,
        Image<Rgba32>? background,
        out int width,
        out int height)
    {
        if (subject is not null)
        {
            width = subject.Width;
            height = subject.Height;
            return true;
        }

        if (mask is not null)
        {
            width = mask.Width;
            height = mask.Height;
            return true;
        }

        if (background is not null)
        {
            width = background.Width;
            height = background.Height;
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }
}

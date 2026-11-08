namespace Floowan.Core.Imaging;

/// <summary>
/// Which model finds the subject (the card's character or monster) for Auto OF and
/// Custom OF "Auto detect". Both produce an L8 mask that the over-frame composer
/// hard-cuts at <see cref="OverFrameAutoArtComposer.MaskKeepThreshold"/>.
/// </summary>
public enum SubjectCutoutModel
{
    /// <summary>rembg isnet-anime: salient-object matting, CPU, ~170 MB, under a second.</summary>
    IsnetAnime = 0,

    /// <summary>
    /// SAM 3 text prompts ("character", "monster", …) pick the subject, ViTMatte places the
    /// edge. DirectML GPU when available (CPU fallback), ~1.1 GB download.
    /// </summary>
    Sam3 = 1
}

public static class SubjectCutoutModels
{
    /// <summary>Stored in <c>user.app_config.cutout_model</c>.</summary>
    public const string IsnetAnimeId = "isnet-anime";

    /// <summary>Stored in <c>user.app_config.cutout_model</c>.</summary>
    public const string Sam3Id = "sam3-vitmatte";

    public static string ToId(SubjectCutoutModel model) =>
        model == SubjectCutoutModel.Sam3 ? Sam3Id : IsnetAnimeId;

    /// <summary>Unknown or empty ids fall back to <see cref="SubjectCutoutModel.IsnetAnime"/>.</summary>
    public static SubjectCutoutModel FromId(string? id) =>
        string.Equals(id?.Trim(), Sam3Id, StringComparison.OrdinalIgnoreCase)
            ? SubjectCutoutModel.Sam3
            : SubjectCutoutModel.IsnetAnime;

    /// <summary>Short name for progress and status text.</summary>
    public static string DisplayName(SubjectCutoutModel model) =>
        model == SubjectCutoutModel.Sam3 ? "SAM 3 + ViTMatte" : "isnet-anime";
}

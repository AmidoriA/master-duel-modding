namespace Floowan.Core.Spine;

/// <summary>
/// Builds Spine atlas + JSON (+ texture path) for Master Duel cut-in replacement.
/// v1 implements single-image bob; image-sequence attachment switching is reserved.
/// </summary>
public interface ISpineCutInGenerator
{
    SpineCutInAssets FromSingleImage(
        string imagePath,
        string assetBaseName,
        SpineCutInOptions? options = null,
        string? textureName = null,
        string? skeletonName = null,
        string? atlasName = null);

    /// <summary>
    /// Reserved for a C# port of MattOstgard spine_sequence (attachment switching).
    /// </summary>
    SpineCutInAssets FromImageSequence(
        IReadOnlyList<string> imagePaths,
        string assetBaseName,
        SpineCutInOptions? options = null,
        string? textureName = null,
        string? skeletonName = null,
        string? atlasName = null);
}

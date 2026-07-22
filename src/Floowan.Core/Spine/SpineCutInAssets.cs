namespace Floowan.Core.Spine;

/// <summary>Generated Spine cut-in package ready to inject into Master Duel bundles.</summary>
public sealed record SpineCutInAssets
{
    /// <summary>Base name without extension, e.g. <c>P14944</c>.</summary>
    public required string AssetBaseName { get; init; }

    /// <summary>Texture2D / atlas page name, typically <c>P14944</c> (no extension in Unity).</summary>
    public required string TextureName { get; init; }

    /// <summary>Skeleton TextAsset name, typically <c>P14944JS</c>.</summary>
    public required string SkeletonName { get; init; }

    /// <summary>Atlas TextAsset name when it differs from the texture name; often same as texture.</summary>
    public required string AtlasName { get; init; }

    public required string AtlasText { get; init; }
    public required string SkeletonJson { get; init; }

    /// <summary>Path to the PNG used as the single atlas page.</summary>
    public required string TexturePngPath { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
}

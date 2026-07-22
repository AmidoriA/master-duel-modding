namespace Floowan.Core.Spine;

public enum CutInAssetKind
{
    Texture,
    Atlas,
    Skeleton
}

public sealed class CutInAssetHit
{
    public required int CutInId { get; init; }
    public required CutInAssetKind Kind { get; init; }
    public required string AssetName { get; init; }
    public required string BundlePath { get; init; }
    public long PathId { get; init; }
    public string? ContainerPath { get; init; }
    public bool PreferHighEnd { get; init; }
}

public sealed class CutInAssetSet
{
    public required int CutInId { get; init; }
    public CutInAssetHit? Texture { get; init; }
    public CutInAssetHit? Atlas { get; init; }
    public CutInAssetHit? Skeleton { get; init; }

    public bool IsComplete => Texture is not null && Atlas is not null && Skeleton is not null;

    public IEnumerable<CutInAssetHit> All
    {
        get
        {
            if (Texture is not null) yield return Texture;
            if (Atlas is not null) yield return Atlas;
            if (Skeleton is not null) yield return Skeleton;
        }
    }
}

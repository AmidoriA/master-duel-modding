namespace Floowan.Core.Models;

/// <summary>One Master Duel summon cut-in discovered under LocalData.</summary>
public sealed class CutInRecord
{
    public int CutInId { get; init; }
    public string? Name { get; init; }
    public bool IsComplete { get; init; }
    public string? TextureName { get; init; }
    public string? TextureBundlePath { get; init; }
    public string? AtlasName { get; init; }
    public string? AtlasBundlePath { get; init; }
    public string? SkeletonName { get; init; }
    public string? SkeletonBundlePath { get; init; }
    public string UpdatedAtUtc { get; init; } = "";
}

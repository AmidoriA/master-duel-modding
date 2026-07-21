namespace Floowan.Core.Game;

/// <summary>
/// Resolves Master Duel AssetBundle paths for a card hash/bundle id.
/// </summary>
public static class BundlePathResolver
{
    public static string GetLocalDataBundlePath(string playerDataPath, string bundleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
        if (bundleId.Length < 2)
            throw new ArgumentException("Bundle id must be at least 2 characters.", nameof(bundleId));

        return Path.Combine(playerDataPath, "0000", bundleId[..2], bundleId);
    }

    public static string GetStreamingAssetsBundlePath(string playerDataPath, string bundleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
        if (bundleId.Length < 2)
            throw new ArgumentException("Bundle id must be at least 2 characters.", nameof(bundleId));

        var installRoot = GamePathLocator.ResolveInstallRoot(playerDataPath);
        return Path.Combine(
            installRoot,
            "masterduel_Data",
            "StreamingAssets",
            "AssetBundle",
            bundleId[..2],
            bundleId);
    }

    public static string ResolveExistingBundlePath(string playerDataPath, string bundleId)
    {
        var local = GetLocalDataBundlePath(playerDataPath, bundleId);
        if (File.Exists(local))
            return local;

        var streaming = GetStreamingAssetsBundlePath(playerDataPath, bundleId);
        if (File.Exists(streaming))
            return streaming;

        throw new FileNotFoundException(
            $"Could not find asset bundle '{bundleId}' under LocalData/0000 or StreamingAssets/AssetBundle.",
            local);
    }
}

namespace Floowan.Core.Game;

/// <summary>
/// Resolves Master Duel AssetBundle paths for a card hash/bundle id.
/// Install layout (from a LocalData/&lt;playerId&gt; path):
/// <list type="bullet">
/// <item><description><c>{player}/0000/{id[0..2]}/{id}</c> — LocalData bundles (player downloads / patches)</description></item>
/// <item><description><c>{install}/masterduel_Data/StreamingAssets/AssetBundle/{id[0..2]}/{id}</c> — shipped/base AssetBundles</description></item>
/// </list>
/// Resolution prefers LocalData when both exist.
/// </summary>
public static class BundlePathResolver
{
    public static string GetLocalDataRoot(string playerDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerDataPath);
        return Path.Combine(playerDataPath, "0000");
    }

    /// <summary>
    /// <c>{installRoot}/masterduel_Data/StreamingAssets/AssetBundle</c>, resolved via
    /// <see cref="GamePathLocator.ResolveInstallRoot"/> (handles the Master Duel folder name,
    /// including the double space in <c>Yu-Gi-Oh!  Master Duel</c>).
    /// </summary>
    public static string GetStreamingAssetsRoot(string playerDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerDataPath);
        var installRoot = GamePathLocator.ResolveInstallRoot(playerDataPath);
        return Path.Combine(installRoot, "masterduel_Data", "StreamingAssets", "AssetBundle");
    }

    public static string GetLocalDataBundlePath(string playerDataPath, string bundleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
        if (bundleId.Length < 2)
            throw new ArgumentException("Bundle id must be at least 2 characters.", nameof(bundleId));

        return Path.Combine(GetLocalDataRoot(playerDataPath), bundleId[..2], bundleId);
    }

    public static string GetStreamingAssetsBundlePath(string playerDataPath, string bundleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
        if (bundleId.Length < 2)
            throw new ArgumentException("Bundle id must be at least 2 characters.", nameof(bundleId));

        return Path.Combine(GetStreamingAssetsRoot(playerDataPath), bundleId[..2], bundleId);
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

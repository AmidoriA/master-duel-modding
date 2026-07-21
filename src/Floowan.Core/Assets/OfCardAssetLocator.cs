using Floowan.Core.Data;
using Floowan.Core.Game;

namespace Floowan.Core.Assets;

/// <summary>
/// Locates the AssetBundle that contains the <c>of_card_asset</c> TextAsset.
/// Prefers a cached id from <see cref="CardDatabase"/> / app_config; otherwise
/// performs a one-time LocalData (+ StreamingAssets) scan.
/// </summary>
public sealed class OfCardAssetLocator
{
    private readonly TextAssetBundleService _textAssets;

    public OfCardAssetLocator(string? classDataPath = null)
    {
        _textAssets = new TextAssetBundleService(classDataPath);
    }

    public OfCardAssetLocator(TextAssetBundleService textAssets)
    {
        _textAssets = textAssets;
    }

    public string? GetCachedBundleId(CardDatabase database) =>
        database.GetOfCardAssetBundleId();

    public void CacheBundleId(CardDatabase database, string bundleId) =>
        database.SetOfCardAssetBundleId(bundleId);

    /// <summary>
    /// Resolve the on-disk path to the of_card_asset bundle, scanning once if needed.
    /// </summary>
    public OfCardAssetLocateResult Locate(
        string playerDataPath,
        CardDatabase? database = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return OfCardAssetLocateResult.Fail(pathError ?? "Invalid game path.");

        var cached = database?.GetOfCardAssetBundleId();
        if (!string.IsNullOrWhiteSpace(cached))
        {
            try
            {
                var path = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, cached);
                if (_textAssets.TryFindTextAsset(path, TextAssetBundleService.OfCardAssetName, out _))
                    return OfCardAssetLocateResult.Ok(cached, path, scanned: false);
            }
            catch (FileNotFoundException)
            {
                // stale cache — rescan
            }
            catch (InvalidOperationException)
            {
                // stale cache — rescan
            }
        }

        progress?.Report("Scanning LocalData for of_card_asset…");
        var found = ScanForOfCardAsset(playerDataPath, progress, cancellationToken);
        if (found is null)
            return OfCardAssetLocateResult.Fail(
                "Could not find TextAsset 'of_card_asset' under LocalData/0000 or StreamingAssets.");

        database?.SetOfCardAssetBundleId(found.Value.BundleId);
        return OfCardAssetLocateResult.Ok(found.Value.BundleId, found.Value.Path, scanned: true);
    }

    public (string BundleId, string Path)? ScanForOfCardAsset(
        string playerDataPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var roots = new List<string>();
        var localRoot = Path.Combine(playerDataPath, "0000");
        if (Directory.Exists(localRoot))
            roots.Add(localRoot);

        try
        {
            var install = GamePathLocator.ResolveInstallRoot(playerDataPath);
            var streaming = Path.Combine(install, "masterduel_Data", "StreamingAssets", "AssetBundle");
            if (Directory.Exists(streaming))
                roots.Add(streaming);
        }
        catch
        {
            // ignore install resolve failures
        }

        // Prefer smaller files: of_card_asset gate bundles are tiny.
        var candidates = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Select(path =>
            {
                try { return (Path: path, Length: new FileInfo(path).Length); }
                catch { return (Path: path, Length: long.MaxValue); }
            })
            .OrderBy(x => x.Length)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var checkedCount = 0;
        foreach (var (path, _) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedCount++;
            if (checkedCount % 200 == 0)
                progress?.Report($"Scanning bundles… checked {checkedCount}/{candidates.Count}");

            if (!_textAssets.BundleContainsNamedTextAsset(path, TextAssetBundleService.OfCardAssetName))
                continue;

            var bundleId = Path.GetFileName(path);
            return (bundleId, path);
        }

        return null;
    }
}

public sealed class OfCardAssetLocateResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? BundleId { get; init; }
    public string? BundlePath { get; init; }
    public bool Scanned { get; init; }

    public static OfCardAssetLocateResult Ok(string bundleId, string bundlePath, bool scanned) =>
        new()
        {
            Success = true,
            Message = scanned
                ? $"Found of_card_asset in bundle {bundleId} (scan complete)."
                : $"Using cached of_card_asset bundle {bundleId}.",
            BundleId = bundleId,
            BundlePath = bundlePath,
            Scanned = scanned
        };

    public static OfCardAssetLocateResult Fail(string message) =>
        new() { Success = false, Message = message };
}

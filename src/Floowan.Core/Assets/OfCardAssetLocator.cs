using System.Text.RegularExpressions;
using Floowan.Core.Data;
using Floowan.Core.Game;

namespace Floowan.Core.Assets;

/// <summary>
/// Locates the AssetBundle that contains the <c>of_card_asset</c> TextAsset.
/// Prefers session / DB cache / the known live default id; a full LocalData scan runs
/// only when the caller sets <c>allowFullScan</c> (UI must confirm first).
/// </summary>
public sealed class OfCardAssetLocator
{
    /// <summary>
    /// Current post-patch live gate bundle id (replaced legacy <c>a589d3b5</c>).
    /// Used when user.db has no verified cache — not a full filesystem scan.
    /// </summary>
    public const string DefaultBundleId = "22817d01";

    /// <summary>
    /// Live Master Duel AssetBundle file names are 8 lowercase hex digits with no extension.
    /// Skips leftovers such as <c>*.gatebak</c> and other non-live files that would win a
    /// size-ordered scan or force expensive Open/Find failures.
    /// </summary>
    private static readonly Regex LiveBundleFileName = new(
        @"^[0-9a-f]{8}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly TextAssetBundleService _textAssets;
    private string? _sessionBundleId;
    private string? _sessionBundlePath;

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

    /// <summary>True when <paramref name="fileName"/> is a live MD bundle id (8 hex, no extension).</summary>
    public static bool IsLiveBundleFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && LiveBundleFileName.IsMatch(fileName);

    /// <summary>
    /// Resolve the on-disk path to the of_card_asset bundle.
    /// Tries session → DB cache → <see cref="DefaultBundleId"/>. Full directory scan
    /// only when <paramref name="allowFullScan"/> is true (caller must have user consent).
    /// </summary>
    public OfCardAssetLocateResult Locate(
        string playerDataPath,
        CardDatabase? database = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowFullScan = false)
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return OfCardAssetLocateResult.Fail(pathError ?? "Invalid game path.");

        if (TryUseSessionCache(playerDataPath, out var sessionHit))
            return sessionHit;

        var cached = database?.GetOfCardAssetBundleId();
        if (!string.IsNullOrWhiteSpace(cached) && IsLiveBundleFileName(cached))
        {
            if (TryBindKnownBundle(playerDataPath, cached, database, persist: false, out var cachedHit))
                return cachedHit;

            // Stale cache (e.g. pre-patch a589d3b5) — clear so we don't keep retrying it.
            try { database?.SetOfCardAssetBundleId(null); } catch { /* read-only ok */ }
            ClearSession();
        }
        else if (!string.IsNullOrWhiteSpace(cached))
        {
            try { database?.SetOfCardAssetBundleId(null); } catch { /* read-only ok */ }
            ClearSession();
        }

        // Known live default — prefer over a full LocalData walk.
        if (!string.Equals(cached, DefaultBundleId, StringComparison.OrdinalIgnoreCase) &&
            TryBindKnownBundle(playerDataPath, DefaultBundleId, database, persist: true, out var defaultHit))
        {
            return defaultHit;
        }

        if (!allowFullScan)
        {
            return OfCardAssetLocateResult.Fail(
                $"Could not open of_card_asset at cached id or default '{DefaultBundleId}'. " +
                "Use Tools → Scan / locate of_card_asset (requires confirmation) to search LocalData.");
        }

        progress?.Report("Scanning LocalData for of_card_asset…");
        var found = ScanForOfCardAsset(playerDataPath, progress, cancellationToken);
        if (found is null)
            return OfCardAssetLocateResult.Fail(
                "Could not find TextAsset 'of_card_asset' under LocalData/0000 or StreamingAssets.");

        RememberSession(found.Value.BundleId, found.Value.Path);
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
        // Only live 8-hex bundle names — skip *.gatebak and other leftovers.
        var candidates = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => IsLiveBundleFileName(Path.GetFileName(path)))
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

    private bool TryBindKnownBundle(
        string playerDataPath,
        string bundleId,
        CardDatabase? database,
        bool persist,
        out OfCardAssetLocateResult result)
    {
        result = OfCardAssetLocateResult.Fail("");
        try
        {
            var path = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, bundleId);
            if (!_textAssets.TryFindTextAsset(path, TextAssetBundleService.OfCardAssetName, out _))
                return false;

            RememberSession(bundleId, path);
            if (persist)
            {
                try { database?.SetOfCardAssetBundleId(bundleId); } catch { /* read-only ok */ }
            }

            result = OfCardAssetLocateResult.Ok(bundleId, path, scanned: false);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private bool TryUseSessionCache(string playerDataPath, out OfCardAssetLocateResult result)
    {
        result = OfCardAssetLocateResult.Fail("");
        if (string.IsNullOrWhiteSpace(_sessionBundleId) || string.IsNullOrWhiteSpace(_sessionBundlePath))
            return false;

        try
        {
            if (!File.Exists(_sessionBundlePath))
            {
                ClearSession();
                return false;
            }

            string path;
            try
            {
                path = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, _sessionBundleId);
            }
            catch (FileNotFoundException)
            {
                ClearSession();
                return false;
            }

            if (!string.Equals(path, _sessionBundlePath, StringComparison.OrdinalIgnoreCase))
                _sessionBundlePath = path;

            if (!_textAssets.TryFindTextAsset(path, TextAssetBundleService.OfCardAssetName, out _))
            {
                ClearSession();
                return false;
            }

            result = OfCardAssetLocateResult.Ok(_sessionBundleId, path, scanned: false);
            return true;
        }
        catch
        {
            ClearSession();
            return false;
        }
    }

    private void RememberSession(string bundleId, string path)
    {
        _sessionBundleId = bundleId;
        _sessionBundlePath = path;
    }

    private void ClearSession()
    {
        _sessionBundleId = null;
        _sessionBundlePath = null;
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
                : $"Using of_card_asset bundle {bundleId}.",
            BundleId = bundleId,
            BundlePath = bundlePath,
            Scanned = scanned
        };

    public static OfCardAssetLocateResult Fail(string message) =>
        new() { Success = false, Message = message };
}

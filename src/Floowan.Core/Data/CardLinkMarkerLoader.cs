using System.Collections.Concurrent;
using AssetsTools.NET.Extra;
using Floowan.Core.Assets;
using Floowan.Core.Game;

namespace Floowan.Core.Data;

/// <summary>
/// Loads card-id → <see cref="LinkMarkerMask"/> from Master Duel <c>CARD_Prop</c>.
/// Prefers LocalData AssetBundles, then <c>data.unity3d</c>, then StreamingAssets.
/// Scans run in parallel with early exit; callers should pass a timeout token so Preview
/// never blocks indefinitely.
/// </summary>
public sealed class CardLinkMarkerLoader
{
    private const long MinCardDataBytes = 64;
    private const long MaxCardDataBytes = 5_000_000;

    private readonly string _classDataPath;
    private readonly object _cacheGate = new();
    private IReadOnlyDictionary<int, LinkMarkerMask>? _cache;
    private string? _cachePlayerPath;

    public CardLinkMarkerLoader(string? classDataPath = null)
    {
        _classDataPath = ClassDataLocator.FindClassDataPath(classDataPath);
    }

    /// <summary>Clears the in-memory marker map (e.g. after a Tools catalog rebuild).</summary>
    public void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cache = null;
            _cachePlayerPath = null;
        }
    }

    public bool TryGetMarkers(
        string playerDataPath,
        int cardId,
        out LinkMarkerMask markers,
        CancellationToken cancellationToken = default)
    {
        markers = LinkMarkerMask.None;
        var map = GetOrLoadMap(playerDataPath, progress: null, cancellationToken);
        return map.TryGetValue(cardId, out markers);
    }

    public IReadOnlyDictionary<int, LinkMarkerMask> GetOrLoadMap(
        string playerDataPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        lock (_cacheGate)
        {
            if (_cache is not null
                && string.Equals(_cachePlayerPath, playerDataPath, StringComparison.OrdinalIgnoreCase))
            {
                return _cache;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var map = LoadMap(playerDataPath, progress, cancellationToken);
        lock (_cacheGate)
        {
            // Another thread may have won the race; prefer the first successful cache.
            if (_cache is not null
                && string.Equals(_cachePlayerPath, playerDataPath, StringComparison.OrdinalIgnoreCase))
            {
                return _cache;
            }

            _cache = map;
            _cachePlayerPath = playerDataPath;
            return _cache;
        }
    }

    public IReadOnlyDictionary<int, LinkMarkerMask> LoadMap(
        string playerDataPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Loading CARD_Prop link markers…");
        var prop = TryLoadEncryptedProp(playerDataPath, progress, cancellationToken);
        if (prop is null)
        {
            throw new InvalidOperationException(
                "Could not find CARD_Prop under LocalData/StreamingAssets or data.unity3d.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (encryptedProp, encryptedIndx) = prop.Value;
        var key = CardDataCrypto.FindCryptoKey(encryptedIndx);
        var decProp = CardDataCrypto.Decrypt(encryptedProp, key);
        var map = CardPropTypeDecoder.ParseLinkMarkerMap(decProp);
        progress?.Report($"Loaded link markers for {map.Count} Link cards.");
        return map;
    }

    private (byte[] Prop, byte[] Indx)? TryLoadEncryptedProp(
        string playerDataPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // 1) LocalData only — CARD_* TextAssets live here for patched clients; avoid StreamingAssets.
        try
        {
            var localRoot = BundlePathResolver.GetLocalDataRoot(playerDataPath);
            var fromLocal = TryLoadPropFromRoots(
                [localRoot], "LocalData", progress, cancellationToken);
            if (fromLocal is not null)
                return fromLocal;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // try unity3d
        }

        // 2) data.unity3d — single file, usually has CARD_Prop; do this before StreamingAssets.
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unityPath = GamePathLocator.ResolveUnity3dPath(playerDataPath);
            if (File.Exists(unityPath))
            {
                var fromUnity = TryLoadPropFromUnity3d(unityPath, progress, cancellationToken);
                if (fromUnity is not null)
                    return fromUnity;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // try StreamingAssets
        }

        // 3) StreamingAssets last — can be huge; only if LocalData + unity3d missed.
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var streaming = BundlePathResolver.GetStreamingAssetsRoot(playerDataPath);
            return TryLoadPropFromRoots(
                [streaming], "StreamingAssets", progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private (byte[] Prop, byte[] Indx)? TryLoadPropFromRoots(
        IEnumerable<string> roots,
        string label,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var existingRoots = roots.Where(Directory.Exists).ToList();
        if (existingRoots.Count == 0)
            return null;

        var files = existingRoots
            .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .ToList();
        if (files.Count == 0)
            return null;

        progress?.Report($"Scanning {label} for CARD_Prop (markers)… 0/{files.Count}");

        var prop = new ConcurrentBag<byte[]>();
        var indx = new ConcurrentBag<byte[]>();
        var checkedCount = 0;

        Parallel.ForEach(
            files,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            },
            () => CreateAssetsManager(),
            (path, _, am) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (prop.Value is not null && indx.Value is not null)
                    return am;

                var n = Interlocked.Increment(ref checkedCount);
                if (n % 400 == 0)
                    progress?.Report($"Scanning {label} for CARD_Prop (markers)… {n}/{files.Count}");

                long length;
                try { length = new FileInfo(path).Length; }
                catch { return am; }
                if (length is < MinCardDataBytes or > MaxCardDataBytes)
                    return am;

                try
                {
                    TryCollectPropIndx(am, path, prop, indx);
                }
                catch
                {
                    // skip unloadable / non-bundle
                }
                finally
                {
                    am.UnloadAll(unloadClassData: false);
                }

                return am;
            },
            am => am.UnloadAll());

        cancellationToken.ThrowIfCancellationRequested();
        return prop.Value is not null && indx.Value is not null
            ? (prop.Value, indx.Value)
            : null;
    }

    private void TryCollectPropIndx(
        AssetsManager am,
        string path,
        ConcurrentBag<byte[]> prop,
        ConcurrentBag<byte[]> indx)
    {
        if (prop.Value is not null && indx.Value is not null)
            return;

        var bi = am.LoadBundleFile(path, unpackIfPacked: true);
        if (bi?.file is null)
            return;

        for (var i = 0; i < bi.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            if (prop.Value is not null && indx.Value is not null)
                return;
            if (!bi.file.IsAssetsFile(i))
                continue;

            var ai = am.LoadAssetsFileFromBundle(bi, i, false);
            if (ai?.file is null)
                continue;

            am.LoadClassDatabaseFromPackage(ai.file.Metadata.UnityVersion);
            foreach (var info in ai.file.GetAssetsOfType(AssetClassID.TextAsset))
            {
                if (prop.Value is not null && indx.Value is not null)
                    return;

                try
                {
                    var bf = am.GetBaseField(ai, info);
                    var name = (bf["m_Name"].AsString ?? "").Trim().ToLowerInvariant();
                    if (name.EndsWith(".bytes", StringComparison.Ordinal))
                        name = name[..^6];

                    byte[]? payload = null;
                    try { payload = bf["m_Script"].AsByteArray; } catch { continue; }
                    if (payload is null || payload.Length == 0)
                        continue;

                    if (name == "card_prop")
                        prop.TrySet(payload);
                    else if (name is "card_indx" or "card_index")
                        indx.TrySet(payload);
                }
                catch
                {
                    // skip bad assets
                }
            }
        }
    }

    private (byte[] Prop, byte[] Indx)? TryLoadPropFromUnity3d(
        string unityPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Reading CARD_Prop from data.unity3d…");
        var am = CreateAssetsManager();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bi = am.LoadBundleFile(unityPath, unpackIfPacked: true);
            if (bi?.file is null)
                return null;

            byte[]? prop = null, indx = null;
            for (var i = 0; i < bi.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!bi.file.IsAssetsFile(i))
                    continue;

                var ai = am.LoadAssetsFileFromBundle(bi, i, false);
                if (ai?.file is null)
                    continue;

                am.LoadClassDatabaseFromPackage(ai.file.Metadata.UnityVersion);
                foreach (var info in ai.file.GetAssetsOfType(AssetClassID.TextAsset))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var bf = am.GetBaseField(ai, info);
                        var name = (bf["m_Name"].AsString ?? "").Trim();
                        byte[]? payload = null;
                        try { payload = bf["m_Script"].AsByteArray; } catch { continue; }
                        if (payload is null || payload.Length == 0)
                            continue;

                        if (name.Equals("CARD_Prop", StringComparison.OrdinalIgnoreCase) && prop is null)
                            prop = payload;
                        else if (name.Equals("CARD_Indx", StringComparison.OrdinalIgnoreCase) && indx is null)
                            indx = payload;
                    }
                    catch
                    {
                        // skip
                    }
                }

                if (prop is not null && indx is not null)
                    return (prop, indx);
            }
        }
        finally
        {
            am.UnloadAll();
        }

        return null;
    }

    private AssetsManager CreateAssetsManager()
    {
        var am = new AssetsManager();
        am.LoadClassPackage(_classDataPath);
        return am;
    }

    /// <summary>Thread-safe first-writer-wins holder for encrypted CARD_* payloads.</summary>
    private sealed class ConcurrentBag<T> where T : class
    {
        private readonly object _gate = new();
        private T? _value;

        public T? Value
        {
            get { lock (_gate) return _value; }
        }

        public void TrySet(T value)
        {
            lock (_gate)
                _value ??= value;
        }
    }
}

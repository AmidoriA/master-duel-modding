using AssetsTools.NET.Extra;
using Floowan.Core.Assets;
using Floowan.Core.Game;

namespace Floowan.Core.Data;

/// <summary>
/// Loads card-id → <see cref="LinkMarkerMask"/> from Master Duel <c>CARD_Prop</c>.
/// Prefers LocalData / StreamingAssets AssetBundles (same scan as catalog types);
/// falls back to <c>masterduel_Data/data.unity3d</c> when those lack CARD_* TextAssets.
/// </summary>
public sealed class CardLinkMarkerLoader
{
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

    public bool TryGetMarkers(string playerDataPath, int cardId, out LinkMarkerMask markers)
    {
        markers = LinkMarkerMask.None;
        var map = GetOrLoadMap(playerDataPath);
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

        var map = LoadMap(playerDataPath, progress, cancellationToken);
        lock (_cacheGate)
        {
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
        progress?.Report("Loading CARD_Prop link markers…");
        var prop = TryLoadEncryptedProp(playerDataPath, progress, cancellationToken);
        if (prop is null)
        {
            throw new InvalidOperationException(
                "Could not find CARD_Prop under LocalData/StreamingAssets or data.unity3d.");
        }

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
        try
        {
            var fromBundles = TryLoadPropFromBundles(playerDataPath, progress, cancellationToken);
            if (fromBundles is not null)
                return fromBundles;
        }
        catch
        {
            // try unity3d
        }

        try
        {
            var unityPath = GamePathLocator.ResolveUnity3dPath(playerDataPath);
            if (File.Exists(unityPath))
                return TryLoadPropFromUnity3d(unityPath, progress);
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private (byte[] Prop, byte[] Indx)? TryLoadPropFromBundles(
        string playerDataPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        var localRoot = BundlePathResolver.GetLocalDataRoot(playerDataPath);
        if (Directory.Exists(localRoot))
            roots.Add(localRoot);
        try
        {
            var streaming = BundlePathResolver.GetStreamingAssetsRoot(playerDataPath);
            if (Directory.Exists(streaming))
                roots.Add(streaming);
        }
        catch
        {
            // ignore
        }

        var files = roots
            .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .ToList();
        if (files.Count == 0)
            return null;

        var am = new AssetsManager();
        am.LoadClassPackage(_classDataPath);
        byte[]? prop = null, indx = null;
        var n = 0;
        try
        {
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                n++;
                if (n % 400 == 0)
                    progress?.Report($"Scanning for CARD_Prop (markers)… {n}/{files.Count}");

                long length;
                try { length = new FileInfo(path).Length; }
                catch { continue; }
                if (length is < 64 or > 5_000_000)
                    continue;

                try
                {
                    var bi = am.LoadBundleFile(path, unpackIfPacked: true);
                    if (bi?.file is null)
                        continue;
                    for (var i = 0; i < bi.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                    {
                        if (!bi.file.IsAssetsFile(i))
                            continue;
                        var ai = am.LoadAssetsFileFromBundle(bi, i, false);
                        if (ai?.file is null)
                            continue;
                        am.LoadClassDatabaseFromPackage(ai.file.Metadata.UnityVersion);
                        foreach (var info in ai.file.GetAssetsOfType(AssetClassID.TextAsset))
                        {
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
                                if (name == "card_prop" && prop is null)
                                    prop = payload;
                                else if ((name is "card_indx" or "card_index") && indx is null)
                                    indx = payload;
                            }
                            catch
                            {
                                // skip bad assets
                            }
                        }
                    }
                }
                catch
                {
                    // skip unloadable
                }
                finally
                {
                    am.UnloadAll(unloadClassData: false);
                }

                if (prop is not null && indx is not null)
                    return (prop, indx);
            }
        }
        finally
        {
            am.UnloadAll();
        }

        return prop is not null && indx is not null ? (prop, indx) : null;
    }

    private (byte[] Prop, byte[] Indx)? TryLoadPropFromUnity3d(
        string unityPath,
        IProgress<string>? progress)
    {
        progress?.Report("Reading CARD_Prop from data.unity3d…");
        var am = new AssetsManager();
        am.LoadClassPackage(_classDataPath);
        try
        {
            var bi = am.LoadBundleFile(unityPath, unpackIfPacked: true);
            byte[]? prop = null, indx = null;
            for (var i = 0; i < bi.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                if (!bi.file.IsAssetsFile(i))
                    continue;
                var ai = am.LoadAssetsFileFromBundle(bi, i, false);
                if (ai?.file is null)
                    continue;
                am.LoadClassDatabaseFromPackage(ai.file.Metadata.UnityVersion);
                foreach (var info in ai.file.GetAssetsOfType(AssetClassID.TextAsset))
                {
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
}

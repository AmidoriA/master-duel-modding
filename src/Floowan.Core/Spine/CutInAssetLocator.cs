using System.Globalization;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Floowan.Core.Assets;
using Floowan.Core.Game;

namespace Floowan.Core.Spine;

/// <summary>
/// Locates Master Duel cut-in Texture2D / atlas / skeleton TextAssets by scanning LocalData
/// for asset names matching <c>P{id}</c> / <c>P{id}JS</c>, with an on-disk index cache.
/// </summary>
public sealed class CutInAssetLocator : IDisposable
{
    private readonly string _classDataPath;
    private readonly string _indexPath;
    private Dictionary<int, CutInAssetSet>? _cache;

    public CutInAssetLocator(string? classDataPath = null, string? indexDirectory = null)
    {
        _classDataPath = ClassDataLocator.FindClassDataPath(classDataPath);
        indexDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Floowan");
        _indexPath = Path.Combine(indexDirectory, "cutin-index.json");
    }

    public string IndexPath => _indexPath;

    public bool TryLoadIndex()
    {
        if (!File.Exists(_indexPath))
            return false;
        try
        {
            var json = File.ReadAllText(_indexPath);
            var dto = JsonSerializer.Deserialize<IndexDto>(json);
            if (dto?.Entries is null)
                return false;
            _cache = dto.Entries
                .Where(e => e.CutInId > 0)
                .ToDictionary(e => e.CutInId, FromDto);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SaveIndex()
    {
        if (_cache is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        var dto = new IndexDto
        {
            Entries = _cache.Values.Select(ToDto).ToList()
        };
        File.WriteAllText(_indexPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    public CutInAssetSet? TryGetCached(int cutInId)
    {
        _cache ??= new Dictionary<int, CutInAssetSet>();
        return _cache.TryGetValue(cutInId, out var set) ? set : null;
    }

    public CutInAssetSet Find(string playerDataPath, int cutInId, IProgress<string>? progress = null)
    {
        if (cutInId <= 0)
            throw new ArgumentOutOfRangeException(nameof(cutInId));

        if (_cache is null)
            TryLoadIndex();

        if (_cache is not null &&
            _cache.TryGetValue(cutInId, out var cached) &&
            cached.IsComplete &&
            CachedPathsExist(cached))
        {
            return cached;
        }

        progress?.Report($"Scanning LocalData for cut-in P{cutInId}…");
        var found = ScanForId(playerDataPath, cutInId, progress);
        _cache ??= new Dictionary<int, CutInAssetSet>();
        _cache[cutInId] = found;
        try { SaveIndex(); } catch { /* best effort */ }
        return found;
    }

    /// <summary>
    /// Builds or refreshes the index for every ID in <paramref name="cutInIds"/>.
    /// Slow: walks all LocalData bundles once and matches any P#### name.
    /// </summary>
    public int BuildIndex(
        string playerDataPath,
        IEnumerable<int> cutInIds,
        IProgress<string>? progress = null)
    {
        var wanted = cutInIds.Where(id => id > 0).ToHashSet();
        if (wanted.Count == 0)
            return 0;

        var byId = wanted.ToDictionary(id => id, id => new List<CutInAssetHit>());
        var roots = EnumerateBundleRoots(playerDataPath).ToList();
        var scanned = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var bundlePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                scanned++;
                if (scanned % 250 == 0)
                    progress?.Report($"Indexed {scanned} bundles…");

                if (!LooksLikeBundle(bundlePath))
                    continue;
                if (!MightContainAny(bundlePath, wanted))
                    continue;

                try
                {
                    CollectHits(bundlePath, wanted, byId);
                }
                catch
                {
                    // Skip unreadable / non-asset files.
                }
            }
        }

        _cache ??= new Dictionary<int, CutInAssetSet>();
        var complete = 0;
        foreach (var id in wanted)
        {
            var set = SelectBestSet(id, byId[id]);
            _cache[id] = set;
            if (set.IsComplete)
                complete++;
        }

        SaveIndex();
        progress?.Report($"Cut-in index ready: {complete}/{wanted.Count} complete sets.");
        return complete;
    }

    private CutInAssetSet ScanForId(string playerDataPath, int cutInId, IProgress<string>? progress)
    {
        var hits = new List<CutInAssetHit>();
        var prefix = "P" + cutInId.ToString(CultureInfo.InvariantCulture);
        var roots = EnumerateBundleRoots(playerDataPath).ToList();
        var scanned = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var bundlePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                scanned++;
                if (scanned % 400 == 0)
                    progress?.Report($"Scanning… {scanned} files (looking for {prefix})");

                if (!LooksLikeBundle(bundlePath))
                    continue;
                if (!FileContainsAscii(bundlePath, prefix) && new FileInfo(bundlePath).Length > 512 * 1024)
                    continue;

                try
                {
                    CollectHitsForPrefix(bundlePath, cutInId, prefix, hits);
                }
                catch
                {
                    // ignore
                }

                if (SelectBestSet(cutInId, hits).IsComplete)
                    break;
            }

            if (SelectBestSet(cutInId, hits).IsComplete)
                break;
        }

        return SelectBestSet(cutInId, hits);
    }

    private void CollectHits(
        string bundlePath,
        HashSet<int> wanted,
        Dictionary<int, List<CutInAssetHit>> byId)
    {
        var am = new AssetsManager();
        try
        {
            am.LoadClassPackage(_classDataPath);
            var bundleInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
            for (var i = 0; i < bundleInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                AssetsFileInstance assetsInst;
                try { assetsInst = am.LoadAssetsFileFromBundle(bundleInst, i, false); }
                catch { continue; }

                am.LoadClassDatabaseFromPackage(assetsInst.file.Metadata.UnityVersion);
                foreach (var info in assetsInst.file.GetAssetsOfType(AssetClassID.Texture2D)
                             .Concat(assetsInst.file.GetAssetsOfType(AssetClassID.TextAsset)))
                {
                    var field = am.GetBaseField(assetsInst, info);
                    var name = field["m_Name"].AsString;
                    if (string.IsNullOrEmpty(name) || name.Length < 2 || (name[0] is not 'P' and not 'p'))
                        continue;
                    if (!TryParseCutInId(name, out var id) || !wanted.Contains(id))
                        continue;

                    var kind = Classify(name, info.TypeId);
                    if (kind is null)
                        continue;

                    byId[id].Add(new CutInAssetHit
                    {
                        CutInId = id,
                        Kind = kind.Value,
                        AssetName = name,
                        BundlePath = bundlePath,
                        PathId = info.PathId,
                        PreferHighEnd = bundlePath.Contains("highend", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
        }
        finally
        {
            am.UnloadAll();
        }
    }

    private void CollectHitsForPrefix(
        string bundlePath,
        int cutInId,
        string prefix,
        List<CutInAssetHit> hits)
    {
        var am = new AssetsManager();
        try
        {
            am.LoadClassPackage(_classDataPath);
            var bundleInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
            for (var i = 0; i < bundleInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                AssetsFileInstance assetsInst;
                try { assetsInst = am.LoadAssetsFileFromBundle(bundleInst, i, false); }
                catch { continue; }

                am.LoadClassDatabaseFromPackage(assetsInst.file.Metadata.UnityVersion);
                foreach (var info in assetsInst.file.GetAssetsOfType(AssetClassID.Texture2D)
                             .Concat(assetsInst.file.GetAssetsOfType(AssetClassID.TextAsset)))
                {
                    var field = am.GetBaseField(assetsInst, info);
                    var name = field["m_Name"].AsString;
                    if (string.IsNullOrEmpty(name) ||
                        !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var kind = Classify(name, info.TypeId);
                    if (kind is null)
                        continue;

                    hits.Add(new CutInAssetHit
                    {
                        CutInId = cutInId,
                        Kind = kind.Value,
                        AssetName = name,
                        BundlePath = bundlePath,
                        PathId = info.PathId,
                        PreferHighEnd = bundlePath.Contains("highend", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("highend", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
        }
        finally
        {
            am.UnloadAll();
        }
    }

    private static CutInAssetKind? Classify(string name, int typeId)
    {
        var upper = name.ToUpperInvariant();
        if (typeId == (int)AssetClassID.Texture2D)
        {
            // Texture page: P14944 (not P14944JS).
            if (upper.EndsWith("JS", StringComparison.Ordinal))
                return null;
            return CutInAssetKind.Texture;
        }

        if (typeId == (int)AssetClassID.TextAsset)
        {
            if (upper.EndsWith("JS", StringComparison.Ordinal))
                return CutInAssetKind.Skeleton;
            // Atlas TextAsset often shares the texture base name or ends with atlas-related tokens.
            return CutInAssetKind.Atlas;
        }

        return null;
    }

    private static bool TryParseCutInId(string assetName, out int id)
    {
        id = 0;
        if (assetName.Length < 2 || (assetName[0] is not 'P' and not 'p'))
            return false;
        var i = 1;
        while (i < assetName.Length && char.IsDigit(assetName[i]))
            i++;
        if (i == 1)
            return false;
        return int.TryParse(assetName.AsSpan(1, i - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static CutInAssetSet SelectBestSet(int cutInId, List<CutInAssetHit> hits)
    {
        static CutInAssetHit? Pick(IEnumerable<CutInAssetHit> candidates) =>
            candidates
                .OrderByDescending(h => h.PreferHighEnd)
                .ThenBy(h => h.AssetName.Length)
                .FirstOrDefault();

        return new CutInAssetSet
        {
            CutInId = cutInId,
            Texture = Pick(hits.Where(h => h.Kind == CutInAssetKind.Texture)),
            Atlas = Pick(hits.Where(h => h.Kind == CutInAssetKind.Atlas)),
            Skeleton = Pick(hits.Where(h => h.Kind == CutInAssetKind.Skeleton))
        };
    }

    private static IEnumerable<string> EnumerateBundleRoots(string playerDataPath)
    {
        var roots = new List<string> { Path.Combine(playerDataPath, "0000") };
        try
        {
            var install = GamePathLocator.ResolveInstallRoot(playerDataPath);
            roots.Add(Path.Combine(install, "masterduel_Data", "StreamingAssets", "AssetBundle"));
        }
        catch
        {
            // ignore
        }

        return roots;
    }

    private static bool LooksLikeBundle(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
            return false;
        // Master Duel LocalData bundles are usually 8-char hex without extension.
        var ext = Path.GetExtension(name);
        return string.IsNullOrEmpty(ext) || ext.Equals(".bundle", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".unity3d", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MightContainAny(string bundlePath, HashSet<int> wanted)
    {
        try
        {
            var length = new FileInfo(bundlePath).Length;
            if (length <= 512 * 1024)
                return true;
            // Cheap ASCII probe for any P{id}.
            foreach (var id in wanted)
            {
                if (FileContainsAscii(bundlePath, "P" + id))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool FileContainsAscii(string path, string ascii)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(ascii);
        if (needle.Length == 0)
            return true;
        using var fs = File.OpenRead(path);
        var buffer = new byte[1024 * 1024];
        int read;
        var overlap = new byte[needle.Length - 1];
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (IndexOf(buffer.AsSpan(0, read), needle) >= 0)
                return true;
            if (read < buffer.Length)
                break;
            // Keep overlap for spans across chunk boundaries — simple retry from previous tail.
            Array.Copy(buffer, read - overlap.Length, overlap, 0, overlap.Length);
        }

        return false;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }

    private static bool CachedPathsExist(CutInAssetSet set) =>
        set.All.All(h => File.Exists(h.BundlePath));

    private static CutInAssetSet FromDto(IndexEntryDto e) =>
        new()
        {
            CutInId = e.CutInId,
            Texture = e.Texture is null ? null : FromHitDto(e.Texture),
            Atlas = e.Atlas is null ? null : FromHitDto(e.Atlas),
            Skeleton = e.Skeleton is null ? null : FromHitDto(e.Skeleton)
        };

    private static IndexEntryDto ToDto(CutInAssetSet set) =>
        new()
        {
            CutInId = set.CutInId,
            Texture = set.Texture is null ? null : ToHitDto(set.Texture),
            Atlas = set.Atlas is null ? null : ToHitDto(set.Atlas),
            Skeleton = set.Skeleton is null ? null : ToHitDto(set.Skeleton)
        };

    private static CutInAssetHit FromHitDto(HitDto h) =>
        new()
        {
            CutInId = h.CutInId,
            Kind = Enum.Parse<CutInAssetKind>(h.Kind),
            AssetName = h.AssetName,
            BundlePath = h.BundlePath,
            PathId = h.PathId,
            PreferHighEnd = h.PreferHighEnd
        };

    private static HitDto ToHitDto(CutInAssetHit h) =>
        new()
        {
            CutInId = h.CutInId,
            Kind = h.Kind.ToString(),
            AssetName = h.AssetName,
            BundlePath = h.BundlePath,
            PathId = h.PathId,
            PreferHighEnd = h.PreferHighEnd
        };

    public void Dispose() { }

    private sealed class IndexDto
    {
        public List<IndexEntryDto> Entries { get; set; } = [];
    }

    private sealed class IndexEntryDto
    {
        public int CutInId { get; set; }
        public HitDto? Texture { get; set; }
        public HitDto? Atlas { get; set; }
        public HitDto? Skeleton { get; set; }
    }

    private sealed class HitDto
    {
        public int CutInId { get; set; }
        public string Kind { get; set; } = "";
        public string AssetName { get; set; } = "";
        public string BundlePath { get; set; } = "";
        public long PathId { get; set; }
        public bool PreferHighEnd { get; set; }
    }
}

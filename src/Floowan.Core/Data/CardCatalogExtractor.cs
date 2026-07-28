using System.Collections.Concurrent;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Floowan.Core.Assets;
using Floowan.Core.Game;

namespace Floowan.Core.Data;

/// <summary>
/// Extracts card catalog rows from Master Duel by scanning AssetBundles (AssetsTools.NET)
/// and decrypting CARD_* TextAssets — C# port of the card portion of the Floowandereeze ETL.
/// <para>
/// Path roots (both scanned):
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>LocalData</b> <c>{player}/0000</c> — primary for CARD_* TextAssets (name/desc/indx/prop)
/// and preferred for illustration bundles when the same art exists in both roots.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>StreamingAssets</b> <c>{install}/masterduel_Data/StreamingAssets/AssetBundle</c> —
/// base/shipped illustration (and other) bundles; resolved via
/// <see cref="BundlePathResolver.GetStreamingAssetsRoot"/>.
/// </description>
/// </item>
/// </list>
/// <para>
/// <c>created_at</c> is the filesystem creation time (UTC, ISO-8601) of the illustration
/// AssetBundle file chosen for that card (LocalData copy when present, otherwise StreamingAssets),
/// via <see cref="File.GetCreationTimeUtc"/>.
/// </para>
/// <para>
/// Incremental scans (optional creation cutoff) skip <b>entire</b> AssetBundle files whose
/// <see cref="File.GetCreationTimeUtc"/> is not strictly after that cutoff (DB
/// <c>MAX(created_at)</c>) — no <c>LoadBundleFile</c>, CARD_* parse, or illust parse for those
/// files. Only newer candidates are opened with AssetsTools.
/// </para>
/// </summary>
public sealed class CardCatalogExtractor
{
    // LZ4-packed bundles rarely expose path/asset strings as plaintext, so we open
    // size-filtered candidates with AssetsTools instead of relying on ASCII scans.
    private const long MinCardDataBytes = 8 * 1024;
    private const long MaxCardDataBytes = 2 * 1024 * 1024;
    private const long MinIllustBytes = 16 * 1024;
    // Inclusive upper bound for illustration candidates. Must stay above large LocalData
    // illust bundles (e.g. Fabled Lurrie / 2caa5d54 ≈ 4.6 MiB). The previous 3 MiB cap
    // skipped those files entirely, so cards never joined into the catalog.
    private const long MaxIllustBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Whether a file length falls in the illustration AssetBundle size window used before
    /// <c>LoadBundleFile</c>. Exposed for regression tests of the upper bound.
    /// </summary>
    public static bool IsIllustrationSizeCandidate(long length) =>
        length is >= MinIllustBytes and <= MaxIllustBytes;

    /// <summary>
    /// AssetsTools.NET class-package load is not safe to run concurrently against the same file.
    /// </summary>
    private static readonly object ClassPackageLoadGate = new();

    private readonly string _classDataPath;

    public CardCatalogExtractor(string? classDataPath = null)
    {
        _classDataPath = ClassDataLocator.FindClassDataPath(classDataPath);
    }

    /// <param name="illustCreatedAfterUtc">
    /// When set (incremental mode), any AssetBundle with <see cref="File.GetCreationTimeUtc"/>
    /// ≤ this cutoff is skipped entirely before AssetsTools open — no CARD_* or illust parse.
    /// Only files strictly newer than the cutoff are candidates for <c>LoadBundleFile</c>.
    /// </param>
    public CardCatalogExtractResult Extract(
        string playerDataPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? illustCreatedAfterUtc = null)
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return CardCatalogExtractResult.Fail(pathError ?? "Invalid game path.");

        progress?.Report("Enumerating AssetBundles (LocalData + StreamingAssets)…");
        var bundleFiles = EnumerateBundleFiles(playerDataPath);
        if (bundleFiles.Count == 0)
            return CardCatalogExtractResult.Fail("No AssetBundle files found under LocalData/0000 or StreamingAssets/AssetBundle.");

        DateTime? illustCutoffUtc = illustCreatedAfterUtc?.UtcDateTime;
        if (illustCutoffUtc is not null)
            progress?.Report($"Incremental: only files newer than {illustCutoffUtc:o} (File.GetCreationTimeUtc vs DB created_at); older files are not opened.");

        progress?.Report($"Scanning {bundleFiles.Count} bundles for card data and illustrations…");
        var artToBundle = new ConcurrentDictionary<int, string>();
        var artBundlePaths = new ConcurrentDictionary<int, string>();
        var cardData = new CardDataPayloads();
        var skippedOldBundles = 0;
        var openedBundles = 0;

        var checkedCount = 0;
        // One AssetsManager per worker thread; serialize LoadClassPackage.
        Parallel.ForEach(
            bundleFiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            },
            () => CreateAssetsManager(),
            (path, _, am) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = Interlocked.Increment(ref checkedCount);
                if (n % 400 == 0)
                    progress?.Report($"Scanning bundles… {n}/{bundleFiles.Count}");

                // Incremental: date gate before any size window / LoadBundleFile.
                if (illustCutoffUtc is DateTime cutoff)
                {
                    try
                    {
                        if (File.GetCreationTimeUtc(path) <= cutoff)
                        {
                            Interlocked.Increment(ref skippedOldBundles);
                            return am;
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref skippedOldBundles);
                        return am;
                    }
                }

                long length;
                try { length = new FileInfo(path).Length; }
                catch { return am; }

                var mayHaveCardData = !cardData.IsComplete
                    && length is >= MinCardDataBytes and <= MaxCardDataBytes;
                var mayHaveIllust = IsIllustrationSizeCandidate(length);
                if (!mayHaveCardData && !mayHaveIllust)
                    return am;

                try
                {
                    Interlocked.Increment(ref openedBundles);
                    // Single LoadBundleFile per path — a second load on a dirty manager
                    // silently drops illustration hits.
                    TryCollectFromBundle(
                        am, path, mayHaveCardData, mayHaveIllust,
                        cardData, artToBundle, artBundlePaths, progress);
                }
                catch (Exception ex)
                {
                    // Skip unreadable / non-bundle files; keep the scan going.
                    progress?.Report($"Skipping unreadable bundle {Path.GetFileName(path)}: {ex.GetType().Name}");
                }
                finally
                {
                    am.UnloadAll(unloadClassData: false);
                }

                return am;
            },
            am => am.UnloadAll());

        if (illustCutoffUtc is not null && skippedOldBundles > 0)
            progress?.Report($"Skipped {skippedOldBundles} files older than cutoff (not opened). Opened {openedBundles} newer candidates.");

        if (illustCutoffUtc is not null && artToBundle.IsEmpty)
        {
            progress?.Report("No new illustration files after cutoff; nothing to upsert.");
            return CardCatalogExtractResult.Ok(
                Array.Empty<CatalogCardRow>(),
                cryptoKey: 0,
                illustCount: 0,
                bundlesScanned: bundleFiles.Count);
        }

        if (cardData.Indx is null || cardData.Name is null || cardData.Desc is null || cardData.Prop is null)
        {
            return CardCatalogExtractResult.Fail(
                "Could not find encrypted CARD_Indx / CARD_Name / CARD_Desc / CARD_Prop TextAssets under LocalData/StreamingAssets.");
        }

        progress?.Report("Decrypting CARD_* files…");
        int cryptoKey;
        try
        {
            cryptoKey = CardDataCrypto.FindCryptoKey(cardData.Indx);
        }
        catch (Exception ex)
        {
            return CardCatalogExtractResult.Fail("CARD_* crypto key search failed: " + ex.Message);
        }

        byte[] decIndx;
        byte[] decName;
        byte[] decDesc;
        byte[] decProp;
        try
        {
            decIndx = CardDataCrypto.Decrypt(cardData.Indx, cryptoKey);
            decName = CardDataCrypto.Decrypt(cardData.Name, cryptoKey);
            decDesc = CardDataCrypto.Decrypt(cardData.Desc, cryptoKey);
            decProp = CardDataCrypto.Decrypt(cardData.Prop, cryptoKey);
        }
        catch (Exception ex)
        {
            return CardCatalogExtractResult.Fail("CARD_* decrypt failed: " + ex.Message);
        }

        progress?.Report("Building catalog rows…");
        var propRecords = CardPropTypeDecoder.ParseRecords(decProp);
        var ids = propRecords.Select(e => e.Id).ToList();
        var propTypeById = new Dictionary<int, string?>();
        var linkMarkersById = new Dictionary<int, LinkMarkerMask>();
        foreach (var group in propRecords.GroupBy(e => e.Id))
        {
            var rec = group.First();
            var label = CardPropTypeDecoder.InferLabel(rec.TypeByte, rec.TypeByte2);
            propTypeById[group.Key] = label;
            if (label == "Link")
                linkMarkersById[group.Key] = CardPropTypeDecoder.DecodeLinkMarkers(rec.PackedAtkDef);
        }
        var rawNames = CardDataFilesParser.SplitIndexedStrings(decIndx, decName, indexStart: 0);
        var descriptions = CardDataFilesParser.SplitIndexedStrings(decIndx, decDesc, indexStart: 4);
        var names = CardDataFilesParser.AddAltSuffixes(rawNames);
        var identity = CardDataFilesParser.BuildIdentityMap(ids, names, descriptions);

        var rows = new List<CatalogCardRow>(artToBundle.Count);
        foreach (var (artId, bundleId) in artToBundle.OrderBy(kv => kv.Key))
        {
            if (!identity.TryGetValue(artId, out var info))
                continue;

            artBundlePaths.TryGetValue(artId, out var bundlePath);
            string? createdAt = null;
            if (!string.IsNullOrEmpty(bundlePath))
            {
                try
                {
                    createdAt = File.GetCreationTimeUtc(bundlePath).ToString("o");
                }
                catch
                {
                    createdAt = null;
                }
            }

            // Prefer CARD_Prop (MD descriptions usually lack [Type] lines); fall back to lore heuristics.
            var cardType = propTypeById.GetValueOrDefault(artId)
                ?? CardTypeLabels.InferFromCardText(info.Name, info.Description);
            LinkMarkerMask? linkMarkers = null;
            if (linkMarkersById.TryGetValue(artId, out var markers))
                linkMarkers = markers;
            rows.Add(new CatalogCardRow
            {
                Id = artId,
                Name = info.Name,
                Description = info.Description,
                Bundle = bundleId,
                DataIndex = info.DataIndex,
                CardType = cardType,
                CreatedAt = createdAt,
                LinkMarkers = linkMarkers
            });
        }

        if (rows.Count == 0)
        {
            if (illustCutoffUtc is not null)
            {
                progress?.Report("No new illustration files after cutoff; nothing to upsert.");
                return CardCatalogExtractResult.Ok(rows, cryptoKey, artToBundle.Count, bundleFiles.Count);
            }

            return CardCatalogExtractResult.Fail("No card rows could be joined from illustrations + CARD_* data.");
        }

        progress?.Report($"Extracted {rows.Count} cards (crypto key 0x{cryptoKey:X}).");
        return CardCatalogExtractResult.Ok(rows, cryptoKey, artToBundle.Count, bundleFiles.Count);
    }

    /// <summary>
    /// Minimal CARD_* read: finds CARD_Indx + CARD_Prop (no illustration scan), decrypts, and
    /// returns card id → <see cref="CardPropTypeDecoder.InferLabel"/> (null when undecodable).
    /// Used for single-card <c>card_type</c> backfill without a full catalog rebuild.
    /// </summary>
    public IReadOnlyDictionary<int, string?> LoadCardPropTypeMap(
        string playerDataPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            throw new InvalidOperationException(pathError ?? "Invalid game path.");

        progress?.Report("Loading CARD_Prop for card types…");
        var bundleFiles = EnumerateBundleFiles(playerDataPath);
        if (bundleFiles.Count == 0)
            throw new InvalidOperationException(
                "No AssetBundle files found under LocalData/0000 or StreamingAssets/AssetBundle.");

        var cardData = new CardDataPayloads();
        var checkedCount = 0;
        // Illust maps unused (collectIllusts: false); shared so workers don't allocate per file.
        var unusedArt = new ConcurrentDictionary<int, string>();
        var unusedArtPaths = new ConcurrentDictionary<int, string>();

        Parallel.ForEach(
            bundleFiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            },
            () => CreateAssetsManager(),
            (path, _, am) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cardData.HasIndxAndProp)
                    return am;

                var n = Interlocked.Increment(ref checkedCount);
                if (n % 400 == 0)
                    progress?.Report($"Scanning for CARD_Prop… {n}/{bundleFiles.Count}");

                long length;
                try { length = new FileInfo(path).Length; }
                catch { return am; }

                if (length is < MinCardDataBytes or > MaxCardDataBytes)
                    return am;

                try
                {
                    // Illust collection off — type map only needs CARD_Indx / CARD_Prop TextAssets.
                    TryCollectFromBundle(
                        am, path,
                        collectCardData: true,
                        collectIllusts: false,
                        cardData,
                        unusedArt,
                        unusedArtPaths,
                        progress);
                }
                catch
                {
                    // Skip unreadable / non-bundle files.
                }
                finally
                {
                    am.UnloadAll(unloadClassData: false);
                }

                return am;
            },
            am => am.UnloadAll());

        if (cardData.Indx is null || cardData.Prop is null)
        {
            throw new InvalidOperationException(
                "Could not find encrypted CARD_Indx / CARD_Prop TextAssets under LocalData/StreamingAssets.");
        }

        progress?.Report("Decrypting CARD_Prop…");
        var cryptoKey = CardDataCrypto.FindCryptoKey(cardData.Indx);
        var decProp = CardDataCrypto.Decrypt(cardData.Prop, cryptoKey);
        var propEntries = CardPropTypeDecoder.ParseEntries(decProp);
        var map = propEntries
            .GroupBy(e => e.Id)
            .ToDictionary(
                g => g.Key,
                g => CardPropTypeDecoder.InferLabel(g.First().TypeByte, g.First().TypeByte2));

        progress?.Report($"Loaded types for {map.Count} CARD_Prop ids.");
        return map;
    }

    private AssetsManager CreateAssetsManager()
    {
        var am = new AssetsManager();
        lock (ClassPackageLoadGate)
            am.LoadClassPackage(_classDataPath);
        return am;
    }

    private static void TryCollectFromBundle(
        AssetsManager am,
        string bundlePath,
        bool collectCardData,
        bool collectIllusts,
        CardDataPayloads payloads,
        ConcurrentDictionary<int, string> artToBundle,
        ConcurrentDictionary<int, string> artBundlePaths,
        IProgress<string>? progress)
    {
        var bundleInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
        if (bundleInst?.file is null)
        {
            progress?.Report($"Skipping unloadable bundle {Path.GetFileName(bundlePath)}");
            return;
        }

        var bundleId = Path.GetFileName(bundlePath);
        var dirCount = bundleInst.file.BlockAndDirInfo.DirectoryInfos.Count;

        // Iterate by index so we can skip non-assets entries (.resS / .resource).
        // LoadAssetsFileFromBundle returns null for those — it does not throw.
        for (var i = 0; i < dirCount; i++)
        {
            if (!bundleInst.file.IsAssetsFile(i))
                continue;

            AssetsFileInstance? assetsInst;
            try
            {
                assetsInst = am.LoadAssetsFileFromBundle(bundleInst, i, false);
            }
            catch
            {
                continue;
            }

            if (assetsInst?.file is null)
            {
                var entryName = bundleInst.file.GetFileName(i);
                progress?.Report(
                    $"Skipping assets entry '{entryName}' in {bundleId} (load returned null)");
                continue;
            }

            lock (ClassPackageLoadGate)
                am.LoadClassDatabaseFromPackage(assetsInst.file.Metadata.UnityVersion);

            if (collectCardData && !payloads.IsComplete)
            {
                foreach (var info in assetsInst.file.GetAssetsOfType(AssetClassID.TextAsset))
                {
                    var baseField = am.GetBaseField(assetsInst, info);
                    var name = baseField["m_Name"].AsString ?? "";
                    if (ClassifyCardDataName(name) is not CardDataKind kind)
                        continue;

                    byte[] payload;
                    try
                    {
                        payload = ReadTextAssetBytes(baseField);
                    }
                    catch
                    {
                        continue;
                    }

                    if (payload.Length == 0)
                        continue;

                    payloads.TrySet(kind, payload);
                }
            }

            if (collectIllusts && ContainerHasCardIllust(am, assetsInst))
            {
                foreach (var info in assetsInst.file.GetAssetsOfType(AssetClassID.Texture2D))
                {
                    var baseField = am.GetBaseField(assetsInst, info);
                    var name = baseField["m_Name"].AsString;
                    if (string.IsNullOrWhiteSpace(name) || !int.TryParse(name, out var artId) || artId <= 0)
                        continue;

                    artToBundle.AddOrUpdate(
                        artId,
                        bundleId,
                        (_, existing) => PreferLocalBundleId(existing, bundleId, bundlePath));
                    artBundlePaths.AddOrUpdate(
                        artId,
                        bundlePath,
                        (_, existing) => PreferLocalBundlePath(existing, bundlePath));
                }
            }
        }
    }

    private static bool ContainerHasCardIllust(AssetsManager am, AssetsFileInstance assetsInst)
    {
        foreach (var info in assetsInst.file.GetAssetsOfType(AssetClassID.AssetBundle))
        {
            var field = am.GetBaseField(assetsInst, info);
            var container = field["m_Container"];
            if (container.IsDummy)
                continue;

            var list = container["Array"];
            if (list.IsDummy)
                list = container;

            foreach (var entry in list)
            {
                string? path = null;
                try { path = entry["first"].AsString; }
                catch { /* ignore */ }

                if (string.IsNullOrEmpty(path) && entry.Children.Count > 0)
                {
                    try { path = entry.Children[0].AsString; }
                    catch { /* ignore */ }
                }

                if (string.IsNullOrEmpty(path))
                    continue;

                var lower = path.ToLowerInvariant();
                if ((lower.Contains("card/images/illust/common/", StringComparison.Ordinal)
                     || lower.Contains("card/images/illust/tcg/", StringComparison.Ordinal))
                    && !lower.Contains("_info", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static CardDataKind? ClassifyCardDataName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n.EndsWith(".bytes", StringComparison.Ordinal))
            n = n[..^6];
        return n switch
        {
            "card_indx" or "card_index" => CardDataKind.Indx,
            "card_name" => CardDataKind.Name,
            "card_desc" or "card_description" => CardDataKind.Desc,
            "card_prop" => CardDataKind.Prop,
            _ => null
        };
    }

    private static byte[] ReadTextAssetBytes(AssetTypeValueField baseField)
    {
        var script = baseField["m_Script"];
        if (script.IsDummy)
            throw new InvalidOperationException("TextAsset has no m_Script field.");

        try
        {
            var arr = script.AsByteArray;
            if (arr is not null)
                return arr;
        }
        catch
        {
            // fall through
        }

        var data = script["Array"];
        if (!data.IsDummy)
        {
            try
            {
                return data.AsByteArray;
            }
            catch
            {
                // fall through
            }
        }

        throw new InvalidOperationException("Could not read TextAsset m_Script bytes.");
    }

    private static List<string> EnumerateBundleFiles(string playerDataPath)
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
            // ignore install resolve failures
        }

        // LocalData first so workers hit player patches before StreamingAssets base files.
        return roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .ToList();
    }

    private static string PreferLocalBundleId(string existingId, string candidateId, string candidatePath)
    {
        if (IsLocalDataPath(candidatePath))
            return candidateId;
        return existingId;
    }

    private static string PreferLocalBundlePath(string existingPath, string candidatePath)
    {
        if (IsLocalDataPath(candidatePath))
            return candidatePath;
        return existingPath;
    }

    private static bool IsLocalDataPath(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}LocalData{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/LocalData/", StringComparison.OrdinalIgnoreCase);

    private enum CardDataKind
    {
        Indx,
        Name,
        Desc,
        Prop
    }

    private sealed class CardDataPayloads
    {
        private readonly object _gate = new();
        public byte[]? Indx { get; private set; }
        public byte[]? Name { get; private set; }
        public byte[]? Desc { get; private set; }
        public byte[]? Prop { get; private set; }

        public bool IsComplete
        {
            get
            {
                lock (_gate)
                    return Indx is not null && Name is not null && Desc is not null && Prop is not null;
            }
        }

        /// <summary>True when CARD_Indx + CARD_Prop are loaded (enough to decrypt type bytes).</summary>
        public bool HasIndxAndProp
        {
            get
            {
                lock (_gate)
                    return Indx is not null && Prop is not null;
            }
        }

        public void TrySet(CardDataKind kind, byte[] payload)
        {
            lock (_gate)
            {
                switch (kind)
                {
                    case CardDataKind.Indx when Indx is null:
                        Indx = payload;
                        break;
                    case CardDataKind.Name when Name is null:
                        Name = payload;
                        break;
                    case CardDataKind.Desc when Desc is null:
                        Desc = payload;
                        break;
                    case CardDataKind.Prop when Prop is null:
                        Prop = payload;
                        break;
                }
            }
        }
    }
}

public sealed class CardCatalogExtractResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<CatalogCardRow> Rows { get; init; } = Array.Empty<CatalogCardRow>();
    public int? CryptoKey { get; init; }
    public int IllustCount { get; init; }
    public int BundlesScanned { get; init; }

    public static CardCatalogExtractResult Ok(
        IReadOnlyList<CatalogCardRow> rows,
        int cryptoKey,
        int illustCount,
        int bundlesScanned) =>
        new()
        {
            Success = true,
            Message = $"Extracted {rows.Count} cards.",
            Rows = rows,
            CryptoKey = cryptoKey,
            IllustCount = illustCount,
            BundlesScanned = bundlesScanned
        };

    public static CardCatalogExtractResult Fail(string message) =>
        new() { Success = false, Message = message };
}

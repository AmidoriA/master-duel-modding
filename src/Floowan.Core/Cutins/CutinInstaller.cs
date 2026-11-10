using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Floowan.Core.Cutins;

public enum CutinState
{
    Ok,
    NeedsRestore,
    SkippedOfficial,
}

public sealed record CutinStatus(CutinEntry Entry, CutinState State, string Detail);

public sealed record CutinApplyResult(int Active, int Built, int Kept, IReadOnlyList<string> Messages);

/// <summary>
/// Installs every registered cut-in (C# port of apply_all / cmd_apply / cmd_remove / cmd_status in
/// scripts/cutin/monster_cutin.py). Shares the registry, pristine backups and build keys with the Python
/// tool: a cut-in whose inputs are unchanged and whose bundles are still byte for byte what was written is
/// kept; only new or changed ones are built. Writes only files it created; existing game files are replaced
/// only for cut-ins that override an official one, and are backed up first.
/// </summary>
public sealed class CutinInstaller
{
    /// <summary>Same value as BUILD_VERSION in monster_cutin.py: bump both when the builder's output changes.</summary>
    public const int BuildVersion = 1;

    private readonly CutinRegistry _reg;
    private readonly string _player;
    private readonly string _classDataPath;
    private readonly ICutinTextureEncoder? _encoder;

    public CutinInstaller(CutinRegistry registry, string playerDir, string classDataPath, ICutinTextureEncoder? encoder)
    {
        _reg = registry;
        _player = ResolvePlayerDir(registry, playerDir);
        _classDataPath = classDataPath;
        _encoder = encoder;
    }

    public CutinRegistry Registry => _reg;
    public string PlayerDir => _player;

    /// <summary>Use the registry's own spelling of the folder when it is the same folder, so file keys match.</summary>
    public static string ResolvePlayerDir(CutinRegistry registry, string playerDir)
    {
        var full = Path.GetFullPath(playerDir).TrimEnd('\\', '/');
        if (registry.PlayerDir is { } stored && string.Equals(Path.GetFullPath(stored).TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase))
            return stored;
        return full;
    }

    public static bool IsGameRunning()
    {
        if (Environment.GetEnvironmentVariable("FLOOWAN_CUTIN_SKIP_GAME_CHECK") == "1")
            return false;
        try
        {
            var procs = Process.GetProcessesByName("masterduel");
            foreach (var p in procs)
                p.Dispose();
            return procs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    // ------------------------------------------------------------------ shared game files

    /// <summary>
    /// Path of the unpatched version of a shared game file, refreshing the backup when the game has
    /// replaced the file since our last write (an update).
    /// </summary>
    public string BaseFile(string logical, List<string>? messages = null)
    {
        var live = CutinNaming.BundleFile(_player, logical);
        var liveSha = CutinNaming.Sha256File(live);
        var pr = _reg.PristineOf(live);
        if (pr is { } p && File.Exists(p.File))
        {
            if (_reg.WrittenSha(live) == liveSha || p.Sha256 == liveSha)
                return p.File;
            messages?.Add($"{logical}: changed by the game since the last write (update?) - new backup taken");
        }
        Directory.CreateDirectory(Path.Combine(_reg.DataDir, "pristine"));
        var rel = Path.Combine("pristine", $"{Path.GetFileName(live)}-{liveSha[..12]}");
        File.Copy(live, Path.Combine(_reg.DataDir, rel), overwrite: true);
        _reg.Pristine[live] = new JsonObject { ["file"] = rel, ["sha256"] = liveSha, ["logical"] = logical, ["taken"] = Now() };
        _reg.Written.Remove(live);
        return Path.Combine(_reg.DataDir, rel);
    }

    private void PreserveOriginal(string path)
    {
        if (!File.Exists(path))
            return;
        var liveSha = CutinNaming.Sha256File(path);
        var pr = _reg.PristineOf(path);
        if (_reg.WrittenSha(path) == liveSha || pr?.Sha256 == liveSha)
            return;
        Directory.CreateDirectory(Path.Combine(_reg.DataDir, "pristine"));
        var rel = Path.Combine("pristine", $"{Path.GetFileName(path)}-{liveSha[..12]}");
        File.Copy(path, Path.Combine(_reg.DataDir, rel), overwrite: true);
        _reg.Pristine[path] = new JsonObject { ["file"] = rel, ["sha256"] = liveSha, ["logical"] = "official cut-in bundle", ["taken"] = Now() };
    }

    /// <summary>Write a game file. Files we did not write are only replaced with allowOverwrite (backed up first).</summary>
    private void WriteBytes(string path, byte[] data, bool allowOverwrite)
    {
        if (File.Exists(path) && _reg.WrittenSha(path) != CutinNaming.Sha256File(path))
        {
            if (!allowOverwrite)
                throw new InvalidOperationException($"Refusing to overwrite a game file this tool did not create: {path}");
            PreserveOriginal(path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".floowan-tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
        _reg.Written[path] = CutinNaming.Sha256Bytes(data);
    }

    public IReadOnlyList<int> ReadTableIds(string tablePath)
    {
        var am = new AssetsManager();
        try
        {
            am.LoadClassPackage(_classDataPath);
            var bun = am.LoadBundleFile(tablePath, unpackIfPacked: true);
            var afi = am.LoadAssetsFileFromBundle(bun, 0, false);
            am.LoadClassDatabaseFromPackage(afi.file.Metadata.UnityVersion);
            foreach (var ai in afi.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var bf = am.GetBaseField(afi, ai);
                if (bf["m_Name"].IsDummy || bf["m_Name"].AsString != "CardIndividualData")
                    continue;
                return bf["monsterCutinTable"]["Array"].Children.Select(c => c["mrk"].AsInt).ToList();
            }
            throw new InvalidOperationException("CardIndividualData not found.");
        }
        finally
        {
            am.UnloadAll();
        }
    }

    private byte[] BuildTable(string tableBase, IEnumerable<int> addIds)
    {
        var am = new AssetsManager();
        try
        {
            am.LoadClassPackage(_classDataPath);
            var bun = am.LoadBundleFile(tableBase, unpackIfPacked: true);
            var afi = am.LoadAssetsFileFromBundle(bun, 0, false);
            am.LoadClassDatabaseFromPackage(afi.file.Metadata.UnityVersion);
            foreach (var ai in afi.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
            {
                var bf = am.GetBaseField(afi, ai);
                if (bf["m_Name"].IsDummy || bf["m_Name"].AsString != "CardIndividualData")
                    continue;
                var arr = bf["monsterCutinTable"]["Array"];
                foreach (var id in addIds)
                {
                    var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                    el["mrk"].AsInt = id;
                    arr.Children.Add(el);
                }
                ai.SetNewData(bf);
                bun.file.BlockAndDirInfo.DirectoryInfos[0].SetNewData(afi.file);
                return BundleIo.SavePacked(bun);
            }
            throw new InvalidOperationException("CardIndividualData not found.");
        }
        finally
        {
            am.UnloadAll();
        }
    }

    public static JsonObject ReadManifest(string path) =>
        JsonNode.Parse(Encoding.UTF8.GetString(CutinNaming.ManifestCrypt(File.ReadAllBytes(path))))!.AsObject();

    // ------------------------------------------------------------------ build keys

    /// <summary>
    /// Hash of everything a cut-in's bundles are built from (identical to build_key in monster_cutin.py):
    /// build version, card, mode, template/donor, override flag, the source manifest entries and bundle files,
    /// and for custom cut-ins the content files. Same key = same output.
    /// </summary>
    public string BuildKey(CutinEntry c, JsonArray baseInformations, bool overrideOfficial)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var head = new JsonArray(BuildVersion, c.CardId, c.Mode,
            c.Raw["template"] is JsonValue t ? t.GetValue<int>() : null,
            c.Raw["src"] is JsonValue s ? s.GetValue<int>() : null,
            overrideOfficial);
        h.AppendData(Encoding.UTF8.GetBytes(PyJson.Dumps(head)));
        var ents = CutinBuilder.SourceEntries(c, baseInformations);
        h.AppendData(Encoding.UTF8.GetBytes(PyJson.Dumps(new JsonArray(ents.Select(e => (JsonNode?)e.DeepClone()).ToArray()), sortKeys: true)));
        foreach (var e in ents)
        {
            var f = CutinNaming.BundleFile(_player, e["assetName"]!.GetValue<string>());
            h.AppendData(File.Exists(f) ? Encoding.ASCII.GetBytes(CutinNaming.Sha256File(f)) : "missing"u8.ToArray());
        }
        if (c.IsCustom)
        {
            var dir = _reg.ContentDir(c);
            foreach (var name in CutinRegistry.BuildInputFiles)
                h.AppendData(Encoding.ASCII.GetBytes(CutinNaming.Sha256File(Path.Combine(dir, name))));
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    private bool Reusable(CutinEntry c, string key) =>
        c.BuildKey == key && c.Files.Count > 0 &&
        c.Files.All(f => File.Exists(f) && CutinNaming.Sha256File(f) == _reg.WrittenSha(f));

    // ------------------------------------------------------------------ apply

    /// <summary>Install every registered cut-in; only new or changed ones are built (rebuild: all).</summary>
    public CutinApplyResult ApplyAll(bool rebuild = false, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsGameRunning())
            throw new InvalidOperationException("Master Duel is running. Close it first.");
        var messages = new List<string>();
        _reg.PlayerDir = _player;
        var tableLive = CutinNaming.BundleFile(_player, CutinNaming.TablePath);
        var manLive = CutinNaming.BundleFile(_player, CutinNaming.ManifestName);
        var tableBase = BaseFile(CutinNaming.TablePath, messages);
        var manBase = BaseFile(CutinNaming.ManifestName, messages);
        _reg.Save();

        var baseIds = ReadTableIds(tableBase).ToHashSet();
        var baseMan = ReadManifest(manBase);
        var baseInfo = baseMan["informations"]!.AsArray();
        var baseNames = baseInfo.Select(n => n!["assetName"]!.GetValue<string>()).ToHashSet();

        var active = new List<(CutinEntry Entry, bool Override)>();
        var replaced = new HashSet<int>();
        foreach (var c in _reg.Entries)
        {
            var official = baseIds.Contains(c.CardId) || baseNames.Contains(CutinNaming.CutinRoot(c.CardId, "SD"));
            if (official && !c.OverrideOfficial)
            {
                messages.Add($"{c.Name ?? c.CardId.ToString()} ({c.CardId}) now has an official cut-in in the game; skipped (it stays registered).");
                c.Raw["skipped_official"] = true;
                c.Raw["files"] = new JsonArray();
                continue;
            }
            c.Raw.Remove("skipped_official");
            if (official)
                replaced.Add(c.CardId);
            active.Add((CutinEntry.From(c.CardId, c.Raw), official));
        }

        var newEntries = new List<JsonObject>();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        CutinBuilder? builder = null;
        int built = 0, kept = 0;
        foreach (var (c, ov) in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = $"{c.Name ?? c.CardId.ToString()} ({c.CardId})";
            var key = BuildKey(c, baseInfo, ov);
            List<string> files;
            if (!rebuild && Reusable(c, key))
            {
                files = c.Files.ToList();
                kept++;
            }
            else
            {
                progress?.Report($"Building {label}…");
                builder ??= new CutinBuilder(_classDataPath,
                    _encoder ?? throw new InvalidOperationException("A texture encoder is needed to build custom cut-ins."));
                var bundles = c.IsCustom
                    ? builder.BuildCustom(_player, c, _reg.ContentDir(c), baseInfo, progress, cancellationToken)
                    : builder.BuildBorrow(_player, c, baseInfo);
                foreach (var b in bundles)
                    WriteBytes(b.Path, b.Data, ov);
                files = bundles.Select(b => b.Path).ToList();
                c.Raw["build_key"] = key;
                built++;
            }
            newEntries.AddRange(CutinBuilder.OwnEntries(c, baseInfo));
            keep.UnionWith(files);
            c.Raw["files"] = new JsonArray(files.OrderBy(f => f, StringComparer.Ordinal).Select(f => (JsonNode?)f).ToArray());
        }

        if (active.Count > 0)
        {
            progress?.Report("Updating the cut-in table and manifest…");
            WriteBytes(tableLive, BuildTable(tableBase, active.Select(a => a.Entry.CardId).Where(id => !baseIds.Contains(id))), allowOverwrite: true);
            var dropPrefixes = replaced.Select(CutinNaming.CutinDir).ToList();
            var kept2 = baseInfo.Select(n => n!.AsObject())
                .Where(e => !dropPrefixes.Any(p => e["assetName"]!.GetValue<string>().StartsWith(p, StringComparison.Ordinal)));
            var all = new JsonArray(newEntries.Select(e => (JsonNode?)e).Concat(kept2.Select(e => (JsonNode?)e.DeepClone())).ToArray());
            var text = PyJson.Dumps(new JsonObject { ["informations"] = all }, compact: true, ensureAscii: false);
            WriteBytes(manLive, CutinNaming.ManifestCrypt(Encoding.UTF8.GetBytes(text)), allowOverwrite: true);
        }
        else
        {
            foreach (var (live, b) in new[] { (tableLive, tableBase), (manLive, manBase) })
            {
                File.Copy(b, live, overwrite: true);
                _reg.Written.Remove(live);
            }
            messages.Add("No cut-ins registered: the original game files were put back.");
        }

        foreach (var f in _reg.CreatedFiles)
        {
            if (keep.Contains(f))
                continue;
            var pr = _reg.PristineOf(f);
            if (File.Exists(f) && CutinNaming.Sha256File(f) == _reg.WrittenSha(f))
            {
                if (pr is { } p)
                    File.Copy(p.File, f, overwrite: true);
                else
                    File.Delete(f);
            }
            _reg.Written.Remove(f);
        }
        var created = _reg.CreatedFiles.Where(keep.Contains).Union(keep).Distinct().OrderBy(f => f, StringComparer.Ordinal);
        _reg.Root["created"] = new JsonArray(created.Select(f => (JsonNode?)f).ToArray());
        _reg.Root["last_apply"] = Now();
        _reg.Save();
        return new CutinApplyResult(active.Count, built, kept, messages);
    }

    // ------------------------------------------------------------------ register / remove

    /// <summary>True when the game itself has a cut-in for the card (in the unpatched table or manifest).</summary>
    public bool HasOfficialCutin(int cardId)
    {
        var tableLive = CutinNaming.BundleFile(_player, CutinNaming.TablePath);
        var manLive = CutinNaming.BundleFile(_player, CutinNaming.ManifestName);
        var table = _reg.PristineOf(tableLive)?.File is { } t && File.Exists(t) ? t : tableLive;
        var man = _reg.PristineOf(manLive)?.File is { } m && File.Exists(m) ? m : manLive;
        if (ReadTableIds(table).Contains(cardId))
            return true;
        var root = CutinNaming.CutinRoot(cardId, "SD");
        return ReadManifest(man)["informations"]!.AsArray().Any(e => e!["assetName"]!.GetValue<string>() == root);
    }

    /// <summary>Register a custom cut-in from a content folder (copied into the registry) and install everything.</summary>
    public CutinApplyResult RegisterCustom(int cardId, string contentDir, string name, int template, bool overrideOfficial,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, bool apply = true)
    {
        if (IsGameRunning())
            throw new InvalidOperationException("Master Duel is running. Close it first.");
        var missing = CutinRegistry.ContentFiles.Where(f => !File.Exists(Path.Combine(contentDir, f))).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("The content folder is missing " + string.Join(", ", missing));
        if (HasOfficialCutin(cardId) && !overrideOfficial)
            throw new InvalidOperationException($"{name} ({cardId}) has an official cut-in. Replacing it must be confirmed.");
        var dst = Path.Combine(_reg.DataDir, "content", cardId.ToString());
        if (!string.Equals(Path.GetFullPath(contentDir).TrimEnd('\\'), Path.GetFullPath(dst).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(contentDir))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
        var entry = new JsonObject
        {
            ["mode"] = "custom",
            ["template"] = template,
            ["name"] = name,
            ["content"] = Path.Combine("content", cardId.ToString()),
            ["added"] = Now(),
        };
        return Register(cardId, entry, overrideOfficial, apply, progress, cancellationToken);
    }

    /// <summary>Give a card another card's existing cut-in.</summary>
    public CutinApplyResult RegisterBorrow(int cardId, int sourceCardId, string name, bool overrideOfficial,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, bool apply = true)
    {
        if (IsGameRunning())
            throw new InvalidOperationException("Master Duel is running. Close it first.");
        if (HasOfficialCutin(cardId) && !overrideOfficial)
            throw new InvalidOperationException($"{name} ({cardId}) has an official cut-in. Replacing it must be confirmed.");
        var entry = new JsonObject { ["mode"] = "borrow", ["src"] = sourceCardId, ["name"] = name, ["added"] = Now() };
        return Register(cardId, entry, overrideOfficial, apply, progress, cancellationToken);
    }

    private CutinApplyResult Register(int cardId, JsonObject entry, bool overrideOfficial, bool apply, IProgress<string>? progress, CancellationToken ct)
    {
        if (overrideOfficial && HasOfficialCutin(cardId))
            entry["override_official"] = true;
        if (_reg.Cutins[cardId.ToString()] is JsonObject old)
        {
            // re-applying identical content keeps its bundles (the key covers the content files)
            foreach (var k in new[] { "build_key", "files" })
                if (old[k] is { } v)
                    entry[k] = v.DeepClone();
        }
        _reg.Cutins[cardId.ToString()] = entry;
        _reg.Save();
        return apply ? ApplyAll(false, progress, ct) : new CutinApplyResult(0, 0, 0, []);
    }

    /// <summary>Uninstall cut-ins (custom content is deleted) and put back what the game had.</summary>
    public CutinApplyResult Remove(IEnumerable<int>? cardIds, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsGameRunning())
            throw new InvalidOperationException("Master Duel is running. Close it first.");
        var ids = cardIds?.Select(i => i.ToString()).ToList() ?? _reg.Cutins.Select(kv => kv.Key).ToList();
        foreach (var id in ids)
        {
            if (_reg.Cutins[id] is not JsonObject o)
                continue;
            var e = CutinEntry.From(int.Parse(id), o);
            _reg.Cutins.Remove(id);
            if (e.IsCustom)
            {
                try { Directory.Delete(_reg.ContentDir(e), recursive: true); } catch { /* already gone */ }
            }
        }
        _reg.Save();
        return ApplyAll(false, progress, cancellationToken);
    }

    // ------------------------------------------------------------------ status

    /// <summary>Is every registered cut-in still installed (the game replaces patched files on updates)?</summary>
    public IReadOnlyList<CutinStatus> Status()
    {
        var entries = _reg.Entries;
        if (entries.Count == 0)
            return [];
        var ids = ReadTableIds(CutinNaming.BundleFile(_player, CutinNaming.TablePath)).ToHashSet();
        var names = ReadManifest(CutinNaming.BundleFile(_player, CutinNaming.ManifestName))["informations"]!.AsArray()
            .Select(e => e!["assetName"]!.GetValue<string>()).ToHashSet();
        var result = new List<CutinStatus>();
        foreach (var c in entries)
        {
            if (c.SkippedOfficial)
            {
                result.Add(new CutinStatus(c, CutinState.SkippedOfficial, "The game has its own cut-in for this card."));
                continue;
            }
            var filesOk = c.Files.Count > 0 && c.Files.All(f => File.Exists(f) && CutinNaming.Sha256File(f) == _reg.WrittenSha(f));
            var inTable = ids.Contains(c.CardId);
            var inManifest = names.Contains(CutinNaming.CutinRoot(c.CardId, "SD"));
            var ok = filesOk && inTable && inManifest;
            var why = ok ? "" : !inTable || !inManifest ? "The game reset its cut-in list (update?)." : "The game replaced its files (update?).";
            result.Add(new CutinStatus(c, ok ? CutinState.Ok : CutinState.NeedsRestore, why));
        }
        return result;
    }
}

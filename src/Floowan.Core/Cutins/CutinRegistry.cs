using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Floowan.Core.Cutins;

/// <summary>
/// The cut-in registry shared with scripts/cutin/monster_cutin.py:
/// %LOCALAPPDATA%\Floowan\cutins\registry.json plus pristine/ (original game files) and content/&lt;id&gt;/.
/// Unknown fields are kept, so both tools can read and write the same file.
/// </summary>
public sealed class CutinRegistry
{
    /// <summary>Content files of a custom cut-in (same list and order as the Python tool's CONTENT_FILES).</summary>
    public static readonly string[] ContentFiles =
        ["skeleton.json", "hd.atlas", "sd.atlas", "hd.png", "sd.png", "meta.json", "preview.gif", "preview_sheet.png"];

    /// <summary>The first five content files are build inputs (part of the build key).</summary>
    public static IEnumerable<string> BuildInputFiles => ContentFiles.Take(5);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private CutinRegistry(string dataDir, JsonObject root)
    {
        DataDir = dataDir;
        Root = root;
    }

    public static string DefaultDataDir =>
        Environment.GetEnvironmentVariable("FLOOWAN_CUTIN_DATA") is { Length: > 0 } env
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Floowan", "cutins");

    public string DataDir { get; }
    public string RegistryPath => Path.Combine(DataDir, "registry.json");
    public JsonObject Root { get; }

    public static CutinRegistry Load(string? dataDir = null)
    {
        dataDir ??= DefaultDataDir;
        var path = Path.Combine(dataDir, "registry.json");
        JsonObject root;
        if (File.Exists(path))
            root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
        else
            root = new JsonObject();
        root.TryAdd("version", 1);
        root.TryAdd("player_dir", null);
        foreach (var key in new[] { "cutins", "pristine", "written" })
            if (root[key] is not JsonObject)
                root[key] = new JsonObject();
        if (root["created"] is not JsonArray)
            root["created"] = new JsonArray();
        return new CutinRegistry(dataDir, root);
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = RegistryPath + ".tmp";
        File.WriteAllText(tmp, Root.ToJsonString(WriteOptions));
        File.Move(tmp, RegistryPath, overwrite: true);
    }

    public string? PlayerDir
    {
        get => Root["player_dir"]?.GetValue<string>();
        set => Root["player_dir"] = value;
    }

    public JsonObject Cutins => Root["cutins"]!.AsObject();
    public JsonObject Pristine => Root["pristine"]!.AsObject();
    public JsonObject Written => Root["written"]!.AsObject();
    public JsonArray Created => Root["created"]!.AsArray();

    public string? WrittenSha(string path) => Written[path]?.GetValue<string>();

    /// <summary>Pristine backup of a game file: absolute path of the stored copy and its SHA-256.</summary>
    public (string File, string Sha256)? PristineOf(string path)
    {
        if (Pristine[path] is not JsonObject p)
            return null;
        return (Path.Combine(DataDir, p["file"]!.GetValue<string>()), p["sha256"]!.GetValue<string>());
    }

    public IReadOnlyList<CutinEntry> Entries =>
        Cutins.Select(kv => CutinEntry.From(int.Parse(kv.Key), kv.Value!.AsObject()))
            .OrderBy(e => e.CardId)
            .ToList();

    public CutinEntry? Get(int cardId) =>
        Cutins[cardId.ToString()] is JsonObject o ? CutinEntry.From(cardId, o) : null;

    public string ContentDir(CutinEntry entry) =>
        Path.Combine(DataDir, entry.Content ?? Path.Combine("content", entry.CardId.ToString()));

    public IReadOnlyList<string> CreatedFiles => Created.Select(n => n!.GetValue<string>()).ToList();
}

/// <summary>One registered cut-in: "custom" (own Spine content, cloned from a template) or "borrow".</summary>
public sealed record CutinEntry(
    int CardId,
    string Mode,
    int? Template,
    int? Source,
    string? Name,
    string? Content,
    bool OverrideOfficial,
    bool SkippedOfficial,
    IReadOnlyList<string> Files,
    string? BuildKey,
    string? Added,
    JsonObject Raw)
{
    public bool IsCustom => Mode == "custom";

    public static CutinEntry From(int cardId, JsonObject o) => new(
        cardId,
        o["mode"]?.GetValue<string>() ?? "custom",
        o["template"] is JsonValue t ? t.GetValue<int>() : null,
        o["src"] is JsonValue s ? s.GetValue<int>() : null,
        o["name"]?.GetValue<string>(),
        o["content"]?.GetValue<string>(),
        o["override_official"]?.GetValue<bool>() ?? false,
        o["skipped_official"]?.GetValue<bool>() ?? false,
        o["files"] is JsonArray f ? f.Select(x => x!.GetValue<string>()).ToList() : [],
        o["build_key"]?.GetValue<string>(),
        o["added"]?.GetValue<string>(),
        o);
}

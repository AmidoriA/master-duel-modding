using System.Globalization;
using System.Reflection;

namespace Floowan.Core.Spine;

/// <summary>
/// Maps Master Duel cut-in CardIDs (P####) to card names.
/// Source: daominah monster_cutin coverage table (cards that ship a summon cut-in).
/// </summary>
public sealed class CutInCatalog
{
    private readonly Dictionary<int, string> _idToName;
    private readonly Dictionary<string, int> _nameToId;

    public CutInCatalog(IEnumerable<(int Id, string Name)> entries)
    {
        _idToName = new Dictionary<int, string>();
        _nameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in entries)
        {
            if (id <= 0 || string.IsNullOrWhiteSpace(name))
                continue;
            _idToName[id] = name;
            // First wins for duplicate names (alt arts keep distinct IDs; match exact then stripped).
            if (!_nameToId.ContainsKey(name))
                _nameToId[name] = id;

            var stripped = StripAltArtSuffix(name);
            if (!_nameToId.ContainsKey(stripped))
                _nameToId[stripped] = id;
        }
    }

    public int Count => _idToName.Count;

    public static CutInCatalog LoadEmbedded()
    {
        var asm = typeof(CutInCatalog).Assembly;
        const string resourceName = "Floowan.Core.Resources.cutin.monster_cutin.csv";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? OpenFallbackFile()
            ?? throw new InvalidOperationException(
                "Embedded cut-in catalog resource was not found. Ensure monster_cutin.csv is embedded.");

        return LoadFromStream(stream);
    }

    public static CutInCatalog LoadFromStream(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var entries = new List<(int, string)>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;
            var sep = line.IndexOf('|');
            if (sep <= 0)
                continue;
            var idPart = line[..sep].Trim();
            var name = line[(sep + 1)..].Trim();
            if (!int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                continue;
            entries.Add((id, name));
        }

        return new CutInCatalog(entries);
    }

    public bool TryGetName(int cutInId, out string? name) =>
        _idToName.TryGetValue(cutInId, out name);

    public bool HasCutInId(int cutInId) => _idToName.ContainsKey(cutInId);

    /// <summary>
    /// Resolves a Floowan/display card name to a cut-in CardID.
    /// Tries exact name, then name without " (alt art)".
    /// </summary>
    public bool TryResolveByCardName(string? cardName, out int cutInId)
    {
        cutInId = 0;
        if (string.IsNullOrWhiteSpace(cardName))
            return false;

        if (_nameToId.TryGetValue(cardName.Trim(), out cutInId))
            return true;

        var stripped = StripAltArtSuffix(cardName.Trim());
        return _nameToId.TryGetValue(stripped, out cutInId);
    }

    public IReadOnlyCollection<int> AllIds => _idToName.Keys;

    private static string StripAltArtSuffix(string name)
    {
        const string suffix = " (alt art)";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length].Trim()
            : name;
    }

    private static Stream? OpenFallbackFile()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "cutin", "monster_cutin.csv"),
            Path.Combine(AppContext.BaseDirectory, "monster_cutin.csv"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Resources", "cutin", "monster_cutin.csv")),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return File.OpenRead(path);
        }

        // Dev: path relative to this source file's project.
        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (asmDir is not null)
        {
            var src = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "Resources", "cutin", "monster_cutin.csv"));
            if (File.Exists(src))
                return File.OpenRead(src);
        }

        return null;
    }
}

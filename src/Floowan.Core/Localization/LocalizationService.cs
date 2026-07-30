using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace Floowan.Core.Localization;

/// <summary>
/// Loads YAML locale files from a directory, discovers available cultures by filename,
/// and resolves keys with fallback to <see cref="FallbackCulture"/>.
/// </summary>
public sealed class LocalizationService
{
    public const string FallbackCulture = "en-US";

    private readonly string _localesDirectory;
    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string _currentCulture = FallbackCulture;

    public LocalizationService(string localesDirectory)
    {
        _localesDirectory = localesDirectory ?? throw new ArgumentNullException(nameof(localesDirectory));
        Reload();
        _currentCulture = ResolveInitialCulture(null);
    }

    /// <summary>Directory scanned for <c>*.yaml</c> / <c>*.yml</c> locale files.</summary>
    public string LocalesDirectory => _localesDirectory;

    public string CurrentCulture
    {
        get
        {
            lock (_gate) return _currentCulture;
        }
    }

    /// <summary>Raised after <see cref="SetCulture"/> changes the active language.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// Discovers culture codes from locale filenames (e.g. <c>en-US.yaml</c> → <c>en-US</c>).
    /// Always includes <see cref="FallbackCulture"/> even if the file is missing.
    /// </summary>
    public IReadOnlyList<string> GetAvailableCultures()
    {
        lock (_gate)
            return GetAvailableCulturesUnlocked();
    }

    private IReadOnlyList<string> GetAvailableCulturesUnlocked()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FallbackCulture };
        foreach (var culture in _catalogs.Keys)
            set.Add(NormalizeCulture(culture));

        if (Directory.Exists(_localesDirectory))
        {
            foreach (var path in EnumerateLocaleFiles(_localesDirectory))
            {
                var culture = CultureFromFileName(Path.GetFileName(path));
                if (culture is not null)
                    set.Add(culture);
            }
        }

        return set
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Re-scan the locales folder and reload all YAML catalogs.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _catalogs.Clear();
            if (!Directory.Exists(_localesDirectory))
                return;

            foreach (var path in EnumerateLocaleFiles(_localesDirectory))
            {
                var culture = CultureFromFileName(Path.GetFileName(path));
                if (culture is null)
                    continue;

                try
                {
                    _catalogs[culture] = LoadFlatMap(path);
                }
                catch
                {
                    // Skip unreadable / invalid files; discovery still lists the culture if desired.
                }
            }
        }
    }

    /// <summary>
    /// Sets the active culture. Accepts exact codes (<c>th-TH</c>) or language-only (<c>th</c>).
    /// Falls back to <see cref="FallbackCulture"/> when nothing matches.
    /// </summary>
    public bool SetCulture(string? culture)
    {
        string resolved;
        lock (_gate)
        {
            resolved = ResolveCultureOrFallbackUnlocked(culture);
            if (string.Equals(_currentCulture, resolved, StringComparison.OrdinalIgnoreCase))
                return false;
            _currentCulture = resolved;
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Picks OS UI culture when <paramref name="preferred"/> is null/empty/"auto",
    /// otherwise resolves the preferred code (with language-only matching).
    /// </summary>
    public string ResolveInitialCulture(string? preferred)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(preferred) ||
                string.Equals(preferred.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveCultureOrFallbackUnlocked(CultureInfo.CurrentUICulture.Name);
            }

            return ResolveCultureOrFallbackUnlocked(preferred);
        }
    }

    public string T(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return key ?? "";

        lock (_gate)
        {
            if (TryGetUnlocked(_currentCulture, key, out var value))
                return value;
            if (!string.Equals(_currentCulture, FallbackCulture, StringComparison.OrdinalIgnoreCase) &&
                TryGetUnlocked(FallbackCulture, key, out value))
                return value;
        }

        return key;
    }

    public string T(string key, params object[] args)
    {
        var format = T(key);
        if (args is null || args.Length == 0)
            return format;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    public bool HasKey(string key, string? culture = null)
    {
        lock (_gate)
        {
            var c = culture ?? _currentCulture;
            return TryGetUnlocked(c, key, out _) ||
                   (!string.Equals(c, FallbackCulture, StringComparison.OrdinalIgnoreCase) &&
                    TryGetUnlocked(FallbackCulture, key, out _));
        }
    }

    private bool TryGetUnlocked(string culture, string key, out string value)
    {
        value = "";
        return _catalogs.TryGetValue(NormalizeCulture(culture), out var map) &&
               map.TryGetValue(key, out value!);
    }

    private string ResolveCultureOrFallbackUnlocked(string? culture)
    {
        var available = GetAvailableCulturesUnlocked();
        if (string.IsNullOrWhiteSpace(culture))
            return FallbackCulture;

        var normalized = NormalizeCulture(culture);
        foreach (var a in available)
        {
            if (string.Equals(a, normalized, StringComparison.OrdinalIgnoreCase))
                return a;
        }

        var lang = normalized.Split('-', 2)[0];
        foreach (var a in available)
        {
            if (string.Equals(a, lang, StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase))
                return a;
        }

        return FallbackCulture;
    }

    private static IEnumerable<string> EnumerateLocaleFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directory, "*.yml", SearchOption.TopDirectoryOnly));

    internal static string? CultureFromFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (!ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            return null;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
            return null;

        return NormalizeCulture(stem);
    }

    internal static string NormalizeCulture(string culture)
    {
        culture = culture.Trim().Replace('_', '-');
        var parts = culture.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return culture;

        if (parts.Length == 1)
            return parts[0].ToLowerInvariant();

        return parts[0].ToLowerInvariant() + "-" + parts[1].ToUpperInvariant();
    }

    internal static Dictionary<string, string> LoadFlatMap(string path)
    {
        using var reader = new StreamReader(path);
        var yaml = new YamlStream();
        yaml.Load(reader);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (yaml.Documents.Count == 0)
            return map;

        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
            return map;

        Flatten(root, "", map);
        return map;
    }

    private static void Flatten(YamlMappingNode node, string prefix, Dictionary<string, string> map)
    {
        foreach (var child in node.Children)
        {
            var keyNode = child.Key as YamlScalarNode;
            if (keyNode?.Value is null)
                continue;

            var key = string.IsNullOrEmpty(prefix) ? keyNode.Value : prefix + "." + keyNode.Value;
            switch (child.Value)
            {
                case YamlMappingNode nested:
                    Flatten(nested, key, map);
                    break;
                case YamlScalarNode scalar:
                    map[key] = scalar.Value ?? "";
                    break;
                case YamlSequenceNode:
                    // Sequences are not used for UI strings; ignore.
                    break;
            }
        }
    }
}

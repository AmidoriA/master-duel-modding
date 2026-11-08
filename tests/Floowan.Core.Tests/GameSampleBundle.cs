using Floowan.Core.Assets;

namespace Floowan.Core.Tests;

/// <summary>
/// A vanilla (512×512, not over-framed) card illustration bundle from a local Master Duel
/// install, for the integration tests that self-skip without the game. The developer's own
/// install is usually modded, so a hard-coded card can already be 704×1024 OF art; this
/// prefers <see cref="PreferredName"/> and otherwise picks the first vanilla bundle found.
/// </summary>
internal sealed record GameSampleBundle(string Path, string Name)
{
    public const string PreferredName = "000c16e8";

    private const string LocalDataRoot =
        @"D:\Games\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\LocalData\2addcbb5\0000";

    private const int MaxCandidates = 400;

    private static readonly Lazy<GameSampleBundle?> Vanilla = new(FindVanilla);

    /// <summary>Null when Master Duel is not installed or no vanilla bundle was found.</summary>
    public static GameSampleBundle? TryGetVanilla() => Vanilla.Value;

    private static GameSampleBundle? FindVanilla()
    {
        if (!Directory.Exists(LocalDataRoot))
            return null;

        var preferred = System.IO.Path.Combine(LocalDataRoot, PreferredName[..2], PreferredName);
        var candidates = new[] { preferred }.Concat(
            Directory.EnumerateDirectories(LocalDataRoot)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .SelectMany(d => Directory.EnumerateFiles(d).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                .Where(f => System.IO.Path.GetFileName(f).Length == 8));

        using var service = new CardArtBundleService(ClassDataLocator.FindClassDataPath());
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxCandidates))
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var info = service.ReadTextureInfo(path);
                if (info.Width == 512 && info.Height == 512)
                    return new GameSampleBundle(path, System.IO.Path.GetFileName(path));
            }
            catch
            {
                // Not a single-texture illustration bundle — try the next one.
            }
        }

        return null;
    }
}

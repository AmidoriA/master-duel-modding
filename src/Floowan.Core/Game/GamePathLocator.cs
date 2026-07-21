using Microsoft.Win32;

namespace Floowan.Core.Game;

/// <summary>
/// Locates Yu-Gi-Oh! Master Duel LocalData player directories.
/// Path layout matches the approach documented by Floowandereeze and Modding.
/// </summary>
public static class GamePathLocator
{
    public const string MasterDuelDirectoryName = "Yu-Gi-Oh!  Master Duel";
    public const string DefaultPlayerDirectory = "00000000";
    public const string UnityDataFile = "data.unity3d";

    public static IReadOnlyList<string> FindSteamMasterDuelPaths()
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var libraryPath in GetSteamLibraryPaths())
        {
            var localData = Path.Combine(
                libraryPath, "steamapps", "common", MasterDuelDirectoryName, "LocalData");
            if (!Directory.Exists(localData))
                continue;

            foreach (var playerDir in Directory.GetDirectories(localData).OrderBy(Path.GetFileName))
            {
                var playerId = Path.GetFileName(playerDir);
                if (string.Equals(playerId, DefaultPlayerDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsValidGamePath(playerDir, out _))
                    continue;

                var key = Path.GetFullPath(playerDir);
                if (seen.Add(key))
                    results.Add(playerDir);
            }
        }

        return results;
    }

    public static bool IsValidGamePath(string? playerDataPath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(playerDataPath))
        {
            error = "Game path is empty.";
            return false;
        }

        try
        {
            var unityPath = ResolveUnity3dPath(playerDataPath);
            if (!File.Exists(unityPath))
            {
                error = "Could not locate masterduel_Data/data.unity3d from the selected LocalData path.";
                return false;
            }

            var assetRoot = Path.Combine(playerDataPath, "0000");
            if (!Directory.Exists(assetRoot))
            {
                error = "Selected LocalData folder is missing the 0000 asset directory.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Floowandereeze resolves unity3d as game_path[:-18] / masterduel_Data / data.unity3d
    /// because LocalData/&lt;playerid&gt; is nested under the game install.
    /// </summary>
    public static string ResolveUnity3dPath(string playerDataPath)
    {
        var installRoot = ResolveInstallRoot(playerDataPath);
        return Path.Combine(installRoot, "masterduel_Data", UnityDataFile);
    }

    public static string ResolveInstallRoot(string playerDataPath)
    {
        // Typical: <install>/LocalData/<playerid>
        var localData = Directory.GetParent(playerDataPath)?.FullName
            ?? throw new InvalidOperationException("Invalid player data path.");
        var install = Directory.GetParent(localData)?.FullName
            ?? throw new InvalidOperationException("Invalid LocalData parent.");
        return install;
    }

    public static IReadOnlyList<string> GetSteamLibraryPaths()
    {
        var libraries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamPath in GetSteamInstallPaths())
        {
            AddUnique(libraries, seen, steamPath);

            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;

            try
            {
                var contents = File.ReadAllText(vdf);
                foreach (System.Text.RegularExpressions.Match match in
                         System.Text.RegularExpressions.Regex.Matches(contents, "\"path\"\\s+\"([^\"]+)\""))
                {
                    var path = match.Groups[1].Value.Replace("\\\\", "\\");
                    AddUnique(libraries, seen, path);
                }

                foreach (System.Text.RegularExpressions.Match match in
                         System.Text.RegularExpressions.Regex.Matches(
                             contents, "^\\s*\"\\d+\"\\s+\"([A-Za-z]:[^\"]+)\"",
                             System.Text.RegularExpressions.RegexOptions.Multiline))
                {
                    AddUnique(libraries, seen, match.Groups[1].Value);
                }
            }
            catch (IOException)
            {
                // ignore unreadable VDF
            }
        }

        return libraries;
    }

    private static IEnumerable<string> GetSteamInstallPaths()
    {
        var paths = new List<string>();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key is not null)
            {
                foreach (var name in new[] { "SteamPath", "InstallPath" })
                {
                    if (key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
                        paths.Add(value);
                }
            }
        }
        catch (System.Security.SecurityException)
        {
            // ignore
        }

        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var root = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(root))
                paths.Add(Path.Combine(root, "Steam"));
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddUnique(List<string> list, HashSet<string> seen, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var full = Path.GetFullPath(path);
        if (seen.Add(full))
            list.Add(full);
    }
}

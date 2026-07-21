namespace Floowan.Core.Assets;

public static class ClassDataLocator
{
    public static string FindClassDataPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "classdata.tpk"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "classdata.tpk"),
        };

        // Walk up from base dir / cwd for development runs
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                candidates.Add(Path.Combine(dir.FullName, "classdata.tpk"));
                candidates.Add(Path.Combine(dir.FullName, "Resources", "classdata.tpk"));
                candidates.Add(Path.Combine(dir.FullName, "src", "Floowan.Core", "Resources", "classdata.tpk"));
            }
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(
            "Missing classdata.tpk (Unity type tree package required by AssetsTools.NET). " +
            "It should be copied next to the app as classdata.tpk.");
    }
}

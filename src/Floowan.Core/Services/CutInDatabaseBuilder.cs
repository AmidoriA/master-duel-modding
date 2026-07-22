using Floowan.Core.Data;
using Floowan.Core.Game;
using Floowan.Core.Models;
using Floowan.Core.Spine;

namespace Floowan.Core.Services;

public sealed class CutInDatabaseBuildResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int Discovered { get; init; }
    public int Complete { get; init; }
    public string? IndexPath { get; init; }
    public string? DatabasePath { get; init; }

    public static CutInDatabaseBuildResult Ok(
        string message,
        int discovered,
        int complete,
        string indexPath,
        string databasePath) =>
        new()
        {
            Success = true,
            Message = message,
            Discovered = discovered,
            Complete = complete,
            IndexPath = indexPath,
            DatabasePath = databasePath
        };

    public static CutInDatabaseBuildResult Fail(string message) =>
        new() { Success = false, Message = message };
}

/// <summary>
/// Scans Master Duel LocalData for every <c>P####</c> cut-in, writes
/// <c>cutin-index.json</c>, and replaces rows in a dedicated <c>cutin.db</c> file.
/// </summary>
public sealed class CutInDatabaseBuilder : IDisposable
{
    private readonly CutInAssetLocator _locator;
    private readonly CutInCatalog _embeddedCatalog;

    public CutInDatabaseBuilder(string? classDataPath = null, string? indexDirectory = null)
    {
        _locator = new CutInAssetLocator(classDataPath, indexDirectory);
        _embeddedCatalog = CutInCatalog.LoadEmbedded();
    }

    public string IndexPath => _locator.IndexPath;

    public CutInDatabaseBuildResult Build(
        string playerDataPath,
        CutInDatabase cutInDatabase,
        CardDatabase? cardDatabase = null,
        IProgress<string>? progress = null)
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return CutInDatabaseBuildResult.Fail(pathError ?? "Invalid game path.");

        progress?.Report("Discovering all cut-in assets under LocalData…");
        var sets = _locator.DiscoverAll(playerDataPath, progress);
        var now = DateTime.UtcNow.ToString("o");
        var rows = new List<CutInRecord>(sets.Count);

        foreach (var set in sets)
        {
            string? name = null;
            if (_embeddedCatalog.TryGetName(set.CutInId, out var catalogName))
                name = catalogName;
            name ??= cardDatabase?.ResolveCutInName(set.CutInId);

            rows.Add(new CutInRecord
            {
                CutInId = set.CutInId,
                Name = name,
                IsComplete = set.IsComplete,
                TextureName = set.Texture?.AssetName,
                TextureBundlePath = set.Texture?.BundlePath,
                AtlasName = set.Atlas?.AssetName,
                AtlasBundlePath = set.Atlas?.BundlePath,
                SkeletonName = set.Skeleton?.AssetName,
                SkeletonBundlePath = set.Skeleton?.BundlePath,
                UpdatedAtUtc = now
            });
        }

        progress?.Report($"Writing {rows.Count} cut_in row(s) to {cutInDatabase.DatabasePath}…");
        cutInDatabase.ReplaceAll(rows);

        var complete = rows.Count(r => r.IsComplete);
        return CutInDatabaseBuildResult.Ok(
            $"Cut-in database built: {rows.Count} ID(s) discovered, {complete} complete → {Path.GetFileName(cutInDatabase.DatabasePath)}.",
            rows.Count,
            complete,
            _locator.IndexPath,
            cutInDatabase.DatabasePath);
    }

    public void Dispose() => _locator.Dispose();
}

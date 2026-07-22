using Floowan.Core.Assets;
using Floowan.Core.Backup;
using Floowan.Core.Data;
using Floowan.Core.Game;
using Floowan.Core.Imaging;
using Floowan.Core.Models;
using Floowan.Core.Spine;

namespace Floowan.Core.Services;

/// <summary>
/// Generates a simple bob cut-in from a PNG (optional ONNX subject crop) and
/// overwrites an existing Master Duel summon cut-in's texture + atlas + skeleton.
/// </summary>
public sealed class CardCutInModService : IDisposable
{
    private readonly CutInCatalog _catalog;
    private readonly CutInAssetLocator _locator;
    private readonly CutInBundleService _bundleService;
    private readonly BackupService _backupService;
    private readonly ISpineCutInGenerator _generator;
    private readonly AutoOverFrameArtService _subjectCrop;
    private CutInDatabase? _cutInDatabase;

    public CardCutInModService(
        string? classDataPath = null,
        string? backupRoot = null,
        string? modelDirectory = null,
        CutInCatalog? catalog = null,
        ISpineCutInGenerator? generator = null)
    {
        _catalog = catalog ?? CutInCatalog.LoadEmbedded();
        var indexDir = backupRoot is null
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(backupRoot));
        _locator = new CutInAssetLocator(classDataPath, indexDir);
        _bundleService = new CutInBundleService(classDataPath);
        _backupService = new BackupService(backupRoot);
        _generator = generator ?? new SimpleBobSpineGenerator();
        _subjectCrop = new AutoOverFrameArtService(modelDirectory);
    }

    public CutInCatalog Catalog => _catalog;
    public BackupService Backups => _backupService;
    public CutInAssetLocator Locator => _locator;
    public CutInDatabase? CutInDatabase => _cutInDatabase;

    public void AttachCutInDatabase(CutInDatabase? database) => _cutInDatabase = database;

    public bool TryResolveCutInId(CardRecord card, out int cutInId)
    {
        if (card.ArtId is int artId && artId > 0)
        {
            if (_cutInDatabase?.HasCutIn(artId) == true || _catalog.HasCutInId(artId))
            {
                cutInId = artId;
                return true;
            }
        }

        if (_catalog.TryResolveByCardName(card.DisplayName, out cutInId)
            || _catalog.TryResolveByCardName(card.Name, out cutInId))
        {
            return true;
        }

        cutInId = 0;
        return false;
    }

    public bool HasKnownCutIn(CardRecord card) =>
        TryResolveCutInId(card, out var id)
        && (_cutInDatabase?.HasCutIn(id) == true || _catalog.HasCutInId(id));

    public async Task<CutInReplacementResult> ApplySimpleBobAsync(
        string playerDataPath,
        CardRecord card,
        string sourceImagePath,
        int? cutInIdOverride = null,
        bool cropSubject = true,
        bool createBackup = true,
        SpineCutInOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return CutInReplacementResult.Fail(pathError ?? "Invalid game path.");
        if (!File.Exists(sourceImagePath))
            return CutInReplacementResult.Fail("Source image was not found.");

        int cutInId;
        if (cutInIdOverride is int forced && forced > 0)
        {
            cutInId = forced;
            if (!_catalog.HasCutInId(cutInId))
                progress?.Report($"Using manual cut-in ID {cutInId} (not in bundled catalog).");
        }
        else if (!TryResolveCutInId(card, out cutInId))
        {
            return CutInReplacementResult.Fail(
                $"'{card.DisplayName}' has no known summon cut-in. Only cards that already ship a cut-in can be animated in v1.");
        }

        progress?.Report($"Resolving cut-in assets for P{cutInId}…");
        CutInAssetSet targets;
        try
        {
            targets = await Task.Run(
                () => _locator.Find(playerDataPath, cutInId, progress),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CutInReplacementResult.Fail($"Could not locate cut-in assets: {ex.Message}");
        }

        if (!targets.IsComplete)
        {
            return CutInReplacementResult.Fail(
                $"Could not find a complete cut-in set for P{cutInId} under LocalData " +
                $"(texture={(targets.Texture is null ? "missing" : "ok")}, " +
                $"atlas={(targets.Atlas is null ? "missing" : "ok")}, " +
                $"skeleton={(targets.Skeleton is null ? "missing" : "ok")}). " +
                "Try Build cut-in index, or confirm this card's cut-in is downloaded.");
        }

        string workingImage = sourceImagePath;
        string? tempCutout = null;
        try
        {
            if (cropSubject)
            {
                tempCutout = Path.Combine(Path.GetTempPath(), $"floowan-cutin-subject-{Guid.NewGuid():N}.png");
                await _subjectCrop.CreateSubjectCutoutAsync(
                    sourceImagePath,
                    tempCutout,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                workingImage = tempCutout;
            }

            progress?.Report("Generating simple bob Spine package…");
            var assetBase = "P" + cutInId;
            var generated = _generator.FromSingleImage(
                workingImage,
                assetBase,
                options,
                textureName: targets.Texture!.AssetName,
                skeletonName: targets.Skeleton!.AssetName,
                atlasName: targets.Atlas!.AssetName);

            var touchedBundles = targets.All
                .Select(h => h.BundlePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (createBackup)
            {
                foreach (var bundlePath in touchedBundles)
                {
                    var bundleId = Path.GetFileName(bundlePath);
                    _backupService.BackupCutInBundleFile(bundlePath, cutInId, bundleId);
                }
            }

            progress?.Report("Injecting cut-in texture + atlas + skeleton…");
            await Task.Run(
                () => _bundleService.Inject(targets, generated),
                cancellationToken).ConfigureAwait(false);

            return CutInReplacementResult.Ok(
                $"Applied simple bob cut-in for '{card.DisplayName}' (P{cutInId}).",
                cutInId,
                touchedBundles);
        }
        catch (Exception ex)
        {
            return CutInReplacementResult.Fail($"Cut-in replace failed: {ex.Message}");
        }
        finally
        {
            try { if (tempCutout is not null && File.Exists(tempCutout)) File.Delete(tempCutout); } catch { /* ignore */ }
        }
    }

    public bool RestoreCutIn(int cutInId)
    {
        var restored = _backupService.TryRestoreAllCutInBundles(cutInId);
        return restored > 0;
    }

    public int BuildIndex(string playerDataPath, IProgress<string>? progress = null) =>
        _locator.BuildIndex(playerDataPath, _catalog.AllIds, progress);

    public void Dispose()
    {
        _locator.Dispose();
        _bundleService.Dispose();
        _subjectCrop.Dispose();
    }
}

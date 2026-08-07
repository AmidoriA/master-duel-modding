using System.IO.Compression;
using System.Text.Json;
using Floowan.Core.Backup;
using Floowan.Core.Data;
using Floowan.Core.Game;
using Floowan.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Services;

/// <summary>
/// Packs / unpacks Floowan over-frame mods as a single <c>.overframes</c> zip for
/// cross-PC transfer. Includes applied 704×1024 canvas when available, and optional
/// <c>of_edit_layer</c> data from <c>user.db</c>.
/// </summary>
public sealed class OverFrameBundleService : IDisposable
{
    public const string FileExtension = ".overframes";
    public const string ManifestFileName = "manifest.json";

    private readonly OverFrameModService _modService;
    private readonly bool _ownsModService;

    public OverFrameBundleService(string? classDataPath = null, string? backupRoot = null)
    {
        _modService = new OverFrameModService(classDataPath, backupRoot);
        _ownsModService = true;
    }

    public OverFrameBundleService(OverFrameModService modService)
    {
        _modService = modService ?? throw new ArgumentNullException(nameof(modService));
        _ownsModService = false;
    }

    public void Dispose()
    {
        if (_ownsModService)
            _modService.Dispose();
    }

    /// <summary>
    /// Lists cards currently in the live <c>of_card_asset</c> gate (Floowan or other mods),
    /// with optional layer / applied-canvas hints from this PC's backups and user.db.
    /// </summary>
    public IReadOnlyList<OverFrameBundleExportItem> ListExportCandidates(
        string playerDataPath,
        CardDatabase database,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowFullScan = false)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (string.IsNullOrWhiteSpace(playerDataPath))
            throw new ArgumentException("Game path is required.", nameof(playerDataPath));

        var gated = _modService.ListGatedOverFrameCards(
            playerDataPath, database, progress, cancellationToken, allowFullScan);

        var items = new List<OverFrameBundleExportItem>(gated.Count);
        foreach (var entry in gated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var card = entry.Card;
            var hasLayer = database.HasOfEditLayer(card.Id);
            // Do not open every AssetBundle while listing — gated cards have live OF art;
            // applied PNG backup is optional (Floowan-only). Packing extracts live art on export.
            var hasBackupCanvas = _modService.Backups.HasAppliedOverFrameBackup(card.Name);

            items.Add(new OverFrameBundleExportItem
            {
                CardId = card.Id,
                Name = card.Name,
                DisplayName = card.DisplayName,
                ArtId = entry.ArtId,
                BaseArtId = entry.BaseArtId,
                HasEditLayer = hasLayer,
                HasAppliedCanvas = true,
                IsFloowanTracked = database.IsFloowanOverframe(card.Id) || hasBackupCanvas
            });
        }

        return items;
    }

    /// <summary>
    /// Writes a <c>.overframes</c> pack for the selected catalog card ids (must be in the live gate
    /// or otherwise resolvable). Cards without an applied/live canvas and without an edit layer are skipped.
    /// </summary>
    public OverFrameBundleExportResult Export(
        string playerDataPath,
        CardDatabase database,
        IReadOnlyCollection<int> cardIds,
        string outputPath,
        string? exporterVersion = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowFullScan = false)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(cardIds);
        if (string.IsNullOrWhiteSpace(outputPath))
            return OverFrameBundleExportResult.Fail("Output path is required.");

        if (!outputPath.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            outputPath += FileExtension;

        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return OverFrameBundleExportResult.Fail(pathError ?? "Invalid game path.");

        var idSet = cardIds.ToHashSet();
        if (idSet.Count == 0)
            return OverFrameBundleExportResult.Fail("Select at least one over-frame card to export.");

        Dictionary<int, (int ArtId, int BaseArtId)> gateMeta;
        try
        {
            var gated = _modService.ListGatedOverFrameCards(
                playerDataPath, database, progress, cancellationToken, allowFullScan);
            gateMeta = gated.ToDictionary(g => g.Card.Id, g => (g.ArtId, g.BaseArtId));
        }
        catch (Exception ex)
        {
            return OverFrameBundleExportResult.Fail(ex.Message);
        }

        var cards = new List<CardRecord>();
        foreach (var id in idSet)
        {
            var card = database.GetById(id);
            if (card is null)
                continue;
            cards.Add(card);
        }

        if (cards.Count == 0)
            return OverFrameBundleExportResult.Fail("None of the selected cards were found in the catalog.");

        var staging = Path.Combine(Path.GetTempPath(), "floowan-of-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var exported = 0;
        var skipped = 0;

        try
        {
            var manifest = new OverFrameBundleManifest
            {
                FormatVersion = OverFrameBundleManifest.CurrentFormatVersion,
                ExportedAtUtc = DateTime.UtcNow.ToString("o"),
                ExporterVersion = exporterVersion
            };

            foreach (var card in cards.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Packing {card.DisplayName}…");

                int? artId = card.ArtId;
                int? baseArtId = card.OverframeBaseId;
                if (gateMeta.TryGetValue(card.Id, out var meta))
                {
                    artId = meta.ArtId;
                    baseArtId = meta.BaseArtId;
                }

                var entry = TryPackCard(playerDataPath, database, card, staging, progress, artId, baseArtId);
                if (entry is null)
                {
                    skipped++;
                    continue;
                }

                manifest.Cards.Add(entry);
                exported++;
            }

            if (exported == 0)
            {
                return OverFrameBundleExportResult.Fail(
                    "Nothing to export — selected cards have no applied over-frame canvas and no edit layers.");
            }

            var manifestPath = Path.Combine(staging, ManifestFileName);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, OverFrameBundleJson.Options));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            progress?.Report("Writing .overframes archive…");
            ZipFile.CreateFromDirectory(staging, outputPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return OverFrameBundleExportResult.Ok(outputPath, exported, skipped);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OverFrameBundleExportResult.Fail("Export failed: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Reads the manifest from a <c>.overframes</c> file without applying anything.
    /// </summary>
    public OverFrameBundleManifest ReadManifest(string bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            throw new FileNotFoundException("Over-frame bundle not found.", bundlePath);

        using var zip = ZipFile.OpenRead(bundlePath);
        var entry = zip.GetEntry(ManifestFileName)
            ?? zip.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Bundle is missing manifest.json.");

        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<OverFrameBundleManifest>(stream, OverFrameBundleJson.Options)
            ?? throw new InvalidOperationException("Could not parse manifest.json.");
        if (manifest.FormatVersion <= 0 || manifest.FormatVersion > OverFrameBundleManifest.CurrentFormatVersion)
            throw new InvalidOperationException($"Unsupported .overframes format version {manifest.FormatVersion}.");
        return manifest;
    }

    /// <summary>
    /// Imports selected cards from a <c>.overframes</c> pack into the live game + user.db.
    /// Texture+gate writes always go through <see cref="OverFrameModService.ApplyOverFrame"/>
    /// (same path as create-new overframe), which performs the standard backup mechanic.
    /// Layer-only entries use <see cref="OverFrameModService.EnableGateOnly"/> with the same
    /// pre-write backups. <paramref name="createBackup"/> is always treated as true.
    /// </summary>
    public OverFrameBundleImportResult Import(
        string playerDataPath,
        CardDatabase database,
        string bundlePath,
        IReadOnlyCollection<int> cardIds,
        bool createBackup,
        string packer = "lz4",
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(cardIds);

        // Match create-new OF / Custom Apply: never skip backups on Import.
        createBackup = true;

        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return OverFrameBundleImportResult.Fail(pathError ?? "Invalid game path.");

        if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            return OverFrameBundleImportResult.Fail("Over-frame bundle not found.");

        var idSet = cardIds.ToHashSet();
        if (idSet.Count == 0)
            return OverFrameBundleImportResult.Fail("Select at least one card to import.");

        OverFrameBundleManifest manifest;
        try
        {
            manifest = ReadManifest(bundlePath);
        }
        catch (Exception ex)
        {
            return OverFrameBundleImportResult.Fail(ex.Message);
        }

        var selected = manifest.Cards.Where(c => idSet.Contains(c.CardId)).ToList();
        if (selected.Count == 0)
            return OverFrameBundleImportResult.Fail("None of the selected cards are in this bundle.");

        var extractRoot = Path.Combine(Path.GetTempPath(), "floowan-of-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractRoot);
        var imported = 0;
        var failed = 0;
        var skipped = 0;

        try
        {
            progress?.Report("Extracting .overframes archive…");
            ZipFile.ExtractToDirectory(bundlePath, extractRoot);

            foreach (var entry in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Importing {entry.Name}…");

                var card = database.GetById(entry.CardId);
                if (card is null)
                {
                    progress?.Report($"Skip {entry.Name}: card id {entry.CardId} not in catalog.");
                    skipped++;
                    continue;
                }

                try
                {
                    ImportOne(playerDataPath, database, card, entry, extractRoot, packer, progress);
                    imported++;
                }
                catch (Exception ex)
                {
                    failed++;
                    progress?.Report($"Failed {entry.Name}: {ex.Message}");
                }
            }

            if (imported == 0 && failed == 0)
                return OverFrameBundleImportResult.Fail("No cards were imported.");

            return OverFrameBundleImportResult.Ok(imported, failed, skipped);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OverFrameBundleImportResult.Fail("Import failed: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(extractRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    private OverFrameBundleCardEntry? TryPackCard(
        string playerDataPath,
        CardDatabase database,
        CardRecord card,
        string stagingRoot,
        IProgress<string>? progress,
        int? gateArtId = null,
        int? gateBaseArtId = null)
    {
        var cardDirRel = Path.Combine("cards", card.Id.ToString());
        var cardDir = Path.Combine(stagingRoot, cardDirRel);
        Directory.CreateDirectory(cardDir);

        string? appliedRel = null;
        var appliedAbs = Path.Combine(cardDir, "applied.png");
        if (_modService.TryExportCurrentOverFrameCanvas(playerDataPath, card, appliedAbs)
            && File.Exists(appliedAbs))
        {
            appliedRel = Path.Combine(cardDirRel, "applied.png").Replace('\\', '/');
        }
        else
        {
            try { if (File.Exists(appliedAbs)) File.Delete(appliedAbs); } catch { /* ignore */ }
        }

        OverFrameBundleLayerEntry? layerEntry = null;
        var hasLayer = false;
        if (database.TryLoadOfEditLayer(card.Id, out var state, out var subject, out var mask, out var background))
        {
            try
            {
                string? subjectRel = null, maskRel = null, bgRel = null;
                if (subject is not null && mask is not null)
                {
                    var subjectAbs = Path.Combine(cardDir, "subject.png");
                    var maskAbs = Path.Combine(cardDir, "subject-mask.png");
                    subject.SaveAsPng(subjectAbs);
                    mask.SaveAsPng(maskAbs);
                    subjectRel = Path.Combine(cardDirRel, "subject.png").Replace('\\', '/');
                    maskRel = Path.Combine(cardDirRel, "subject-mask.png").Replace('\\', '/');
                }

                if (background is not null)
                {
                    var bgAbs = Path.Combine(cardDir, "background.png");
                    background.SaveAsPng(bgAbs);
                    bgRel = Path.Combine(cardDirRel, "background.png").Replace('\\', '/');
                }

                layerEntry = OverFrameBundleLayerEntry.FromStage(state, subjectRel, maskRel, bgRel);
                hasLayer = true;
            }
            finally
            {
                subject?.Dispose();
                mask?.Dispose();
                background?.Dispose();
            }
        }

        if (appliedRel is null && !hasLayer)
        {
            progress?.Report($"Skip {card.DisplayName}: no applied canvas and no edit layer.");
            try { Directory.Delete(cardDir, recursive: true); } catch { /* ignore */ }
            return null;
        }

        return new OverFrameBundleCardEntry
        {
            CardId = card.Id,
            Name = card.Name,
            Bundle = card.Bundle,
            ArtId = gateArtId ?? card.ArtId,
            OverframeBaseId = gateBaseArtId ?? card.OverframeBaseId,
            AppliedAtUtc = null,
            AppliedPng = appliedRel,
            HasEditLayer = hasLayer,
            EditLayer = layerEntry
        };
    }

    /// <summary>
    /// Unpacks one manifest entry, then applies it with the same Core APIs as create-new OF:
    /// <see cref="OverFrameModService.ApplyOverFrame"/> when an applied canvas is present,
    /// otherwise <see cref="OverFrameModService.EnableGateOnly"/>. Both perform shared backups.
    /// </summary>
    private void ImportOne(
        string playerDataPath,
        CardDatabase database,
        CardRecord card,
        OverFrameBundleCardEntry entry,
        string extractRoot,
        string packer,
        IProgress<string>? progress)
    {
        if (entry.ArtId is int artId && artId > 0)
        {
            database.SetArtId(card.Id, artId);
            card = database.GetById(card.Id) ?? card;
        }

        if (!string.IsNullOrWhiteSpace(entry.AppliedPng))
        {
            var appliedPath = ResolvePackPath(extractRoot, entry.AppliedPng);
            if (!File.Exists(appliedPath))
                throw new FileNotFoundException("Missing applied.png in pack.", appliedPath);

            // Same function + backup mechanic as Auto-create / Custom overframe Apply.
            var apply = _modService.ApplyOverFrame(
                playerDataPath,
                card,
                appliedPath,
                createBackup: true,
                database,
                packer);
            if (!apply.Success)
                throw new InvalidOperationException(apply.Message);
        }
        else
        {
            // Layer-only pack: register gate so the card is treated as OF; texture unchanged.
            progress?.Report($"{card.DisplayName}: no applied canvas — enabling gate only.");
            var gate = _modService.EnableGateOnly(
                playerDataPath,
                card,
                createBackup: true,
                database,
                packer);
            if (!gate.Success)
                throw new InvalidOperationException(gate.Message);
        }

        if (entry.HasEditLayer && entry.EditLayer is not null)
            RestoreEditLayer(database, card.Id, entry.EditLayer, extractRoot);
    }

    private static void RestoreEditLayer(
        CardDatabase database,
        int cardId,
        OverFrameBundleLayerEntry layer,
        string extractRoot)
    {
        Image<Rgba32>? subject = null;
        Image<L8>? mask = null;
        Image<Rgba32>? background = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(layer.SubjectPng) && !string.IsNullOrWhiteSpace(layer.SubjectMaskPng))
            {
                subject = Image.Load<Rgba32>(ResolvePackPath(extractRoot, layer.SubjectPng));
                mask = Image.Load<L8>(ResolvePackPath(extractRoot, layer.SubjectMaskPng));
            }

            if (!string.IsNullOrWhiteSpace(layer.BackgroundPng))
                background = Image.Load<Rgba32>(ResolvePackPath(extractRoot, layer.BackgroundPng));

            if (subject is null && mask is null && background is null)
                return;

            database.SaveOfEditLayer(cardId, layer.ToStageState(), subject, mask, background);
        }
        finally
        {
            subject?.Dispose();
            mask?.Dispose();
            background?.Dispose();
        }
    }

    private static string ResolvePackPath(string extractRoot, string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(extractRoot, normalized));
        var rootFull = Path.GetFullPath(extractRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                rootFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid path in .overframes pack.");
        }

        return full;
    }
}

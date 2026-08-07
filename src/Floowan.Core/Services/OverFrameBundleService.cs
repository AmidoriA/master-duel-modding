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
    /// Lists Floowan-applied over-frame cards eligible for export (with layer / canvas hints).
    /// </summary>
    public IReadOnlyList<OverFrameBundleExportItem> ListExportCandidates(
        CardDatabase database,
        string? playerDataPath = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        var cards = database.ListFloowanOverframeCards();
        var items = new List<OverFrameBundleExportItem>(cards.Count);
        foreach (var card in cards)
        {
            var hasLayer = database.HasOfEditLayer(card.Id);
            var hasCanvas = _modService.Backups.HasAppliedOverFrameBackup(card.Name);
            if (!hasCanvas && !string.IsNullOrWhiteSpace(playerDataPath))
            {
                // Live OF texture counts as exportable canvas even without a local applied PNG.
                try
                {
                    var temp = Path.Combine(Path.GetTempPath(), "floowan-of-export-probe-" + Guid.NewGuid().ToString("N") + ".png");
                    try
                    {
                        hasCanvas = _modService.TryExportCurrentOverFrameCanvas(playerDataPath, card, temp);
                    }
                    finally
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
                    }
                }
                catch
                {
                    hasCanvas = false;
                }
            }

            items.Add(new OverFrameBundleExportItem
            {
                CardId = card.Id,
                Name = card.Name,
                DisplayName = card.DisplayName,
                HasEditLayer = hasLayer,
                HasAppliedCanvas = hasCanvas
            });
        }

        return items;
    }

    /// <summary>
    /// Writes a <c>.overframes</c> pack for the selected Floowan card ids.
    /// Cards without an applied/live canvas and without an edit layer are skipped.
    /// </summary>
    public OverFrameBundleExportResult Export(
        string playerDataPath,
        CardDatabase database,
        IReadOnlyCollection<int> cardIds,
        string outputPath,
        string? exporterVersion = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
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

        var cards = database.ListFloowanOverframeCards()
            .Where(c => idSet.Contains(c.Id))
            .ToList();
        if (cards.Count == 0)
            return OverFrameBundleExportResult.Fail("None of the selected cards are Floowan over-frames.");

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

            foreach (var card in cards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Packing {card.DisplayName}…");

                var entry = TryPackCard(playerDataPath, database, card, staging, progress);
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
                    ImportOne(playerDataPath, database, card, entry, extractRoot, createBackup, packer, progress);
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
        IProgress<string>? progress)
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
            ArtId = card.ArtId,
            OverframeBaseId = card.OverframeBaseId,
            AppliedAtUtc = null,
            AppliedPng = appliedRel,
            HasEditLayer = hasLayer,
            EditLayer = layerEntry
        };
    }

    private void ImportOne(
        string playerDataPath,
        CardDatabase database,
        CardRecord card,
        OverFrameBundleCardEntry entry,
        string extractRoot,
        bool createBackup,
        string packer,
        IProgress<string>? progress)
    {
        if (entry.ArtId is int artId && artId > 0)
            database.SetArtId(card.Id, artId);

        if (!string.IsNullOrWhiteSpace(entry.AppliedPng))
        {
            var appliedPath = ResolvePackPath(extractRoot, entry.AppliedPng);
            if (!File.Exists(appliedPath))
                throw new FileNotFoundException("Missing applied.png in pack.", appliedPath);

            var apply = _modService.ApplyOverFrame(
                playerDataPath,
                card,
                appliedPath,
                createBackup,
                database,
                packer);
            if (!apply.Success)
                throw new InvalidOperationException(apply.Message);
        }
        else
        {
            // Layer-only pack: register gate so the card is treated as OF; texture unchanged.
            progress?.Report($"{card.DisplayName}: no applied canvas — enabling gate only.");
            var gate = _modService.EnableGateOnly(playerDataPath, card, createBackup, database, packer);
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

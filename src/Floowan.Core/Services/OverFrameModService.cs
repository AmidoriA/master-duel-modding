using Floowan.Core.Assets;
using Floowan.Core.Backup;
using Floowan.Core.Data;
using Floowan.Core.Game;
using Floowan.Core.Imaging;
using Floowan.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Services;

/// <summary>
/// Applies Master Duel over-frame mods: 704×1024 texture replace + of_card_asset gate registration.
/// </summary>
public sealed class OverFrameModService : IDisposable
{
    private readonly CardArtBundleService _bundleService;
    private readonly TextAssetBundleService _textAssets;
    private readonly OfCardAssetLocator _locator;
    private readonly BackupService _backupService;

    public OverFrameModService(string? classDataPath = null, string? backupRoot = null)
    {
        _bundleService = new CardArtBundleService(classDataPath);
        _textAssets = new TextAssetBundleService(classDataPath);
        _locator = new OfCardAssetLocator(_textAssets);
        _backupService = new BackupService(backupRoot);
    }

    public BackupService Backups => _backupService;
    public OfCardAssetLocator Locator => _locator;

    public OfCardAssetLocateResult EnsureGateLocated(
        string playerDataPath,
        CardDatabase? database = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowFullScan = false) =>
        _locator.Locate(playerDataPath, database, progress, cancellationToken, allowFullScan);

    public OverFrameResult ApplyOverFrame(
        string playerDataPath,
        CardRecord card,
        string replacementImagePath,
        bool createBackup,
        CardDatabase? database = null,
        string packer = "lz4")
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return OverFrameResult.Fail(pathError ?? "Invalid game path.");

        var validation = ImagePreparation.Validate(
            replacementImagePath,
            OverFrameConstants.Width,
            OverFrameConstants.Height);
        if (!validation.IsValid)
            return OverFrameResult.Fail(validation.Error ?? "Invalid image.");

        string cardBundlePath;
        try
        {
            cardBundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
        }
        catch (Exception ex)
        {
            return OverFrameResult.Fail(ex.Message);
        }

        var gateLocate = _locator.Locate(playerDataPath, database);
        if (!gateLocate.Success || gateLocate.BundlePath is null || gateLocate.BundleId is null)
            return OverFrameResult.Fail(gateLocate.Message);

        string? cardBackup = null;
        byte[]? gateRollbackBytes = null;
        try
        {
            PrepareOverFrameWriteBackups(
                card,
                cardBundlePath,
                gateLocate,
                createBackup,
                database,
                out cardBackup);

            // Per-apply rollback snapshot of the live gate (includes all prior OF registrations).
            gateRollbackBytes = _textAssets.ReadTextAssetBytes(gateLocate.BundlePath);

            _bundleService.ReplaceTexture(cardBundlePath, replacementImagePath, TextureReplaceOptions.OverFrame with
            {
                Compression = packer
            });

            var gate = OfCardAssetGate.Parse(gateRollbackBytes);
            var artId = ResolveAndCacheArtId(playerDataPath, card, database);
            var baseArtId = ResolveGateBaseArtId(gate, artId);
            TryRemoveLegacyPkGateEntry(gate, card.Id, artId, database);
            gate.Add(artId, baseArtId);
            _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes(), compression: packer);

            // Re-open from disk so a failed CAB rewrite cannot report false success.
            var info = _bundleService.ReadTextureInfo(cardBundlePath);
            if (info.Width != OverFrameConstants.Width || info.Height != OverFrameConstants.Height)
            {
                throw new InvalidOperationException(
                    $"Texture rewrite verification failed: got {info.Width}x{info.Height}, expected {OverFrameConstants.Width}x{OverFrameConstants.Height}.");
            }

            var verifyBytes = _textAssets.ReadTextAssetBytes(gateLocate.BundlePath);
            var verifyGate = OfCardAssetGate.Parse(verifyBytes);
            if (!verifyGate.Contains(artId))
            {
                throw new InvalidOperationException(
                    "of_card_asset rewrite verification failed: art id was not present after save. " +
                    "Without a working gate entry the game keeps the art inside the frame.");
            }

            database?.SetFloowanOverframe(
                card.Id,
                applied: true,
                overframeBaseId: baseArtId,
                bundleId: card.Bundle);

            try
            {
                _backupService.SaveAppliedOverFramePng(card.Name, replacementImagePath);
            }
            catch
            {
                /* best-effort canvas snapshot for post-patch restore */
            }

            var msg =
                $"Applied over-frame for '{card.DisplayName}' ({info.Width}x{info.Height}, RGBA32) and registered gate ({artId},{baseArtId}). " +
                "Fully quit and restart Master Duel so it reloads LocalData.";
            if (!string.IsNullOrEmpty(validation.Warning))
                msg += " " + validation.Warning;
            return OverFrameResult.Ok(msg, cardBundlePath, gateLocate.BundlePath, inGate: true);
        }
        catch (Exception ex)
        {
            if (cardBackup is not null)
            {
                try { _backupService.TryRestoreBundleFile(cardBundlePath, card.Bundle); } catch { /* best effort */ }
            }

            if (gateRollbackBytes is not null && gateLocate.BundlePath is not null)
            {
                try
                {
                    _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gateRollbackBytes, compression: packer);
                }
                catch { /* best effort */ }
            }

            return OverFrameResult.Fail($"Over-frame apply failed: {ex.Message}");
        }
    }

    public OverFrameResult EnableGateOnly(
        string playerDataPath,
        CardRecord card,
        bool createBackup,
        CardDatabase? database = null,
        string packer = "lz4")
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return OverFrameResult.Fail(pathError ?? "Invalid game path.");

        var gateLocate = _locator.Locate(playerDataPath, database);
        if (!gateLocate.Success || gateLocate.BundlePath is null || gateLocate.BundleId is null)
            return OverFrameResult.Fail(gateLocate.Message);

        string? cardBundlePath = null;
        try
        {
            cardBundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
        }
        catch
        {
            /* Gate-only can still proceed if the illustration bundle is temporarily missing. */
        }

        try
        {
            // Same pre-write backup mechanic as ApplyOverFrame / create-new OF.
            PrepareOverFrameWriteBackups(
                card,
                cardBundlePath,
                gateLocate,
                createBackup,
                database,
                out _);

            var gateBytes = _textAssets.ReadTextAssetBytes(gateLocate.BundlePath);
            var gate = OfCardAssetGate.Parse(gateBytes);
            var artId = ResolveAndCacheArtId(playerDataPath, card, database);
            var baseArtId = ResolveGateBaseArtId(gate, artId);
            TryRemoveLegacyPkGateEntry(gate, card.Id, artId, database);
            gate.Add(artId, baseArtId);
            _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes(), compression: packer);

            var verify = OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
            if (!verify.Contains(artId))
            {
                return OverFrameResult.Fail(
                    "of_card_asset rewrite verification failed: art id was not present after save.");
            }

            database?.SetFloowanOverframe(
                card.Id,
                applied: true,
                overframeBaseId: baseArtId,
                bundleId: card.Bundle);

            TrySnapshotLiveAppliedOverFrame(playerDataPath, card);

            return OverFrameResult.Ok(
                $"Enabled over-frame gate for '{card.DisplayName}' ({artId},{baseArtId}) without changing texture. " +
                "Fully quit and restart Master Duel so it reloads LocalData.",
                gateBundlePath: gateLocate.BundlePath,
                inGate: true);
        }
        catch (Exception ex)
        {
            return OverFrameResult.Fail($"Enable gate failed: {ex.Message}");
        }
    }

    public OverFrameResult RemoveFromGate(
        string playerDataPath,
        CardRecord card,
        bool createBackup,
        CardDatabase? database = null,
        string packer = "lz4")
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return OverFrameResult.Fail(pathError ?? "Invalid game path.");

        var gateLocate = _locator.Locate(playerDataPath, database);
        if (!gateLocate.Success || gateLocate.BundlePath is null || gateLocate.BundleId is null)
            return OverFrameResult.Fail(gateLocate.Message);

        try
        {
            if (createBackup)
                _backupService.BackupGateBundleFile(gateLocate.BundlePath, gateLocate.BundleId);

            var gateBytes = _textAssets.ReadTextAssetBytes(gateLocate.BundlePath);
            var gate = OfCardAssetGate.Parse(gateBytes);

            var removed = false;
            int? artId = null;
            try
            {
                artId = ResolveAndCacheArtId(playerDataPath, card, database);
                removed = gate.Remove(artId.Value);
            }
            catch
            {
                // Card bundle may be missing; still try legacy PK cleanup below.
            }

            // Clear mistaken Floowandereeze-PK entries from older builds — never delete
            // another card's real art-id trigger that happens to equal this PK.
            if (TryRemoveLegacyPkGateEntry(gate, card.Id, artId, database))
                removed = true;

            if (removed)
                _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes(), compression: packer);

            database?.SetFloowanOverframe(card.Id, applied: false);
            database?.ClearOfEditLayer(card.Id);
            _backupService.TryDeleteAppliedOverFrameBackup(card.Name);
            _backupService.TryDeleteCustomOverframeStage(card.Name);

            var msg = removed
                ? $"Removed '{card.DisplayName}' from of_card_asset gate."
                : $"'{card.DisplayName}' was not present in of_card_asset gate; DB flag cleared.";
            return OverFrameResult.Ok(msg, gateBundlePath: gateLocate.BundlePath, inGate: false);
        }
        catch (Exception ex)
        {
            return OverFrameResult.Fail($"Remove from gate failed: {ex.Message}");
        }
    }

    public OverFrameResult RestoreBackups(
        string playerDataPath,
        CardRecord card,
        CardDatabase? database = null,
        string packer = "lz4")
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return OverFrameResult.Fail(pathError ?? "Invalid game path.");

        var messages = new List<string>();
        try
        {
            var cardPath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
            if (_backupService.TryRestoreBundleFile(cardPath, card.Bundle))
                messages.Add("card bundle");
        }
        catch (Exception ex)
        {
            return OverFrameResult.Fail($"Card restore failed: {ex.Message}");
        }

        // Never restore the shared one-time gate backup here — that snapshot is from before the
        // first OF and would unregister every other over-framed card. Only drop this card's rows.
        var gateLocate = _locator.Locate(playerDataPath, database);
        if (gateLocate.Success && gateLocate.BundlePath is not null)
        {
            try
            {
                var gate = OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
                var removed = false;
                int? artId = null;
                try { artId = ResolveAndCacheArtId(playerDataPath, card, database); }
                catch { /* bundle may already be restored / missing name */ }

                if (artId is int id && gate.Remove(id))
                    removed = true;
                if (TryRemoveLegacyPkGateEntry(gate, card.Id, artId, database))
                    removed = true;

                if (removed)
                {
                    _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes(), compression: packer);
                    messages.Add("gate entry");
                }
            }
            catch (Exception ex)
            {
                return OverFrameResult.Fail($"Gate cleanup failed: {ex.Message}");
            }
        }

        if (messages.Count == 0)
            return OverFrameResult.Fail("No over-frame backups found for this card/gate.");

        database?.SetFloowanOverframe(card.Id, applied: false);
        database?.ClearOfEditLayer(card.Id);
        _backupService.TryDeleteAppliedOverFrameBackup(card.Name);
        _backupService.TryDeleteCustomOverframeStage(card.Name);
        return OverFrameResult.Ok($"Restored {string.Join(" + ", messages)} from backup.");
    }

    /// <summary>
    /// Re-applies Floowan over-frames after an MD patch replaced <c>of_card_asset</c> / art.
    /// Reads Floowan-applied cards from <c>user.db</c>, merges gate entries additively
    /// (official OF rows kept), and writes applied OF canvases from backups when needed.
    /// </summary>
    public OverFrameRestoreBatchResult RestoreOverframesAfterPatch(
        string playerDataPath,
        CardDatabase database,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string packer = "lz4")
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
        {
            return new OverFrameRestoreBatchResult
            {
                Success = false,
                Message = pathError ?? "Invalid game path."
            };
        }

        var cards = database.ListFloowanOverframeCards();
        if (cards.Count == 0)
        {
            return new OverFrameRestoreBatchResult
            {
                Success = true,
                Message = "No modded over-frames recorded in user.db. Apply OF first, or run after a patch that wiped the gate.",
                Total = 0
            };
        }

        progress?.Report($"Locating of_card_asset gate ({cards.Count} modded OF card(s))…");
        var gateLocate = _locator.Locate(playerDataPath, database, progress, cancellationToken);
        if (!gateLocate.Success || gateLocate.BundlePath is null || gateLocate.BundleId is null)
        {
            return new OverFrameRestoreBatchResult
            {
                Success = false,
                Message = gateLocate.Message,
                Total = cards.Count
            };
        }

        // One-time vanilla/disaster gate snapshot; never restore it wholesale during this merge.
        _backupService.BackupGateBundleFile(gateLocate.BundlePath, gateLocate.BundleId);

        OfCardAssetGate gate;
        try
        {
            gate = OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
        }
        catch (Exception ex)
        {
            return new OverFrameRestoreBatchResult
            {
                Success = false,
                Message = "Could not parse live of_card_asset gate: " + ex.Message,
                Total = cards.Count
            };
        }

        var officialCount = gate.Entries.Count;
        var restored = 0;
        var skipped = 0;
        var failed = 0;
        var warnings = new List<string>();
        var gateDirty = false;

        for (var i = 0; i < cards.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var card = cards[i];
            progress?.Report($"Restoring OF {i + 1}/{cards.Count}: {card.DisplayName}…");

            try
            {
                var outcome = RestoreOneOverframeAfterPatch(
                    playerDataPath,
                    card,
                    database,
                    gate,
                    packer,
                    out var warning);

                if (!string.IsNullOrWhiteSpace(warning))
                    warnings.Add(warning);

                switch (outcome)
                {
                    case RestoreOneOutcome.Restored:
                        restored++;
                        gateDirty = true;
                        // Persist gate after each success so a mid-run crash keeps prior merges.
                        _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes(), compression: packer);
                        gateDirty = false;
                        break;
                    case RestoreOneOutcome.Skipped:
                        skipped++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                warnings.Add($"{card.DisplayName}: {ex.Message}");
            }
        }

        if (gateDirty)
        {
            _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes(), compression: packer);
        }

        // Verify gate still parses and still has at least the official entries we started with.
        try
        {
            var verify = OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
            if (verify.Entries.Count < officialCount)
            {
                warnings.Add(
                    $"Gate entry count after restore ({verify.Entries.Count}) is below the pre-merge count ({officialCount}).");
            }
        }
        catch (Exception ex)
        {
            warnings.Add("Gate verify failed: " + ex.Message);
        }

        progress?.Report($"Done. Restored {restored}, skipped {skipped}, failed {failed}.");
        var result = OverFrameRestoreBatchResult.Create(
            cards.Count, restored, skipped, failed, warnings, gateLocate.BundlePath);
        if (warnings.Count > 0)
        {
            return new OverFrameRestoreBatchResult
            {
                Success = result.Success,
                Message = result.Message + " Warnings: " + string.Join("; ", warnings.Take(8)) +
                          (warnings.Count > 8 ? $" (+{warnings.Count - 8} more)" : ""),
                Total = result.Total,
                Restored = result.Restored,
                Skipped = result.Skipped,
                Failed = result.Failed,
                Warnings = warnings
            };
        }

        return result;
    }

    /// <summary>
    /// Full (slow) scan of live card AssetBundles for 704×1024 textures the app would
    /// render as over-frame, then persists missing/incomplete user.db OF records and
    /// adds missing <c>of_card_asset</c> gate entries (additive). Does not rewrite art.
    /// </summary>
    public OverFrameOrphanRepairBatchResult RepairOrphanOverframes(
        string playerDataPath,
        CardDatabase database,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string packer = "lz4")
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
        {
            return new OverFrameOrphanRepairBatchResult
            {
                Success = false,
                Message = pathError ?? "Invalid game path."
            };
        }

        progress?.Report("Locating of_card_asset gate…");
        var gateLocate = _locator.Locate(playerDataPath, database, progress, cancellationToken);
        if (!gateLocate.Success || gateLocate.BundlePath is null || gateLocate.BundleId is null)
        {
            return new OverFrameOrphanRepairBatchResult
            {
                Success = false,
                Message = gateLocate.Message
            };
        }

        _backupService.BackupGateBundleFile(gateLocate.BundlePath, gateLocate.BundleId);

        OfCardAssetGate gate;
        try
        {
            gate = OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
        }
        catch (Exception ex)
        {
            return new OverFrameOrphanRepairBatchResult
            {
                Success = false,
                Message = "Could not parse live of_card_asset gate: " + ex.Message
            };
        }

        var officialCount = gate.Entries.Count;
        var bundles = database.ListCardBundles();
        var scanned = 0;
        var liveOfFound = 0;
        var fixedCount = 0;
        var gateUpdated = 0;
        var skipped = 0;
        var failed = 0;
        var warnings = new List<string>();
        var gateDirty = false;

        for (var i = 0; i < bundles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (cardId, bundleId) = bundles[i];
            if (i % 50 == 0 || i + 1 == bundles.Count)
            {
                progress?.Report(
                    $"Scanning live art bundles {i + 1}/{bundles.Count} " +
                    $"(found {liveOfFound} OF, fixed {fixedCount})…");
            }

            var card = database.GetById(cardId);
            if (card is null)
                continue;

            string cardBundlePath;
            try
            {
                cardBundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
            }
            catch
            {
                continue;
            }

            TextureInfo info;
            try
            {
                info = _bundleService.ReadTextureInfo(cardBundlePath);
            }
            catch
            {
                continue;
            }

            scanned++;
            var liveIsOf = OverFrameOrphanDetector.IsRenderedAsOverframe(info.Width, info.Height);
            if (!liveIsOf)
                continue;

            liveOfFound++;

            int artId;
            try
            {
                if (CardArtId.TryParse(info.Name, out var parsed))
                {
                    artId = parsed;
                    if (card.ArtId != artId)
                        database.SetArtId(card.Id, artId);
                }
                else
                {
                    artId = ResolveAndCacheArtId(playerDataPath, card, database);
                }
            }
            catch (Exception ex)
            {
                failed++;
                warnings.Add($"{card.DisplayName}: could not resolve art id ({ex.Message})");
                continue;
            }

            var inGate = gate.Contains(artId);
            var floowan = database.IsFloowanOverframe(card.Id);
            var hasEvidence = HasFloowanOrphanEvidence(database, card);
            var kind = OverFrameOrphanDetector.Classify(
                liveIsOverframeSize: true,
                dbIsOverframe: card.IsOverframe,
                dbFloowanOverframe: floowan,
                inGate: inGate,
                hasFloowanEvidence: hasEvidence);

            if (kind == OverFrameOrphanDetector.FixKind.None)
            {
                skipped++;
                continue;
            }

            try
            {
                var baseArtId = card.OverframeBaseId is int stored && stored > 0
                    ? stored
                    : ResolveGateBaseArtId(gate, artId);

                switch (kind)
                {
                    case OverFrameOrphanDetector.FixKind.DbFlagFromGate:
                        database.SetOverframe(card.Id, true, baseArtId);
                        fixedCount++;
                        break;

                    case OverFrameOrphanDetector.FixKind.FloowanMemoryOnly:
                    case OverFrameOrphanDetector.FixKind.FloowanDbInGate:
                        database.SetFloowanOverframe(
                            card.Id, applied: true, overframeBaseId: baseArtId, bundleId: card.Bundle);
                        TrySnapshotLiveAppliedOverFrame(playerDataPath, card);
                        fixedCount++;
                        break;

                    case OverFrameOrphanDetector.FixKind.GateAndFloowanDb:
                        TryRemoveLegacyPkGateEntry(gate, card.Id, artId, database);
                        gate.Add(artId, baseArtId);
                        database.SetFloowanOverframe(
                            card.Id, applied: true, overframeBaseId: baseArtId, bundleId: card.Bundle);
                        TrySnapshotLiveAppliedOverFrame(playerDataPath, card);
                        gateDirty = true;
                        gateUpdated++;
                        fixedCount++;
                        // Persist gate after each add so a mid-run crash keeps prior merges.
                        _textAssets.WriteTextAssetBytes(
                            gateLocate.BundlePath, gate.ToBytes(), compression: packer);
                        gateDirty = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                warnings.Add($"{card.DisplayName}: {ex.Message}");
            }
        }

        if (gateDirty)
            _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes(), compression: packer);

        try
        {
            var verify = OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
            if (verify.Entries.Count < officialCount)
            {
                warnings.Add(
                    $"Gate entry count after repair ({verify.Entries.Count}) is below the pre-merge count ({officialCount}).");
            }
        }
        catch (Exception ex)
        {
            warnings.Add("Gate verify failed: " + ex.Message);
        }

        progress?.Report($"Done. Fixed {fixedCount}, gate updates {gateUpdated}, skipped {skipped}, failed {failed}.");
        var result = OverFrameOrphanRepairBatchResult.Create(
            scanned, liveOfFound, fixedCount, gateUpdated, skipped, failed, warnings, gateLocate.BundlePath);
        if (warnings.Count == 0)
            return result;

        return new OverFrameOrphanRepairBatchResult
        {
            Success = result.Success,
            Message = result.Message + " Warnings: " + string.Join("; ", warnings.Take(8)) +
                      (warnings.Count > 8 ? $" (+{warnings.Count - 8} more)" : ""),
            Scanned = result.Scanned,
            LiveOverframeFound = result.LiveOverframeFound,
            Fixed = result.Fixed,
            GateUpdated = result.GateUpdated,
            Skipped = result.Skipped,
            Failed = result.Failed,
            Warnings = warnings
        };
    }

    private bool HasFloowanOrphanEvidence(CardDatabase database, CardRecord card) =>
        database.HasOfEditLayer(card.Id) ||
        _backupService.HasAppliedOverFrameBackup(card.Name) ||
        _backupService.HasCustomOverframeStage(card.Name);

    private enum RestoreOneOutcome { Restored, Skipped, Failed }

    private RestoreOneOutcome RestoreOneOverframeAfterPatch(
        string playerDataPath,
        CardRecord card,
        CardDatabase database,
        OfCardAssetGate gate,
        string packer,
        out string? warning)
    {
        warning = null;
        string? tempExtract = null;
        try
        {
            string cardBundlePath;
            try
            {
                cardBundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
            }
            catch (Exception ex)
            {
                warning = $"{card.DisplayName}: missing live bundle ({ex.Message})";
                return RestoreOneOutcome.Failed;
            }

            int artId;
            try
            {
                artId = ResolveAndCacheArtId(playerDataPath, card, database);
            }
            catch (Exception ex)
            {
                warning = $"{card.DisplayName}: could not resolve art id ({ex.Message})";
                return RestoreOneOutcome.Failed;
            }

            var baseArtId = card.OverframeBaseId is int stored && stored > 0
                ? stored
                : ResolveGateBaseArtId(gate, artId);

            var liveIsOf = false;
            try
            {
                var info = _bundleService.ReadTextureInfo(cardBundlePath);
                liveIsOf = info.Width == OverFrameConstants.Width &&
                           info.Height == OverFrameConstants.Height;
            }
            catch
            {
                // continue; may still restore from applied PNG
            }

            var alreadyInGate = gate.Contains(artId);
            if (alreadyInGate && liveIsOf)
            {
                warning = $"{card.DisplayName}: already in gate with OF texture; skipped";
                return RestoreOneOutcome.Skipped;
            }

            var needsTextureWrite = !liveIsOf;
            if (needsTextureWrite)
            {
                var imagePath = ResolveAppliedOverFrameImageForRestore(
                    card, out tempExtract, out var imageWarning);
                if (imagePath is null)
                {
                    warning = imageWarning ?? $"{card.DisplayName}: no applied OF backup";
                    return RestoreOneOutcome.Failed;
                }

                var validation = ImagePreparation.Validate(
                    imagePath, OverFrameConstants.Width, OverFrameConstants.Height);
                if (!validation.IsValid)
                {
                    warning = $"{card.DisplayName}: invalid OF image ({validation.Error})";
                    return RestoreOneOutcome.Failed;
                }

                _bundleService.ReplaceTexture(cardBundlePath, imagePath, TextureReplaceOptions.OverFrame with
                {
                    Compression = packer
                });

                var verify = _bundleService.ReadTextureInfo(cardBundlePath);
                if (verify.Width != OverFrameConstants.Width || verify.Height != OverFrameConstants.Height)
                {
                    warning =
                        $"{card.DisplayName}: texture verify failed ({verify.Width}x{verify.Height})";
                    return RestoreOneOutcome.Failed;
                }
            }

            TryRemoveLegacyPkGateEntry(gate, card.Id, artId, database);
            gate.Add(artId, baseArtId);
            database.SetFloowanOverframe(
                card.Id, applied: true, overframeBaseId: baseArtId, bundleId: card.Bundle);
            return RestoreOneOutcome.Restored;
        }
        finally
        {
            if (tempExtract is not null)
            {
                try { if (File.Exists(tempExtract)) File.Delete(tempExtract); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Prefer applied OF PNG backup; else OF-sized extract from card bundle backup.
    /// Pre-OF <c>*-overframe.png</c> is illustration-only and cannot re-apply OF alone.
    /// </summary>
    private string? ResolveAppliedOverFrameImageForRestore(
        CardRecord card,
        out string? tempExtractPath,
        out string? warning)
    {
        tempExtractPath = null;
        warning = null;
        var applied = _backupService.GetAppliedOverFrameBackupPath(card.Name);
        if (File.Exists(applied) && IsOverFrameSizedPng(applied))
            return applied;

        var bundleBackup = _backupService.GetBundleBackupPath(card.Bundle);
        if (File.Exists(bundleBackup))
        {
            try
            {
                var info = _bundleService.ReadTextureInfo(bundleBackup);
                if (info.Width == OverFrameConstants.Width && info.Height == OverFrameConstants.Height)
                {
                    tempExtractPath = Path.Combine(
                        Path.GetTempPath(),
                        $"floowan-of-restore-{card.Id}-{Guid.NewGuid():N}.png");
                    _bundleService.ExtractTexturePng(bundleBackup, tempExtractPath);
                    if (IsOverFrameSizedPng(tempExtractPath))
                    {
                        warning =
                            $"{card.DisplayName}: reconstructed OF from bundle backup (no applied PNG)";
                        return tempExtractPath;
                    }

                    try { File.Delete(tempExtractPath); } catch { /* ignore */ }
                    tempExtractPath = null;
                }
            }
            catch
            {
                if (tempExtractPath is not null)
                {
                    try { File.Delete(tempExtractPath); } catch { /* ignore */ }
                    tempExtractPath = null;
                }
            }
        }

        var originalArt = _backupService.GetOverFrameTextureBackupPath(card.Name);
        if (File.Exists(originalArt))
        {
            warning =
                $"{card.DisplayName}: only pre-OF *-overframe.png found (not applied canvas); skip. Re-apply OF manually or keep applied-overframe backups.";
            return null;
        }

        warning =
            $"{card.DisplayName}: no applied OF PNG / OF-sized live art / OF-sized bundle backup";
        return null;
    }

    private static bool IsOverFrameSizedPng(string path)
    {
        try
        {
            using var image = Image.Load<Rgba32>(path);
            return OverFrameAutoArtComposer.IsOverFrameTextureSize(image.Width, image.Height);
        }
        catch
        {
            return false;
        }
    }

    private void TrySnapshotLiveAppliedOverFrame(string playerDataPath, CardRecord card)
    {
        if (_backupService.HasAppliedOverFrameBackup(card.Name))
            return;

        try
        {
            var bundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
            var info = _bundleService.ReadTextureInfo(bundlePath);
            if (info.Width != OverFrameConstants.Width || info.Height != OverFrameConstants.Height)
                return;

            var temp = Path.Combine(Path.GetTempPath(), $"floowan-of-snap-{Guid.NewGuid():N}.png");
            try
            {
                _bundleService.ExtractTexturePng(bundlePath, temp);
                _backupService.SaveAppliedOverFramePng(card.Name, temp);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            }
        }
        catch
        {
            /* best effort */
        }
    }

    public bool IsInGate(string playerDataPath, CardRecord card, CardDatabase? database = null)
    {
        try
        {
            var gateLocate = _locator.Locate(playerDataPath, database);
            if (!gateLocate.Success || gateLocate.BundlePath is null)
                return false;

            var bytes = _textAssets.ReadTextAssetBytes(gateLocate.BundlePath);
            var gate = OfCardAssetGate.Parse(bytes);
            try
            {
                var artId = ResolveAndCacheArtId(playerDataPath, card, database);
                if (gate.Contains(artId))
                    return true;
            }
            catch
            {
                // fall through to legacy check
            }

            return gate.Contains(card.Id);
        }
        catch
        {
            // Locate/parse/open failures must not block OF preview selection.
            return false;
        }
    }

    public OfCardAssetGate? ReadGate(
        string playerDataPath,
        CardDatabase? database = null,
        bool allowFullScan = false)
    {
        var gateLocate = _locator.Locate(playerDataPath, database, allowFullScan: allowFullScan);
        if (!gateLocate.Success || gateLocate.BundlePath is null)
            return null;

        return OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
    }

    /// <summary>
    /// Cards currently registered in the live <c>of_card_asset</c> gate (any mod source).
    /// Resolves triggers via <c>database.db</c> indexing (catalog <c>card.id</c> is the MD art id;
    /// optional cached <c>user.art_id</c>). Does not scan AssetBundles.
    /// </summary>
    public IReadOnlyList<GatedOverFrameCard> ListGatedOverFrameCards(
        string playerDataPath,
        CardDatabase database,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowFullScan = false)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            throw new InvalidOperationException(pathError ?? "Invalid game path.");

        progress?.Report("Reading of_card_asset gate…");
        var gateLocate = _locator.Locate(playerDataPath, database, progress, cancellationToken, allowFullScan);
        if (!gateLocate.Success || gateLocate.BundlePath is null)
            throw new InvalidOperationException(gateLocate.Message);

        var gate = OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
        var entries = gate.ListEntries();
        if (entries.Count == 0)
            return Array.Empty<GatedOverFrameCard>();

        cancellationToken.ThrowIfCancellationRequested();
        var triggers = entries.Select(e => (int)e.TriggerId).Distinct().ToList();
        progress?.Report($"Indexing {triggers.Count} gate id(s) against database.db…");

        // Master catalog id == Texture2D art id for illustration rows. Prefer that + cached art_id.
        var byCatalogId = database.GetByIds(triggers);
        var byCachedArtId = database.GetByArtIds(triggers);

        var results = new List<GatedOverFrameCard>(entries.Count);
        var seenCards = new HashSet<int>();
        var unresolved = 0;
        foreach (var (trigger, baseArt) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CardRecord? card = null;
            if (byCachedArtId.TryGetValue(trigger, out var fromArt))
                card = fromArt;
            else if (byCatalogId.TryGetValue(trigger, out var fromId))
                card = fromId;

            if (card is null)
            {
                unresolved++;
                continue;
            }

            if (!seenCards.Add(card.Id))
                continue;

            results.Add(new GatedOverFrameCard(card, trigger, baseArt));
        }

        if (unresolved > 0)
        {
            progress?.Report(
                $"Resolved {results.Count} gate card(s); {unresolved} trigger(s) not in database.db.");
        }
        else
        {
            progress?.Report($"Resolved {results.Count} gate card(s) from database.db.");
        }

        results.Sort((a, b) => string.Compare(a.Card.DisplayName, b.Card.DisplayName, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    public int SyncDatabaseFromGate(
        string playerDataPath,
        CardDatabase database,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var gateLocate = _locator.Locate(playerDataPath, database, progress, cancellationToken);
        if (!gateLocate.Success || gateLocate.BundlePath is null || gateLocate.BundleId is null)
            throw new InvalidOperationException(gateLocate.Message);

        var gate = OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));

        // Resolve art ids BEFORE pruning — otherwise a trigger that equals some other card's
        // Floowandereeze PK is treated as legacy junk and deleted, even when it is a real OF art id.
        progress?.Report("Resolving Master Duel art ids for gate entries…");
        PopulateArtIdsForGate(playerDataPath, database, gate.ListEntries(), progress, cancellationToken);

        // Remove mistaken Floowandereeze-PK gate rows from older builds
        // (trigger equals a card PK but is not that card's Texture2D art id).
        var pruned = false;
        foreach (var (trigger, _) in gate.ListEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (database.GetByArtId(trigger) is not null)
                continue;
            var byPk = database.GetById(trigger);
            if (byPk is null)
                continue;

            // Confirm this PK is not secretly the real art id for that row.
            try
            {
                var info = GetTextureInfo(playerDataPath, byPk);
                if (CardArtId.TryParse(info.Name, out var realArt) && realArt == trigger)
                {
                    database.SetArtId(byPk.Id, realArt);
                    continue;
                }
            }
            catch
            {
                // If the bundle is missing, still treat PK-only hits as legacy junk.
            }

            gate.Remove(trigger);
            pruned = true;
        }

        if (pruned)
        {
            progress?.Report("Removing invalid Floowandereeze-PK entries from of_card_asset…");
            _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes());
            gate = OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
        }

        return database.SyncOverframeFromGate(gate.ListEntries());
    }

    /// <summary>
    /// Older builds registered <c>(floowandereezePk, …)</c> instead of the Texture2D art id.
    /// Removing by PK unconditionally is unsafe: art ids often collide with other cards' PKs,
    /// which unregisters previously over-framed cards and shows multilayer frames in-game.
    /// </summary>
    public static bool TryRemoveLegacyPkGateEntry(
        OfCardAssetGate gate,
        int cardPk,
        int? thisCardArtId,
        CardDatabase? database)
    {
        if (cardPk is < 0 or > ushort.MaxValue)
            return false;

        // This card's real art id is the PK — the entry is legitimate, not legacy.
        if (thisCardArtId == cardPk)
            return false;

        // Without a DB we cannot tell PK junk from another card's art-id trigger.
        if (database is null)
            return false;

        // Any card that owns this number as art_id (including the PK row) → keep the trigger.
        if (database.GetByArtId(cardPk) is not null)
            return false;

        return gate.Remove(cardPk);
    }

    public TextureInfo GetTextureInfo(string playerDataPath, CardRecord card)
    {
        var bundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
        return _bundleService.ReadTextureInfo(bundlePath);
    }

    public void ExtractCardArt(string playerDataPath, CardRecord card, string outputPngPath)
    {
        var bundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
        _bundleService.ExtractTexturePng(bundlePath, outputPngPath);
    }

    /// <summary>
    /// True when Auto-create should use the live texture instead of pre-OF backups.
    /// Live wins whenever it is still illustration-sized (not an over-frame canvas).
    /// </summary>
    public static bool PreferLiveAutoCreateSource(bool cardIsOverframe, int liveWidth, int liveHeight) =>
        !CardArtModService.IsLiveOverFrameTexture(cardIsOverframe, liveWidth, liveHeight);

    /// <summary>
    /// Illustration for Custom OF Cover background / rembg / SAM. Same preference as
    /// <see cref="ResolveAutoCreateSourceArt"/>, but when the card is already OF with no
    /// clean backup, crops the live OF art window (last-resort) instead of throwing.
    /// </summary>
    public string ResolveCustomOfIllustrationSource(
        string playerDataPath,
        CardRecord card,
        string outputPngPath)
    {
        try
        {
            return ResolveAutoCreateSourceArt(playerDataPath, card, outputPngPath);
        }
        catch (InvalidOperationException)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);
            var liveTemp = outputPngPath + ".live-of-crop.png";
            try
            {
                ExtractCardArt(playerDataPath, card, liveTemp);
                if (!TryCopyCleanIllustration(liveTemp, outputPngPath))
                    throw;

                return "live OF art-window crop";
            }
            finally
            {
                try { if (File.Exists(liveTemp)) File.Delete(liveTemp); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Writes the current OF canvas (applied PNG backup, else live extract) for preview.
    /// Returns false when nothing OF-sized is available.
    /// </summary>
    public bool TryExportCurrentOverFrameCanvas(
        string playerDataPath,
        CardRecord card,
        string outputPngPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);

        var applied = _backupService.GetAppliedOverFrameBackupPath(card.Name);
        if (File.Exists(applied) && IsOverFrameSizedPng(applied))
        {
            File.Copy(applied, outputPngPath, overwrite: true);
            return true;
        }

        var liveTemp = outputPngPath + ".live-extract.png";
        try
        {
            ExtractCardArt(playerDataPath, card, liveTemp);
            if (!IsOverFrameSizedPng(liveTemp))
                return false;

            File.Copy(liveTemp, outputPngPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(liveTemp)) File.Delete(liveTemp); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Resolves source art for Auto-create / Preview. Prefers the live illustration when
    /// it is still non-OF (including after Card Art replacement). Falls back to pre-OF backups
    /// only when the live texture is already over-framed, so Auto-create cannot nest frames.
    /// </summary>
    public string ResolveAutoCreateSourceArt(string playerDataPath, CardRecord card, string outputPngPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);

        // Live first: Card Art replace writes the live bundle but leaves a pre-replace bundle
        // backup (and possibly an older *-overframe.png). Those must not win over current art.
        if (TryExtractLiveIllustration(playerDataPath, card, outputPngPath, out var liveLabel, out _))
            return liveLabel!;

        var textureBackup = _backupService.GetOverFrameTextureBackupPath(card.Name);
        if (File.Exists(textureBackup))
        {
            if (TryCopyCleanIllustration(textureBackup, outputPngPath))
                return "original texture backup";

            // Unusable backup (corrupt / unreadable) — ignore it.
            try { File.Delete(textureBackup); } catch { /* best effort */ }
        }

        var bundleBackup = _backupService.GetBundleBackupPath(card.Bundle);
        if (File.Exists(bundleBackup))
        {
            var temp = outputPngPath + ".bundle-extract.png";
            try
            {
                _bundleService.ExtractTexturePng(bundleBackup, temp);
                if (TryCopyCleanIllustration(temp, outputPngPath))
                    return "card bundle backup";
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException(
            $"'{card.DisplayName}' is already over-framed and no clean original art backup was found. " +
            "Use Restore backups (or restore the card bundle from a clean install), then Auto-create again. " +
            "Auto-create will not use framed art — that causes nested frames.");
    }

    /// <summary>
    /// Attempts to copy live non-OF illustration to <paramref name="outputPngPath"/>.
    /// Returns true on success. When live is over-framed, returns false with
    /// <paramref name="liveIsOverframed"/> set so callers can fall back to backups.
    /// </summary>
    private bool TryExtractLiveIllustration(
        string playerDataPath,
        CardRecord card,
        string outputPngPath,
        out string? sourceLabel,
        out bool liveIsOverframed)
    {
        sourceLabel = null;
        liveIsOverframed = card.IsOverframe;
        var liveTemp = outputPngPath + ".live-extract.png";
        try
        {
            ExtractCardArt(playerDataPath, card, liveTemp);
            using var liveInfo = Image.Load<Rgba32>(liveTemp);
            liveIsOverframed = CardArtModService.IsLiveOverFrameTexture(
                card.IsOverframe, liveInfo.Width, liveInfo.Height);
            if (liveIsOverframed)
                return false;

            if (!TryCopyCleanIllustration(liveTemp, outputPngPath))
            {
                throw new InvalidOperationException(
                    $"Could not read live art for '{card.DisplayName}'.");
            }

            sourceLabel = "live texture";
            return true;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(liveTemp)) File.Delete(liveTemp); } catch { /* ignore */ }
        }
    }

    private static bool TryCopyCleanIllustration(string sourcePath, string outputPngPath)
    {
        try
        {
            using var loaded = Image.Load<Rgba32>(sourcePath);
            using var clean = OverFrameAutoArtComposer.RequireCleanIllustrationSource(loaded);
            clean.Save(outputPngPath, new PngEncoder());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Shared pre-write backups used by create-new OF (<see cref="ApplyOverFrame"/>) and
    /// gate-only registration (<see cref="EnableGateOnly"/>), including Import.
    /// Snapshots pre-OF illustration once, seeds the card AssetBundle backup when safe,
    /// and keeps a one-time vanilla gate file for disaster recovery.
    /// </summary>
    private void PrepareOverFrameWriteBackups(
        CardRecord card,
        string? cardBundlePath,
        OfCardAssetLocateResult gateLocate,
        bool createBackup,
        CardDatabase? database,
        out string? cardBackup)
    {
        cardBackup = null;

        if (!string.IsNullOrWhiteSpace(cardBundlePath))
            TryPreserveOriginalArtBackup(card, cardBundlePath);

        if (!createBackup)
            return;

        // Never seed the "original" card backup from an already over-framed live bundle.
        if (!string.IsNullOrWhiteSpace(cardBundlePath)
            && (!card.IsOverframe || _backupService.HasBundleBackup(card.Bundle)))
        {
            if (_backupService.TryCreateBundleBackupIfMissing(cardBundlePath, card.Bundle, out var newBackup))
                cardBackup = newBackup;
            else
                cardBackup = _backupService.GetBundleBackupPath(card.Bundle);
        }

        // Keep a one-time vanilla gate file for disaster recovery, but never roll back
        // a failed apply to that stale snapshot (it would wipe every other OF entry).
        if (gateLocate.BundlePath is not null && gateLocate.BundleId is not null)
            _backupService.BackupGateBundleFile(gateLocate.BundlePath, gateLocate.BundleId);

        database?.SetHasBackup(card.Id, true);
    }

    /// <summary>
    /// Saves the pre-over-frame art once. Prefers the live illustration when it is still non-OF
    /// (so Card Art replacements become the OF re-run source). Falls back to the card bundle
    /// backup only when live is already over-framed.
    /// </summary>
    private void TryPreserveOriginalArtBackup(CardRecord card, string liveBundlePath)
    {
        var textureBackup = _backupService.GetOverFrameTextureBackupPath(card.Name);
        if (File.Exists(textureBackup))
        {
            // Replace backups that are already OF-sized (not original illustration).
            if (IsCleanIllustrationFile(textureBackup))
                return;
            try { File.Delete(textureBackup); } catch { return; }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(textureBackup)!);
            var temp = textureBackup + ".tmp.png";
            try
            {
                var bundleBackup = _backupService.GetBundleBackupPath(card.Bundle);
                // Live first when not OF — bundle backup is often pre–Card Art vanilla.
                if (!card.IsOverframe)
                    _bundleService.ExtractTexturePng(liveBundlePath, temp);
                else if (File.Exists(bundleBackup))
                    _bundleService.ExtractTexturePng(bundleBackup, temp);
                else
                    return;

                if (!IsCleanIllustrationFile(temp))
                    return;

                File.Move(temp, textureBackup, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            }
        }
        catch
        {
            /* best-effort snapshot for Auto-create */
        }
    }

    private static bool IsCleanIllustrationFile(string path)
    {
        try
        {
            using var image = Image.Load<Rgba32>(path);
            // Reject actual OF canvases; illustration-sized art (including cream-heavy
            // cards) is accepted — the old "looks framed" heuristic false-triggered.
            return !OverFrameAutoArtComposer.IsOverFrameTextureSize(image.Width, image.Height);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads Texture2D m_Name as the Master Duel art id and caches it on the card row.
    /// </summary>
    public int ResolveAndCacheArtId(string playerDataPath, CardRecord card, CardDatabase? database = null)
    {
        if (card.ArtId is int cached && cached > 0)
            return cached;

        var info = GetTextureInfo(playerDataPath, card);
        var artId = CardArtId.ParseRequired(info.Name);
        database?.SetArtId(card.Id, artId);
        return artId;
    }

    private static int ResolveGateBaseArtId(OfCardAssetGate gate, int artId)
    {
        foreach (var (trigger, baseArt) in gate.Entries)
        {
            if (trigger == artId)
                return baseArt;
        }

        return artId;
    }

    private void PopulateArtIdsForGate(
        string playerDataPath,
        CardDatabase database,
        IReadOnlyList<(ushort TriggerId, ushort BaseArtId)> entries,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // Only triggers need art_id rows for [OF] sync. Bases are stored as overframe_base_id.
        var needed = new HashSet<int>();
        foreach (var (trigger, _) in entries)
        {
            if (database.GetByArtId(trigger) is null)
                needed.Add(trigger);
        }

        if (needed.Count == 0)
            return;

        var cards = database.ListCardBundles();
        for (var i = 0; i < cards.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (needed.Count == 0)
                return;

            if (i % 100 == 0)
                progress?.Report($"Matching gate art ids… {i}/{cards.Count} cards, {needed.Count} left");

            var (cardId, bundle) = cards[i];
            string path;
            try
            {
                path = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, bundle);
            }
            catch
            {
                continue;
            }

            TextureInfo info;
            try { info = _bundleService.ReadTextureInfo(path); }
            catch { continue; }

            if (!CardArtId.TryParse(info.Name, out var parsed) || !needed.Contains(parsed))
                continue;

            database.SetArtId(cardId, parsed);
            needed.Remove(parsed);
        }

        if (needed.Count > 0)
        {
            progress?.Report(
                $"Could not resolve art id(s) {string.Join(", ", needed.OrderBy(x => x))} to a card bundle.");
        }
    }

    public void Dispose()
    {
        _bundleService.Dispose();
        _textAssets.Dispose();
    }
}

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
        CancellationToken cancellationToken = default) =>
        _locator.Locate(playerDataPath, database, progress, cancellationToken);

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
            // Always snapshot pre-OF art once so Auto-create can re-run without nesting frames.
            TryPreserveOriginalArtBackup(card, cardBundlePath);

            if (createBackup)
            {
                // Never seed the "original" card backup from an already over-framed live bundle.
                if (!card.IsOverframe || _backupService.HasBundleBackup(card.Bundle))
                {
                    if (_backupService.TryCreateBundleBackupIfMissing(cardBundlePath, card.Bundle, out var newBackup))
                        cardBackup = newBackup;
                    else
                        cardBackup = _backupService.GetBundleBackupPath(card.Bundle);
                }

                // Keep a one-time vanilla gate file for disaster recovery, but never roll back
                // a failed apply to that stale snapshot (it would wipe every other OF entry).
                _backupService.BackupGateBundleFile(gateLocate.BundlePath, gateLocate.BundleId);
                database?.SetHasBackup(card.Id, true);
            }

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

            database?.SetOverframe(card.Id, isOverframe: true, overframeBaseId: baseArtId);

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

        try
        {
            if (createBackup)
                _backupService.BackupGateBundleFile(gateLocate.BundlePath, gateLocate.BundleId);

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

            database?.SetOverframe(card.Id, isOverframe: true, overframeBaseId: baseArtId);

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

            database?.SetOverframe(card.Id, isOverframe: false, overframeBaseId: null);

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

        database?.SetOverframe(card.Id, isOverframe: false, overframeBaseId: null);
        return OverFrameResult.Ok($"Restored {string.Join(" + ", messages)} from backup.");
    }

    public bool IsInGate(string playerDataPath, CardRecord card, CardDatabase? database = null)
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

    public OfCardAssetGate? ReadGate(string playerDataPath, CardDatabase? database = null)
    {
        var gateLocate = _locator.Locate(playerDataPath, database);
        if (!gateLocate.Success || gateLocate.BundlePath is null)
            return null;

        return OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
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
    /// Resolves source art for Auto-create. Prefers clean pre-over-frame backups and
    /// refuses framed / already-OF textures so Auto-create cannot nest frames.
    /// </summary>
    public string ResolveAutoCreateSourceArt(string playerDataPath, CardRecord card, string outputPngPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);

        var textureBackup = _backupService.GetOverFrameTextureBackupPath(card.Name);
        if (File.Exists(textureBackup))
        {
            if (TryCopyCleanIllustration(textureBackup, outputPngPath))
                return "original texture backup";

            // Poisoned backup (saved after a nested OF) — ignore it.
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

        // Live texture: only acceptable when it is still original (non-OF) art.
        var liveTemp = outputPngPath + ".live-extract.png";
        try
        {
            ExtractCardArt(playerDataPath, card, liveTemp);
            using var liveInfo = Image.Load<Rgba32>(liveTemp);
            var liveIsOfSize = OverFrameAutoArtComposer.IsOverFrameTextureSize(liveInfo.Width, liveInfo.Height);
            if (card.IsOverframe || liveIsOfSize)
            {
                throw new InvalidOperationException(
                    $"'{card.DisplayName}' is already over-framed and no clean original art backup was found. " +
                    "Use Restore backups (or restore the card bundle from a clean install), then Auto-create again. " +
                    "Auto-create will not use framed art — that causes nested frames.");
            }

            if (!TryCopyCleanIllustration(liveTemp, outputPngPath))
            {
                throw new InvalidOperationException(
                    $"Live art for '{card.DisplayName}' looks like a framed card. " +
                    "Restore the original illustration backup before Auto-create.");
            }

            return "live texture";
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
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Saves the pre-over-frame art once. Skips when the live card is already over-framed unless a
    /// prior bundle backup exists to extract from (avoids snapshotting framed art as "original").
    /// </summary>
    private void TryPreserveOriginalArtBackup(CardRecord card, string liveBundlePath)
    {
        var textureBackup = _backupService.GetOverFrameTextureBackupPath(card.Name);
        if (File.Exists(textureBackup))
        {
            // Replace poisoned backups that still look framed.
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
                if (File.Exists(bundleBackup))
                    _bundleService.ExtractTexturePng(bundleBackup, temp);
                else if (!card.IsOverframe)
                    _bundleService.ExtractTexturePng(liveBundlePath, temp);
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
            using var clean = OverFrameAutoArtComposer.ExtractIllustrationSource(image);
            return !OverFrameAutoArtComposer.LooksLikeFramedCardArt(clean);
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

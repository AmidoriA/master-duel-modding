using Floowan.Core.Assets;
using Floowan.Core.Backup;
using Floowan.Core.Data;
using Floowan.Core.Game;
using Floowan.Core.Imaging;
using Floowan.Core.Models;

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
        string? gateBackup = null;
        try
        {
            if (createBackup)
            {
                cardBackup = _backupService.BackupBundleFile(cardBundlePath, card.Bundle);
                gateBackup = _backupService.BackupGateBundleFile(gateLocate.BundlePath, gateLocate.BundleId);
                var textureBackup = _backupService.GetTextureBackupPath(card.Name + "-overframe");
                if (!File.Exists(textureBackup))
                {
                    try { _bundleService.ExtractTexturePng(cardBundlePath, textureBackup); }
                    catch { /* preview extract is best-effort */ }
                }

                database?.SetHasBackup(card.Id, true);
            }

            _bundleService.ReplaceTexture(cardBundlePath, replacementImagePath, TextureReplaceOptions.OverFrame with
            {
                Compression = packer
            });

            var gateBytes = _textAssets.ReadTextAssetBytes(gateLocate.BundlePath);
            var gate = OfCardAssetGate.Parse(gateBytes);
            gate.Add(card.Id, card.Id);
            _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes(), compression: packer);

            database?.SetOverframe(card.Id, isOverframe: true, overframeBaseId: card.Id);

            var msg =
                $"Applied over-frame for '{card.DisplayName}' ({OverFrameConstants.Width}x{OverFrameConstants.Height}, RGBA32) and registered gate ({card.Id},{card.Id}).";
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

            if (gateBackup is not null && gateLocate.BundlePath is not null && gateLocate.BundleId is not null)
            {
                try { _backupService.TryRestoreGateBundleFile(gateLocate.BundlePath, gateLocate.BundleId); }
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
            gate.Add(card.Id, card.Id);
            _textAssets.WriteTextAssetBytes(gateLocate.BundlePath, gate.ToBytes(), compression: packer);
            database?.SetOverframe(card.Id, isOverframe: true, overframeBaseId: card.Id);

            return OverFrameResult.Ok(
                $"Enabled over-frame gate for '{card.DisplayName}' ({card.Id},{card.Id}) without changing texture.",
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
            var removed = gate.Remove(card.Id);
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
        CardDatabase? database = null)
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

        var gateLocate = _locator.Locate(playerDataPath, database);
        if (gateLocate.Success && gateLocate.BundlePath is not null && gateLocate.BundleId is not null)
        {
            if (_backupService.TryRestoreGateBundleFile(gateLocate.BundlePath, gateLocate.BundleId))
                messages.Add("gate bundle");
        }

        if (messages.Count == 0)
            return OverFrameResult.Fail("No over-frame backups found for this card/gate.");

        database?.SetOverframe(card.Id, isOverframe: false, overframeBaseId: null);
        return OverFrameResult.Ok($"Restored {string.Join(" + ", messages)} from backup.");
    }

    public bool IsInGate(string playerDataPath, int cardId, CardDatabase? database = null)
    {
        var gateLocate = _locator.Locate(playerDataPath, database);
        if (!gateLocate.Success || gateLocate.BundlePath is null)
            return false;

        var bytes = _textAssets.ReadTextAssetBytes(gateLocate.BundlePath);
        var gate = OfCardAssetGate.Parse(bytes);
        return gate.Contains(cardId);
    }

    public OfCardAssetGate? ReadGate(string playerDataPath, CardDatabase? database = null)
    {
        var gateLocate = _locator.Locate(playerDataPath, database);
        if (!gateLocate.Success || gateLocate.BundlePath is null)
            return null;

        return OfCardAssetGate.Parse(_textAssets.ReadTextAssetBytes(gateLocate.BundlePath));
    }

    public int SyncDatabaseFromGate(string playerDataPath, CardDatabase database)
    {
        var gate = ReadGate(playerDataPath, database)
                   ?? throw new InvalidOperationException("of_card_asset gate could not be read.");
        return database.SyncOverframeFromGate(gate.ListEntries());
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

    public void Dispose()
    {
        _bundleService.Dispose();
        _textAssets.Dispose();
    }
}

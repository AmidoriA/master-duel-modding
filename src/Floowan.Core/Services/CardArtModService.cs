using Floowan.Core.Assets;
using Floowan.Core.Backup;
using Floowan.Core.Data;
using Floowan.Core.Game;
using Floowan.Core.Imaging;
using Floowan.Core.Models;

namespace Floowan.Core.Services;

public sealed class CardArtModService : IDisposable
{
    private readonly CardArtBundleService _bundleService;
    private readonly BackupService _backupService;

    public CardArtModService(string? classDataPath = null, string? backupRoot = null)
    {
        _bundleService = new CardArtBundleService(classDataPath);
        _backupService = new BackupService(backupRoot);
    }

    public BackupService Backups => _backupService;

    public CardArtReplacementResult ReplaceCardArt(
        string playerDataPath,
        CardRecord card,
        string replacementImagePath,
        bool createBackup,
        CardDatabase? database = null,
        string packer = "lz4")
    {
        if (!GamePathLocator.IsValidGamePath(playerDataPath, out var pathError))
            return CardArtReplacementResult.Fail(pathError ?? "Invalid game path.");

        string bundlePath;
        try
        {
            bundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
        }
        catch (Exception ex)
        {
            return CardArtReplacementResult.Fail(ex.Message);
        }

        TextureInfo info;
        try
        {
            info = _bundleService.ReadTextureInfo(bundlePath);
        }
        catch (Exception ex)
        {
            return CardArtReplacementResult.Fail($"Could not read bundle texture: {ex.Message}");
        }

        var validation = ImagePreparation.Validate(replacementImagePath, info.Width, info.Height);
        if (!validation.IsValid)
            return CardArtReplacementResult.Fail(validation.Error ?? "Invalid image.");

        string? backupPath = null;
        try
        {
            if (createBackup)
            {
                backupPath = _backupService.BackupBundleFile(bundlePath, card.Bundle);
                var textureBackup = _backupService.GetTextureBackupPath(card.Name);
                if (!File.Exists(textureBackup))
                    _bundleService.ExtractTexturePng(bundlePath, textureBackup);
                database?.SetHasBackup(card.Id, true);
            }

            _bundleService.ReplaceTexture(bundlePath, replacementImagePath, packer);
            var msg = $"Replaced art for '{card.DisplayName}' ({info.Width}x{info.Height}, format→RGBA32).";
            if (!string.IsNullOrEmpty(validation.Warning))
                msg += " " + validation.Warning;
            return CardArtReplacementResult.Ok(msg, bundlePath, backupPath);
        }
        catch (Exception ex)
        {
            if (backupPath is not null)
            {
                try { _backupService.TryRestoreBundleFile(bundlePath, card.Bundle); }
                catch { /* best effort */ }
            }

            return CardArtReplacementResult.Fail($"Replacement failed: {ex.Message}");
        }
    }

    public bool RestoreCardArt(string playerDataPath, CardRecord card, CardDatabase? database = null)
    {
        var bundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
        var ok = _backupService.TryRestoreBundleFile(bundlePath, card.Bundle);
        if (ok)
            database?.SetHasBackup(card.Id, true);
        return ok;
    }

    public void ExtractCardArt(string playerDataPath, CardRecord card, string outputPngPath)
    {
        var bundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
        _bundleService.ExtractTexturePng(bundlePath, outputPngPath);
    }

    public TextureInfo GetTextureInfo(string playerDataPath, CardRecord card)
    {
        var bundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
        return _bundleService.ReadTextureInfo(bundlePath);
    }

    public void Dispose() => _bundleService.Dispose();
}

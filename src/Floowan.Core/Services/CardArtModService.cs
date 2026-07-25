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

    /// <summary>
    /// True when the live texture is an over-frame face. Card Art should not write that
    /// canvas into <c>backups/cards/{slug}.png</c> (illustration) or the OF pre-art path.
    /// </summary>
    public static bool IsLiveOverFrameTexture(bool cardIsOverframe, int width, int height) =>
        cardIsOverframe ||
        (width == OverFrameConstants.Width && height == OverFrameConstants.Height);

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
            var backupCreated = false;
            if (createBackup)
            {
                // One-time bundle snapshot of whatever is live before overwrite (never clobber).
                if (_backupService.TryCreateBundleBackupIfMissing(bundlePath, card.Bundle, out var newBackup))
                {
                    backupPath = newBackup;
                    backupCreated = true;
                }
                else
                {
                    backupPath = _backupService.GetBundleBackupPath(card.Bundle);
                }

                // Readable PNG under backups/cards/{slug}.png — only for illustration textures.
                // Over-frame live canvases (704×1024) must not land here or on the OF pre-art path;
                // the bundle backup is the restore point when replacing an OF face via Card Art.
                if (!IsLiveOverFrameTexture(card.IsOverframe, info.Width, info.Height))
                {
                    var textureBackup = _backupService.GetTextureBackupPath(card.Name);
                    if (!File.Exists(textureBackup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(textureBackup)!);
                        _bundleService.ExtractTexturePng(bundlePath, textureBackup);
                        backupCreated = true;
                    }
                }

                database?.SetHasBackup(card.Id, true);
            }

            // Keeps live Texture2D width/height (Pendulum canvas or normal 512×512).
            _bundleService.ReplaceTexture(bundlePath, replacementImagePath, packer);

            // OF Auto-create / Preview previously preferred a stale *-overframe.png (or the
            // pre-replace bundle backup). Drop the OF pre-art snapshot so the next OF run
            // uses the live replaced illustration.
            _backupService.TryInvalidateOverFrameTextureBackup(card.Name);

            var targetDesc = CardArtTextureSizes.Describe(info.Width, info.Height);
            var msg = $"Replaced art for '{card.DisplayName}' ({targetDesc}, format→RGBA32).";
            if (!string.IsNullOrEmpty(validation.Info))
                msg += " " + validation.Info;
            if (!string.IsNullOrEmpty(validation.Warning))
                msg += " " + validation.Warning;
            if (createBackup)
            {
                msg += backupCreated
                    ? " Backup created."
                    : " Existing backup kept.";
            }

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
        // Pendulum: full live canvas resized to 512×683 (not top-cropped).
        _bundleService.ExtractTexturePng(bundlePath, outputPngPath);
    }

    public TextureInfo GetTextureInfo(string playerDataPath, CardRecord card)
    {
        var bundlePath = BundlePathResolver.ResolveExistingBundlePath(playerDataPath, card.Bundle);
        return _bundleService.ReadTextureInfo(bundlePath);
    }

    public void Dispose() => _bundleService.Dispose();
}

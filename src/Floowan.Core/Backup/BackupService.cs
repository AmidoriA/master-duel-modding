using Floowan.Core.Imaging;

namespace Floowan.Core.Backup;

public sealed class BackupService
{
    private readonly string _root;

    public BackupService(string? rootDirectory = null)
    {
        _root = rootDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "backups");
    }

    public string RootDirectory => _root;

    public string GetBundleBackupPath(string bundleId) =>
        Path.Combine(_root, "bundles", "cards", bundleId);

    public string GetGateBundleBackupPath(string bundleId) =>
        Path.Combine(_root, "bundles", "gate", bundleId);

    public string GetTextureBackupPath(string cardName) =>
        Path.Combine(_root, "cards", ImagePreparation.Slugify(cardName) + ".png");

    /// <summary>
    /// Original (pre-over-frame) art snapshot used by Auto-create on re-runs.
    /// </summary>
    public string GetOverFrameTextureBackupPath(string cardName) =>
        GetTextureBackupPath(cardName + "-overframe");

    /// <summary>
    /// Last successfully applied Floowan over-frame canvas (704x1024), used to re-apply after
    /// an MD patch replaces live art / of_card_asset.
    /// </summary>
    public string GetAppliedOverFrameBackupPath(string cardName) =>
        GetTextureBackupPath(cardName + "-applied-overframe");

    public bool HasBundleBackup(string bundleId) =>
        File.Exists(GetBundleBackupPath(bundleId));

    public bool HasOverFrameTextureBackup(string cardName) =>
        File.Exists(GetOverFrameTextureBackupPath(cardName));

    public bool HasAppliedOverFrameBackup(string cardName) =>
        File.Exists(GetAppliedOverFrameBackupPath(cardName));

    /// <summary>
    /// Copies the applied OF PNG into backups (overwrites). Used on successful Apply.
    /// </summary>
    public string SaveAppliedOverFramePng(string cardName, string sourcePngPath)
    {
        var dest = GetAppliedOverFrameBackupPath(cardName);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(sourcePngPath, dest, overwrite: true);
        return dest;
    }

    /// <summary>
    /// Drops the applied OF canvas backup (e.g. after Restore backups / intentional OF removal).
    /// </summary>
    public bool TryDeleteAppliedOverFrameBackup(string cardName)
    {
        var path = GetAppliedOverFrameBackupPath(cardName);
        if (!File.Exists(path))
            return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Drops the pre-over-frame PNG snapshot so the next OF run re-reads current live art
    /// (e.g. after Card Art replacement). Bundle backups used for Restore are left intact.
    /// </summary>
    public bool TryInvalidateOverFrameTextureBackup(string cardName)
    {
        var path = GetOverFrameTextureBackupPath(cardName);
        if (!File.Exists(path))
            return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string BackupBundleFile(string sourceBundlePath, string bundleId) =>
        BackupFile(sourceBundlePath, GetBundleBackupPath(bundleId));

    public string BackupGateBundleFile(string sourceBundlePath, string bundleId) =>
        BackupFile(sourceBundlePath, GetGateBundleBackupPath(bundleId));

    public bool TryRestoreBundleFile(string targetBundlePath, string bundleId) =>
        TryRestoreFile(targetBundlePath, GetBundleBackupPath(bundleId));

    public bool TryRestoreGateBundleFile(string targetBundlePath, string bundleId) =>
        TryRestoreFile(targetBundlePath, GetGateBundleBackupPath(bundleId));

    /// <summary>
    /// Copies the bundle only when no backup exists yet. Returns whether a new file was written.
    /// </summary>
    public bool TryCreateBundleBackupIfMissing(string sourceBundlePath, string bundleId, out string backupPath)
    {
        backupPath = GetBundleBackupPath(bundleId);
        if (File.Exists(backupPath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(sourceBundlePath, backupPath);
        return true;
    }

    private static string BackupFile(string sourcePath, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!File.Exists(dest))
            File.Copy(sourcePath, dest);
        return dest;
    }

    private static bool TryRestoreFile(string targetPath, string backupPath)
    {
        if (!File.Exists(backupPath))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        File.Copy(backupPath, targetPath, overwrite: true);
        return true;
    }
}

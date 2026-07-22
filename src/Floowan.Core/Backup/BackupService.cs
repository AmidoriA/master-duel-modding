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

    public bool HasBundleBackup(string bundleId) =>
        File.Exists(GetBundleBackupPath(bundleId));

    public bool HasOverFrameTextureBackup(string cardName) =>
        File.Exists(GetOverFrameTextureBackupPath(cardName));

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

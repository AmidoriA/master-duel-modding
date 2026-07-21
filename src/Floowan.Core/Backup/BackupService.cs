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

    public string BackupBundleFile(string sourceBundlePath, string bundleId) =>
        BackupFile(sourceBundlePath, GetBundleBackupPath(bundleId));

    public string BackupGateBundleFile(string sourceBundlePath, string bundleId) =>
        BackupFile(sourceBundlePath, GetGateBundleBackupPath(bundleId));

    public bool TryRestoreBundleFile(string targetBundlePath, string bundleId) =>
        TryRestoreFile(targetBundlePath, GetBundleBackupPath(bundleId));

    public bool TryRestoreGateBundleFile(string targetBundlePath, string bundleId) =>
        TryRestoreFile(targetBundlePath, GetGateBundleBackupPath(bundleId));

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

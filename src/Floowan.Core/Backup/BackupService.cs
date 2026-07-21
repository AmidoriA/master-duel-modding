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

    public string GetTextureBackupPath(string cardName) =>
        Path.Combine(_root, "cards", ImagePreparation.Slugify(cardName) + ".png");

    public string BackupBundleFile(string sourceBundlePath, string bundleId)
    {
        var dest = GetBundleBackupPath(bundleId);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!File.Exists(dest))
            File.Copy(sourceBundlePath, dest);
        return dest;
    }

    public bool TryRestoreBundleFile(string targetBundlePath, string bundleId)
    {
        var backup = GetBundleBackupPath(bundleId);
        if (!File.Exists(backup))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetBundlePath))!);
        File.Copy(backup, targetBundlePath, overwrite: true);
        return true;
    }
}

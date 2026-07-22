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

    public string GetCutInBundleBackupPath(int cutInId, string bundleFileName) =>
        Path.Combine(_root, "bundles", "cutin", cutInId.ToString(), bundleFileName);

    public string GetCutInBackupDirectory(int cutInId) =>
        Path.Combine(_root, "bundles", "cutin", cutInId.ToString());

    public bool HasBundleBackup(string bundleId) =>
        File.Exists(GetBundleBackupPath(bundleId));

    public bool HasOverFrameTextureBackup(string cardName) =>
        File.Exists(GetOverFrameTextureBackupPath(cardName));

    public bool HasCutInBackup(int cutInId) =>
        Directory.Exists(GetCutInBackupDirectory(cutInId))
        && Directory.EnumerateFiles(GetCutInBackupDirectory(cutInId)).Any();

    public string BackupBundleFile(string sourceBundlePath, string bundleId) =>
        BackupFile(sourceBundlePath, GetBundleBackupPath(bundleId));

    public string BackupGateBundleFile(string sourceBundlePath, string bundleId) =>
        BackupFile(sourceBundlePath, GetGateBundleBackupPath(bundleId));

    public string BackupCutInBundleFile(string sourceBundlePath, int cutInId, string bundleFileName) =>
        BackupFile(sourceBundlePath, GetCutInBundleBackupPath(cutInId, bundleFileName));

    public bool TryRestoreBundleFile(string targetBundlePath, string bundleId) =>
        TryRestoreFile(targetBundlePath, GetBundleBackupPath(bundleId));

    public bool TryRestoreGateBundleFile(string targetBundlePath, string bundleId) =>
        TryRestoreFile(targetBundlePath, GetGateBundleBackupPath(bundleId));

    /// <summary>
    /// Restores every backed-up cut-in bundle for <paramref name="cutInId"/> into the
    /// directories recorded next to each backup via a sibling <c>.path.txt</c> file.
    /// </summary>
    public int TryRestoreAllCutInBundles(int cutInId)
    {
        var dir = GetCutInBackupDirectory(cutInId);
        if (!Directory.Exists(dir))
            return 0;

        var restored = 0;
        foreach (var backup in Directory.EnumerateFiles(dir))
        {
            if (backup.EndsWith(".path.txt", StringComparison.OrdinalIgnoreCase))
                continue;
            var pathFile = backup + ".path.txt";
            if (!File.Exists(pathFile))
                continue;
            var target = File.ReadAllText(pathFile).Trim();
            if (string.IsNullOrWhiteSpace(target))
                continue;
            if (TryRestoreFile(target, backup))
                restored++;
        }

        return restored;
    }

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

        // For cut-in backups, remember the live path so restore can find it again.
        if (dest.Contains(Path.Combine("bundles", "cutin"), StringComparison.Ordinal))
        {
            var pathSidecar = dest + ".path.txt";
            if (!File.Exists(pathSidecar))
                File.WriteAllText(pathSidecar, Path.GetFullPath(sourcePath));
        }

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

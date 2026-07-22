using Floowan.Core.Backup;

namespace Floowan.Core.Tests;

public sealed class BackupServiceTests
{
    [Fact]
    public void OverFrameTextureBackupPath_UsesDistinctSlug()
    {
        var root = Path.Combine(Path.GetTempPath(), "floowan-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backups = new BackupService(root);
            var path = backups.GetOverFrameTextureBackupPath("Garura, Wings of Resonant Life");
            Assert.EndsWith(Path.Combine("cards", "garura-wings-of-resonant-life-overframe.png"), path);
            Assert.False(backups.HasOverFrameTextureBackup("Garura, Wings of Resonant Life"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryCreateBundleBackupIfMissing_DoesNotOverwriteExisting()
    {
        var root = Path.Combine(Path.GetTempPath(), "floowan-backup-tests-" + Guid.NewGuid().ToString("N"));
        var srcA = Path.Combine(root, "src-a.bin");
        var srcB = Path.Combine(root, "src-b.bin");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(srcA, [1, 2, 3]);
            File.WriteAllBytes(srcB, [9, 9, 9]);

            var backups = new BackupService(root);
            Assert.True(backups.TryCreateBundleBackupIfMissing(srcA, "bundle1", out var first));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first));

            Assert.False(backups.TryCreateBundleBackupIfMissing(srcB, "bundle1", out var second));
            Assert.Equal(first, second);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(second));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}

using Floowan.Core.Assets;
using Floowan.Core.Backup;
using Floowan.Core.Services;

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
    public void TextureBackupPath_UsesReadableSlug()
    {
        var root = Path.Combine(Path.GetTempPath(), "floowan-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backups = new BackupService(root);
            var path = backups.GetTextureBackupPath("Blue-Eyes White Dragon");
            Assert.EndsWith(Path.Combine("cards", "blue-eyes-white-dragon.png"), path);
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

    [Fact]
    public void TryInvalidateOverFrameTextureBackup_DeletesExistingSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "floowan-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backups = new BackupService(root);
            var path = backups.GetOverFrameTextureBackupPath("Test Card");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);

            Assert.True(backups.HasOverFrameTextureBackup("Test Card"));
            Assert.True(backups.TryInvalidateOverFrameTextureBackup("Test Card"));
            Assert.False(backups.HasOverFrameTextureBackup("Test Card"));
            Assert.False(backups.TryInvalidateOverFrameTextureBackup("Test Card"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Theory]
    [InlineData(false, 512, 512, true)]
    [InlineData(false, 512, 683, true)]
    [InlineData(false, 512, 1024, true)]
    [InlineData(false, OverFrameConstants.Width, OverFrameConstants.Height, false)]
    [InlineData(true, 512, 512, false)]
    [InlineData(true, OverFrameConstants.Width, OverFrameConstants.Height, false)]
    public void PreferLiveAutoCreateSource_UsesLiveWhenNotOverframed(
        bool cardIsOverframe, int width, int height, bool expected)
    {
        Assert.Equal(
            expected,
            OverFrameModService.PreferLiveAutoCreateSource(cardIsOverframe, width, height));
    }

    [Theory]
    [InlineData(false, 512, 512, false)]
    [InlineData(false, 512, 683, false)]
    [InlineData(false, 512, 1024, false)]
    [InlineData(false, OverFrameConstants.Width, OverFrameConstants.Height, true)]
    [InlineData(true, 512, 512, true)]
    [InlineData(true, OverFrameConstants.Width, OverFrameConstants.Height, true)]
    public void IsLiveOverFrameTexture_DetectsOfCanvas(
        bool cardIsOverframe, int width, int height, bool expected)
    {
        Assert.Equal(expected, CardArtModService.IsLiveOverFrameTexture(cardIsOverframe, width, height));
    }
}

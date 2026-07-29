using Floowan.Core.Assets;
using Floowan.Core.Backup;
using Floowan.Core.Imaging;
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
    public void AppliedOverFrameBackup_SaveAndDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), "floowan-backup-tests-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src.png");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(src, [1, 2, 3, 4]);
            var backups = new BackupService(root);

            var saved = backups.SaveAppliedOverFramePng("Blue-Eyes White Dragon", src);
            Assert.EndsWith(Path.Combine("cards", "blue-eyes-white-dragon-applied-overframe.png"), saved);
            Assert.True(backups.HasAppliedOverFrameBackup("Blue-Eyes White Dragon"));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(saved));

            Assert.True(backups.TryDeleteAppliedOverFrameBackup("Blue-Eyes White Dragon"));
            Assert.False(backups.HasAppliedOverFrameBackup("Blue-Eyes White Dragon"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void CustomOverframeStage_SaveLoadAndDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), "floowan-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backups = new BackupService(root);
            using var subject = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(32, 32);
            using var mask = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.L8>(32, 32);
            using var background = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(64, 64);
            subject[4, 4] = new SixLabors.ImageSharp.PixelFormats.Rgba32(200, 10, 10, 255);
            mask[4, 4] = new SixLabors.ImageSharp.PixelFormats.L8(255);
            background[1, 1] = new SixLabors.ImageSharp.PixelFormats.Rgba32(10, 200, 10, 255);

            var state = new CustomOverframeStageState
            {
                FrameStyle = nameof(CardFrameStyle.OfGradientSynchro),
                SubjectScale = 1.75f,
                SubjectOffsetX = 12,
                SubjectOffsetY = -8,
                BackgroundScale = 1.25f,
                BackgroundOffsetX = 3,
                BackgroundOffsetY = -2,
                BackgroundIsCardArt = true,
                SubjectIsFromCardArt = true
            };

            backups.SaveCustomOverframeStage("Mirrorjade the Iceblade Dragon", state, subject, mask, background);
            Assert.True(backups.HasCustomOverframeStage("Mirrorjade the Iceblade Dragon"));
            Assert.EndsWith(
                Path.Combine("cards", "mirrorjade-the-iceblade-dragon-custom-of"),
                backups.GetCustomOverframeStageDirectory("Mirrorjade the Iceblade Dragon"));

            Assert.True(backups.TryLoadCustomOverframeStage(
                "Mirrorjade the Iceblade Dragon",
                out var loaded,
                out var loadedSubject,
                out var loadedMask,
                out var loadedBg));
            using (loadedSubject)
            using (loadedMask)
            using (loadedBg)
            {
                Assert.Equal(nameof(CardFrameStyle.OfGradientSynchro), loaded.FrameStyle);
                Assert.Equal(1.75f, loaded.SubjectScale);
                Assert.Equal(12, loaded.SubjectOffsetX);
                Assert.Equal(-8, loaded.SubjectOffsetY);
                Assert.Equal(1.25f, loaded.BackgroundScale);
                Assert.Equal(3, loaded.BackgroundOffsetX);
                Assert.Equal(-2, loaded.BackgroundOffsetY);
                Assert.True(loaded.BackgroundIsCardArt);
                Assert.True(loaded.SubjectIsFromCardArt);
                Assert.True(loaded.HasSubject);
                Assert.True(loaded.HasBackground);
                Assert.NotNull(loadedSubject);
                Assert.NotNull(loadedMask);
                Assert.NotNull(loadedBg);
                Assert.Equal(32, loadedSubject.Width);
                Assert.Equal(64, loadedBg.Width);
                Assert.Equal(200, loadedSubject[4, 4].R);
            }

            Assert.True(backups.TryDeleteCustomOverframeStage("Mirrorjade the Iceblade Dragon"));
            Assert.False(backups.HasCustomOverframeStage("Mirrorjade the Iceblade Dragon"));
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

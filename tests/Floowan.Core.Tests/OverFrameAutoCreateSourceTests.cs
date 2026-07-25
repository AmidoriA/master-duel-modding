using Floowan.Core.Assets;
using Floowan.Core.Backup;
using Floowan.Core.Game;
using Floowan.Core.Models;
using Floowan.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public sealed class OverFrameAutoCreateSourceTests
{
    private static readonly string SampleBundle =
        @"D:\Games\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\LocalData\2addcbb5\0000\00\000c16e8";

    [Fact]
    public void ResolveAutoCreateSourceArt_PrefersLiveOverStaleBackups_WhenGameBundleAvailable()
    {
        if (!File.Exists(SampleBundle))
            return;

        var classData = ClassDataLocator.FindClassDataPath();
        var workRoot = Path.Combine(Path.GetTempPath(), "floowan-of-src-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(workRoot, "Yu-Gi-Oh!  Master Duel");
        var playerData = Path.Combine(install, "LocalData", "player");
        var backupRoot = Path.Combine(workRoot, "backups");
        var liveBundle = BundlePathResolver.GetLocalDataBundlePath(playerData, "000c16e8");
        var livePng = Path.Combine(workRoot, "live-magenta.png");
        var outPng = Path.Combine(workRoot, "resolved.png");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(liveBundle)!);
            File.Copy(SampleBundle, liveBundle);

            using (var img = new Image<Rgba32>(512, 512, Color.Magenta))
                img.SaveAsPng(livePng);

            using (var bundles = new CardArtBundleService(classData))
                bundles.ReplaceTexture(liveBundle, livePng, compression: "lz4");

            using var ofService = new OverFrameModService(classData, backupRoot);
            var card = new CardRecord
            {
                Id = 1,
                Name = "Test Dragon",
                Bundle = "000c16e8",
                IsOverframe = false
            };

            // Stale pre-replace snapshots that previously won over live art.
            var ofBackup = ofService.Backups.GetOverFrameTextureBackupPath(card.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(ofBackup)!);
            using (var stale = new Image<Rgba32>(512, 512, Color.Lime))
                stale.SaveAsPng(ofBackup);

            ofService.Backups.TryCreateBundleBackupIfMissing(SampleBundle, card.Bundle, out _);

            var label = ofService.ResolveAutoCreateSourceArt(playerData, card, outPng);
            Assert.Equal("live texture", label);
            Assert.True(File.Exists(outPng));

            using var resolved = Image.Load<Rgba32>(outPng);
            Assert.Equal(512, resolved.Width);
            Assert.Equal(512, resolved.Height);
            var pixel = resolved[256, 256];
            Assert.True(pixel.R > 200 && pixel.B > 200 && pixel.G < 80,
                $"expected magenta live art, got R={pixel.R} G={pixel.G} B={pixel.B}");
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ReplaceCardArt_InvalidatesOverFrameTextureBackup_WhenGameBundleAvailable()
    {
        if (!File.Exists(SampleBundle))
            return;

        var classData = ClassDataLocator.FindClassDataPath();
        var workRoot = Path.Combine(Path.GetTempPath(), "floowan-art-inv-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(workRoot, "Yu-Gi-Oh!  Master Duel");
        var playerData = Path.Combine(install, "LocalData", "player");
        var backupRoot = Path.Combine(workRoot, "backups");
        var liveBundle = BundlePathResolver.GetLocalDataBundlePath(playerData, "000c16e8");
        var replacement = Path.Combine(workRoot, "replacement.png");
        var unity = Path.Combine(install, "masterduel_Data", "data.unity3d");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(unity)!);
            File.WriteAllBytes(unity, [0]);
            Directory.CreateDirectory(Path.GetDirectoryName(liveBundle)!);
            File.Copy(SampleBundle, liveBundle);

            using (var img = new Image<Rgba32>(512, 512, Color.Orange))
                img.SaveAsPng(replacement);

            Assert.True(GamePathLocator.IsValidGamePath(playerData, out var pathError), pathError);

            using var mod = new CardArtModService(classData, backupRoot);
            var card = new CardRecord
            {
                Id = 1,
                Name = "Invalidate Me",
                Bundle = "000c16e8",
                IsOverframe = false
            };

            var ofBackup = mod.Backups.GetOverFrameTextureBackupPath(card.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(ofBackup)!);
            using (var stale = new Image<Rgba32>(512, 512, Color.Blue))
                stale.SaveAsPng(ofBackup);
            Assert.True(mod.Backups.HasOverFrameTextureBackup(card.Name));

            var result = mod.ReplaceCardArt(playerData, card, replacement, createBackup: true);
            Assert.True(result.Success, result.Message);
            Assert.False(mod.Backups.HasOverFrameTextureBackup(card.Name));
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* ignore */ }
        }
    }
}

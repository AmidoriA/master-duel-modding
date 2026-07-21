using Floowan.Core.Assets;
using Floowan.Core.Backup;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class CardArtBundleServiceIntegrationTests
{
    [Fact]
    public void ReplaceRoundTrip_OnTempCopy_WhenGameBundleAvailable()
    {
        var sample = @"D:\Games\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\LocalData\2addcbb5\0000\00\000c16e8";
        if (!File.Exists(sample))
            return; // skip when Master Duel is not installed

        var classData = ClassDataLocator.FindClassDataPath();
        var workDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "floowan-it-" + Guid.NewGuid().ToString("N")));
        var bundleCopy = Path.Combine(workDir.FullName, "000c16e8");
        var png = Path.Combine(workDir.FullName, "replacement.png");
        var extracted = Path.Combine(workDir.FullName, "out.png");

        try
        {
            File.Copy(sample, bundleCopy);
            using (var img = new Image<Rgba32>(512, 512, Color.DeepSkyBlue))
                img.SaveAsPng(png);

            using var service = new CardArtBundleService(classData);
            var before = service.ReadTextureInfo(bundleCopy);
            Assert.Equal(512, before.Width);

            service.ReplaceTexture(bundleCopy, png, compression: "lz4");
            var after = service.ReadTextureInfo(bundleCopy);
            Assert.Equal(4, after.Format); // RGBA32
            Assert.Equal(512, after.Width);

            service.ExtractTexturePng(bundleCopy, extracted);
            Assert.True(File.Exists(extracted));
            Assert.True(new FileInfo(extracted).Length > 0);

            // Backup service round-trip
            var backups = new BackupService(Path.Combine(workDir.FullName, "backups"));
            File.Copy(sample, bundleCopy, true);
            var backupPath = backups.BackupBundleFile(bundleCopy, "000c16e8");
            File.Delete(bundleCopy);
            Assert.True(backups.TryRestoreBundleFile(bundleCopy, "000c16e8"));
            Assert.True(File.Exists(bundleCopy));
            Assert.True(File.Exists(backupPath));
        }
        finally
        {
            try { workDir.Delete(true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ClassDataLocator_FindsEmbeddedPackage()
    {
        var path = ClassDataLocator.FindClassDataPath();
        Assert.True(File.Exists(path));
    }
}

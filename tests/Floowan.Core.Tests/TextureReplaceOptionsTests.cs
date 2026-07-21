using Floowan.Core.Assets;
using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class TextureReplaceOptionsTests
{
    [Fact]
    public void OverFrame_Constants_Are_704x1024()
    {
        Assert.Equal(704, OverFrameConstants.Width);
        Assert.Equal(1024, OverFrameConstants.Height);
        Assert.Equal(704, TextureReplaceOptions.OverFrame.Width);
        Assert.Equal(1024, TextureReplaceOptions.OverFrame.Height);
    }

    [Fact]
    public void Validate_Warns_When_Not_Exact_Overframe_Size()
    {
        var path = Path.Combine(Path.GetTempPath(), "floowan-of-img-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var img = new Image<Rgba32>(100, 200, Color.Blue))
                img.SaveAsPng(path);

            var result = ImagePreparation.Validate(path, OverFrameConstants.Width, OverFrameConstants.Height);
            Assert.True(result.IsValid);
            Assert.NotNull(result.Warning);
            Assert.Contains("704", result.Warning);
            Assert.Contains("1024", result.Warning);

            var bytes = ImagePreparation.PrepareRgba32TextureBytes(path, OverFrameConstants.Width, OverFrameConstants.Height);
            Assert.Equal(OverFrameConstants.Width * OverFrameConstants.Height * 4, bytes.Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReplaceTexture_OverrideSize_WhenGameBundleAvailable()
    {
        var sample = @"D:\Games\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\LocalData\2addcbb5\0000\00\000c16e8";
        if (!File.Exists(sample))
            return;

        var classData = ClassDataLocator.FindClassDataPath();
        var workDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "floowan-of-it-" + Guid.NewGuid().ToString("N")));
        var bundleCopy = Path.Combine(workDir.FullName, "000c16e8");
        var png = Path.Combine(workDir.FullName, "of.png");

        try
        {
            File.Copy(sample, bundleCopy);
            using (var img = new Image<Rgba32>(704, 1024, Color.Orange))
                img.SaveAsPng(png);

            using var service = new CardArtBundleService(classData);
            service.ReplaceTexture(bundleCopy, png, TextureReplaceOptions.OverFrame);
            var after = service.ReadTextureInfo(bundleCopy);
            Assert.Equal(4, after.Format); // RGBA32
            Assert.Equal(704, after.Width);
            Assert.Equal(1024, after.Height);
        }
        finally
        {
            try { workDir.Delete(true); } catch { /* ignore */ }
        }
    }
}

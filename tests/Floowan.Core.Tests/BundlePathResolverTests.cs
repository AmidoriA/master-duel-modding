using Floowan.Core.Game;

namespace Floowan.Core.Tests;

public class BundlePathResolverTests
{
    [Fact]
    public void LocalDataPath_UsesFirstTwoCharsAsFolder()
    {
        var path = BundlePathResolver.GetLocalDataBundlePath(@"D:\Game\LocalData\abc", "000c16e8");
        Assert.Equal(Path.Combine(@"D:\Game\LocalData\abc", "0000", "00", "000c16e8"), path);
    }

    [Fact]
    public void StreamingAssetsRoot_UsesInstallRoot()
    {
        var player = @"D:\Games\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\LocalData\playerid";
        var root = BundlePathResolver.GetStreamingAssetsRoot(player);
        Assert.Equal(
            Path.Combine(@"D:\Games\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel", "masterduel_Data", "StreamingAssets", "AssetBundle"),
            root);
    }

    [Fact]
    public void StreamingAssetsPath_UsesInstallRoot()
    {
        var player = @"D:\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\LocalData\playerid";
        var path = BundlePathResolver.GetStreamingAssetsBundlePath(player, "abcd1234");
        Assert.Equal(
            Path.Combine(@"D:\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel", "masterduel_Data", "StreamingAssets", "AssetBundle", "ab", "abcd1234"),
            path);
    }

    [Fact]
    public void ResolveExisting_ThrowsWhenMissing()
    {
        var ex = Assert.Throws<FileNotFoundException>(() =>
            BundlePathResolver.ResolveExistingBundlePath(Path.GetTempPath(), "deadbeef"));
        Assert.Contains("deadbeef", ex.Message);
    }
}

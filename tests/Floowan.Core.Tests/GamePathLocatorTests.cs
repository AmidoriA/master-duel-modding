using Floowan.Core.Game;

namespace Floowan.Core.Tests;

public class GamePathLocatorTests
{
    [Fact]
    public void IsValidGamePath_RejectsEmpty()
    {
        Assert.False(GamePathLocator.IsValidGamePath("", out var error));
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsValidGamePath_RejectsMissingUnity()
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "floowan-gp-" + Guid.NewGuid().ToString("N")));
        try
        {
            Assert.False(GamePathLocator.IsValidGamePath(temp.FullName, out var error));
            Assert.Contains("data.unity3d", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            temp.Delete(true);
        }
    }

    [Fact]
    public void ResolveInstallRoot_WalksUpFromLocalData()
    {
        var player = @"C:\Games\Yu-Gi-Oh!  Master Duel\LocalData\abcdef01";
        var install = GamePathLocator.ResolveInstallRoot(player);
        Assert.Equal(@"C:\Games\Yu-Gi-Oh!  Master Duel", install);
    }

    [Fact]
    public void ResolveUnity3dPath_PointsAtMasterduelData()
    {
        var player = @"C:\Games\Yu-Gi-Oh!  Master Duel\LocalData\abcdef01";
        var unity = GamePathLocator.ResolveUnity3dPath(player);
        Assert.Equal(
            Path.Combine(@"C:\Games\Yu-Gi-Oh!  Master Duel", "masterduel_Data", "data.unity3d"),
            unity);
    }
}

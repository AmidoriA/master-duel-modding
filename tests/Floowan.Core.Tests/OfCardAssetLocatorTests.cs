using Floowan.Core.Assets;

namespace Floowan.Core.Tests;

public class OfCardAssetLocatorTests
{
    [Theory]
    [InlineData("a589d3b5", true)]
    [InlineData("22817d01", true)]
    [InlineData("ABCDEF12", true)]
    [InlineData("a589d3b5.gatebak", false)]
    [InlineData("a589d3b5.bak", false)]
    [InlineData("of_card_asset", false)]
    [InlineData("deadbeef0", false)]
    [InlineData("abcd123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLiveBundleFileName_OnlyEightHexDigits(string? name, bool expected)
    {
        Assert.Equal(expected, OfCardAssetLocator.IsLiveBundleFileName(name));
    }

    [Fact]
    public void DefaultBundleId_IsCurrentLivePostPatchGate()
    {
        Assert.Equal("22817d01", OfCardAssetLocator.DefaultBundleId);
        Assert.True(OfCardAssetLocator.IsLiveBundleFileName(OfCardAssetLocator.DefaultBundleId));
    }
}

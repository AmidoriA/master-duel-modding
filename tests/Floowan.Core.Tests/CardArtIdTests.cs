using Floowan.Core.Assets;

namespace Floowan.Core.Tests;

public class CardArtIdTests
{
    [Theory]
    [InlineData("22811", 22811)]
    [InlineData("4007", 4007)]
    [InlineData("1", 1)]
    public void TryParse_Accepts_Numeric_Texture_Names(string name, int expected)
    {
        Assert.True(CardArtId.TryParse(name, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P22811")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void TryParse_Rejects_Non_Art_Ids(string? name)
    {
        Assert.False(CardArtId.TryParse(name, out _));
    }
}

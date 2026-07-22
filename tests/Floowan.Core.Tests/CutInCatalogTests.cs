using Floowan.Core.Spine;

namespace Floowan.Core.Tests;

public class CutInCatalogTests
{
    [Fact]
    public void LoadEmbedded_ResolvesVerteAnaconda()
    {
        var catalog = CutInCatalog.LoadEmbedded();
        Assert.True(catalog.Count >= 400);
        Assert.True(catalog.TryResolveByCardName("Predaplant Verte Anaconda", out var id));
        Assert.Equal(14944, id);
        Assert.True(catalog.HasCutInId(14944));
        Assert.True(catalog.TryGetName(19375, out var name));
        Assert.Contains("Legatia", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownCard_DoesNotResolve()
    {
        var catalog = CutInCatalog.LoadEmbedded();
        Assert.False(catalog.TryResolveByCardName("Definitely Not A Real Card XYZ", out _));
    }
}

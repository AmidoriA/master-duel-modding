using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class FrameLoreLayoutTests
{
    [Fact]
    public void EffectLoreConstants_MatchEffectTemplateDetection()
    {
        using var effect = CardFrameTemplates.Load(CardFrameStyle.Effect);
        var art = OverFrameAutoArtComposer.DetectArtWindow(effect);
        var text = OverFrameAutoArtComposer.DetectTextBox(effect, art);
        var outer = OverFrameAutoArtComposer.DetectLoreOuterBorder(effect, text);
        var cut = OverFrameAutoArtComposer.DetectLoreOuterTop(effect, text, art);

        Assert.Equal(OverFrameAutoArtComposer.EffectLoreCream, text);
        Assert.Equal(OverFrameAutoArtComposer.EffectLoreOuter, outer);
        Assert.Equal(OverFrameAutoArtComposer.EffectLoreCutTop, cut);
    }

    [Theory]
    [InlineData(CardFrameStyle.PendulumNormal)]
    [InlineData(CardFrameStyle.PendulumEffect)]
    [InlineData(CardFrameStyle.PendulumFusion)]
    [InlineData(CardFrameStyle.PendulumSynchro)]
    [InlineData(CardFrameStyle.PendulumXyz)]
    [InlineData(CardFrameStyle.PendulumToken)]
    public void PendulumTemplates_HaveWiderShorterArtHole(CardFrameStyle style)
    {
        using var pend = CardFrameTemplates.Load(style);
        var art = OverFrameAutoArtComposer.DetectArtWindow(pend);
        var layout = OverFrameAutoArtComposer.GetPendulumLayout(style);
        Assert.False(art.IsEmpty);
        Assert.Equal(layout.ArtWindow.Width, art.Width);
        Assert.Equal(layout.ArtWindow.Height, art.Height);
        Assert.Equal(layout.ArtWindow.X, art.X);
        Assert.Equal(layout.ArtWindow.Y, art.Y);
        Assert.True(art.Width > OverFrameAutoArtComposer.ArtWindow.Width);
        Assert.True(art.Height < OverFrameAutoArtComposer.ArtWindow.Height);
        Assert.Equal(OverFrameAutoArtComposer.PendulumLoreCream, layout.LoreCream);
        Assert.Equal(OverFrameAutoArtComposer.PendulumLoreCutTop, layout.LoreCutTop);
        Assert.Equal(new Rectangle(50, 186, 604, 451), OverFrameAutoArtComposer.PendulumArtWindow);
        Assert.Equal(new Rectangle(27, 645, 627, 114), OverFrameAutoArtComposer.PendulumMintTextBox);
        Assert.Equal(new Rectangle(27, 766, 627, 196), OverFrameAutoArtComposer.PendulumMonsterLoreCream);
        Assert.Equal(new Rectangle(27, 645, 627, 317), OverFrameAutoArtComposer.PendulumLoreCream);
        Assert.Equal(56, OverFrameAutoArtComposer.PendulumVerticalOffset);
        // DetectTextBox alone only finds the mint strip — compose must use the union.
        var detected = OverFrameAutoArtComposer.DetectTextBox(pend, art);
        Assert.False(detected.IsEmpty);
        Assert.Equal(645, detected.Y);
        Assert.True(detected.Height < 150, $"DetectTextBox should stop at mint strip, got {detected}");
        Assert.True(detected.Bottom <= OverFrameAutoArtComposer.PendulumMonsterLoreCream.Top,
            $"DetectTextBox must not include bottom monster lore, got {detected}");
    }

    [Theory]
    [InlineData(CardFrameStyle.Normal)]
    [InlineData(CardFrameStyle.Synchro)]
    [InlineData(CardFrameStyle.Link)]
    [InlineData(CardFrameStyle.Fusion)]
    [InlineData(CardFrameStyle.Xyz)]
    [InlineData(CardFrameStyle.Ritual)]
    [InlineData(CardFrameStyle.Spell)]
    [InlineData(CardFrameStyle.Trap)]
    [InlineData(CardFrameStyle.Token)]
    public void Compose_NonEffectStyles_UseEffectLoreWingAndCutGeometry(CardFrameStyle style)
    {
        // Narrow center subject — lore side wings must keep chrome (Effect layout), not be
        // wiped by bad per-style cream detection (Synchro thin strip / Link empty / Normal wide).
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 100; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(20, 200, 50, 255);
            mask[x, y] = new L8(255);
        }

        using var result = OverFrameAutoArtComposer.Compose(source, mask, style);

        var cream = OverFrameAutoArtComposer.EffectLoreCream;
        var outer = OverFrameAutoArtComposer.EffectLoreOuter;
        var cut = OverFrameAutoArtComposer.EffectLoreCutTop;
        var midX = cream.Left + cream.Width / 2;
        var loreY = cream.Top + 40;
        var wingY = loreY;
        var leftWingX = (outer.Left + cream.Left) / 2;
        var rightWingX = (cream.Right + outer.Right) / 2;

        using var frame = CardFrameTemplates.Load(style);
        _ = frame; // style template loads (ensures asset exists)

        // Type-line band uses shared Effect ArtWindow bottom → Effect cut.
        var art = OverFrameAutoArtComposer.ArtWindow;
        if (cut > art.Bottom + 5)
        {
            var typeLine = result[midX, (art.Bottom + cut) / 2];
            Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, typeLine.A);
        }

        var lore = result[midX, loreY];
        Assert.True(lore.A >= 200, $"{style}: lore interior must stay opaque, got {lore}");

        var leftWing = result[leftWingX, wingY];
        Assert.True(leftWing.A >= 200, $"{style}: left lore wing must stay chrome without subject, got {leftWing}");

        var rightWing = result[rightWingX, wingY];
        Assert.True(rightWing.A >= 200, $"{style}: right lore wing must stay chrome without subject, got {rightWing}");
    }

    [Theory]
    [InlineData(CardFrameStyle.Synchro)]
    [InlineData(CardFrameStyle.Link)]
    public void Compose_OtherStyles_MatchEffectArtWindowScale(CardFrameStyle style)
    {
        using var source = new Image<Rgba32>(100, 100, new Rgba32(10, 180, 40, 255));
        using var mask = new Image<L8>(100, 100, new L8(0));
        for (var y = 0; y < 100; y++)
        for (var x = 40; x < 60; x++)
        {
            source[x, y] = new Rgba32(20, 200, 50, 255);
            mask[x, y] = new L8(255);
        }

        using var result = OverFrameAutoArtComposer.Compose(source, mask, style);
        using var effect = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.Effect);

        var hole = OverFrameAutoArtComposer.ArtWindow;
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, result[hole.Left + 8, hole.Top + 8].A);
        Assert.Equal(effect[hole.Left + 8, hole.Top + 8].A, result[hole.Left + 8, hole.Top + 8].A);

        var midX = hole.Left + hole.Width / 2;
        var typeY = (hole.Bottom + OverFrameAutoArtComposer.EffectLoreCutTop) / 2;
        Assert.Equal(OverFrameAutoArtComposer.FoilMaskAlpha, result[midX, typeY].A);
        Assert.Equal(effect[midX, typeY].A, result[midX, typeY].A);
    }
}

using Floowan.Core.Data;
using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class OfGradientBorderComposerTests
{
    [Theory]
    [InlineData(CardFrameStyle.OfGradientEffect, CardFrameStyle.Effect)]
    [InlineData(CardFrameStyle.OfGradientSpell, CardFrameStyle.Spell)]
    [InlineData(CardFrameStyle.OfGradientRitual, CardFrameStyle.Ritual)]
    [InlineData(CardFrameStyle.OfGradientLink, CardFrameStyle.Link)]
    [InlineData(CardFrameStyle.OfGradientPendulumEffect, CardFrameStyle.PendulumEffect)]
    [InlineData(CardFrameStyle.OfGradientPendulumFusion, CardFrameStyle.PendulumFusion)]
    public void GetSolidBaseStyle_MapsOfGradientToSolid(CardFrameStyle ofGradient, CardFrameStyle expected)
    {
        Assert.True(CardFrameTemplates.IsOfGradientStyle(ofGradient));
        Assert.False(CardFrameTemplates.IsOfGradientStyle(expected));
        Assert.Equal(expected, CardFrameTemplates.GetSolidBaseStyle(ofGradient));
        Assert.Equal(expected, CardFrameTemplates.GetSolidBaseStyle(expected));
    }

    [Fact]
    public void FileNames_Registered_ForAllOfGradientStyles()
    {
        foreach (CardFrameStyle style in Enum.GetValues<CardFrameStyle>())
        {
            if (!CardFrameTemplates.IsOfGradientStyle(style))
                continue;

            var name = CardFrameTemplates.GetFileName(style);
            Assert.StartsWith("OfGradient", name, StringComparison.Ordinal);
            Assert.EndsWith(".png", name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToLabel_RoundTrips_OfGradientStyles()
    {
        foreach (CardFrameStyle style in Enum.GetValues<CardFrameStyle>())
        {
            if (!CardFrameTemplates.IsOfGradientStyle(style))
                continue;

            var label = CardTypeLabels.ToLabel(style);
            Assert.StartsWith("OF Gradient", label, StringComparison.Ordinal);
            Assert.True(CardTypeLabels.TryParseStyle(label, out var parsed), label);
            Assert.Equal(style, parsed);
        }
    }

    [Fact]
    public void InferStyle_NeverReturnsOfGradient()
    {
        Assert.Equal(CardFrameStyle.Effect, CardFrameTemplates.InferStyle("A", "[Fiend/Effect]"));
        Assert.Equal(CardFrameStyle.Spell, CardFrameTemplates.InferStyle("S", "Spell Card"));
        Assert.Equal(CardFrameStyle.PendulumEffect, CardFrameTemplates.InferStyle("P", "[Dragon/Pendulum/Effect]"));
        Assert.False(CardFrameTemplates.IsOfGradientStyle(CardFrameTemplates.InferStyle("A", "[Fiend/Effect]")!.Value));
    }

    [Theory]
    [InlineData(CardFrameStyle.OfGradientEffect)]
    [InlineData(CardFrameStyle.OfGradientSpell)]
    [InlineData(CardFrameStyle.OfGradientRitual)]
    public void Apply_BrightensOuterRim_PreservesArtHoleAndLoreCream(CardFrameStyle ofGradient)
    {
        var solidStyle = CardFrameTemplates.GetSolidBaseStyle(ofGradient);
        using var solid = CardFrameTemplates.Load(solidStyle);
        using var derived = OfGradientBorderComposer.Apply(solid, ofGradient);

        Assert.Equal(solid.Width, derived.Width);
        Assert.Equal(solid.Height, derived.Height);

        var art = OverFrameAutoArtComposer.ArtWindow;
        var hole = derived[art.Left + 8, art.Top + 8];
        Assert.True(hole.A <= OverFrameAutoArtComposer.VisibleAlphaThreshold, $"art hole must stay empty, got {hole}");

        var cream = OverFrameAutoArtComposer.EffectLoreCream;
        var creamPix = derived[cream.Left + cream.Width / 2, cream.Top + 40];
        var solidCream = solid[cream.Left + cream.Width / 2, cream.Top + 40];
        Assert.Equal(solidCream.R, creamPix.R);
        Assert.Equal(solidCream.G, creamPix.G);
        Assert.Equal(solidCream.B, creamPix.B);

        // Outer corner should be brighter than the solid template (OF light leak).
        var solidCorner = solid[6, 6];
        var derivedCorner = derived[6, 6];
        var solidLum = solidCorner.R + solidCorner.G + solidCorner.B;
        var derivedLum = derivedCorner.R + derivedCorner.G + derivedCorner.B;
        Assert.True(derivedLum > solidLum + 40,
            $"outer rim should brighten ({solidLum} → {derivedLum})");
    }

    [Fact]
    public void Load_OfGradient_ReturnsImageWithArtHole()
    {
        using var frame = CardFrameTemplates.Load(CardFrameStyle.OfGradientSpell);
        Assert.Equal(704, frame.Width);
        Assert.Equal(1024, frame.Height);
        var art = OverFrameAutoArtComposer.ArtWindow;
        Assert.True(frame[art.Left + 10, art.Top + 10].A <= OverFrameAutoArtComposer.VisibleAlphaThreshold);
    }

    [Fact]
    public void IsPendulumStyle_IncludesOfGradientPendulum()
    {
        Assert.True(CardFrameTemplates.IsPendulumStyle(CardFrameStyle.OfGradientPendulumEffect));
        Assert.False(CardFrameTemplates.IsPendulumStyle(CardFrameStyle.OfGradientEffect));
        Assert.True(LinkArrowOverlay.NeedsArrowOverlay(CardFrameStyle.OfGradientLink));
        Assert.False(LinkArrowOverlay.NeedsArrowOverlay(CardFrameStyle.OfGradientSpell));
    }

    [Fact]
    public void Compose_OfGradientEffect_KeepsMirrorjadeSoftLore()
    {
        using var source = new Image<Rgba32>(120, 160, new Rgba32(20, 180, 220, 255));
        using var mask = new Image<L8>(120, 160, new L8(0));
        // Tall subject that overflows the art hole into lore.
        for (var y = 0; y < 160; y++)
        for (var x = 40; x < 80; x++)
            mask[x, y] = new L8(255);

        using var result = OverFrameAutoArtComposer.Compose(source, mask, CardFrameStyle.OfGradientEffect);
        var cream = OverFrameAutoArtComposer.EffectLoreCream;
        var midX = cream.Left + cream.Width / 2;
        var coveredY = cream.Top + 20;
        var p = result[midX, coveredY];
        // Soft Mirrorjade underlay: not foil A≈4, not fully opaque vanilla cream alone.
        Assert.True(p.A > OverFrameAutoArtComposer.VisibleAlphaThreshold);
        Assert.NotEqual(OverFrameAutoArtComposer.FoilMaskAlpha, p.A);
    }
}

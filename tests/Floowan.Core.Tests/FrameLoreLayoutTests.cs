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
        Assert.Equal(200, OverFrameAutoArtComposer.PendulumVerticalOffset);
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

    [Fact]
    public void RitualTemplate_IsNotLinkAsset()
    {
        // Regression: pendulum-overframe briefly swapped Ritual↔Link (and Link↔Token).
        // Ritual = card_frame02 (plain blue); Link = card_frame18 (hex + arrow markers).
        using var ritual = CardFrameTemplates.Load(CardFrameStyle.Ritual);
        using var link = CardFrameTemplates.Load(CardFrameStyle.Link);

        Assert.Equal(704, ritual.Width);
        Assert.Equal(1024, ritual.Height);
        Assert.Equal(link.Width, ritual.Width);
        Assert.Equal(link.Height, ritual.Height);

        // Both use the square monster art hole (~Effect), not Pendulum's wider/shorter window.
        // Compose still punches with shared Effect ArtWindow (detection can be ±1px).
        var ritualHole = OverFrameAutoArtComposer.DetectArtWindow(ritual);
        var linkHole = OverFrameAutoArtComposer.DetectArtWindow(link);
        var effectHole = OverFrameAutoArtComposer.ArtWindow;
        Assert.InRange(ritualHole.Width, effectHole.Width - 2, effectHole.Width + 2);
        Assert.InRange(ritualHole.Height, effectHole.Height - 2, effectHole.Height + 2);
        Assert.InRange(linkHole.Width, effectHole.Width - 2, effectHole.Width + 2);
        Assert.InRange(linkHole.Height, effectHole.Height - 2, effectHole.Height + 2);
        Assert.True(ritualHole.Width < OverFrameAutoArtComposer.PendulumArtWindow.Width);
        Assert.True(ritualHole.Height > OverFrameAutoArtComposer.PendulumArtWindow.Height);

        // Distinctive pixels: Link arrows are near-black triangles on art mid-edges;
        // Ritual namebar is medium blue without those markers.
        var ritualName = ritual[352, 80];
        var linkName = link[352, 80];
        Assert.True(ritualName.A > 200 && ritualName.B > ritualName.R + 40,
            $"Ritual namebar should be blue, got {ritualName}");
        Assert.True(linkName.A > 200 && linkName.B > linkName.R,
            $"Link namebar should be blue-cyan, got {linkName}");

        static int CountDarkArrowPixels(Image<Rgba32> img)
        {
            // Mid-edge / corner arrow zones around the shared art window.
            var regions = new[]
            {
                (340, 175, 24, 20), (50, 440, 20, 24), (634, 440, 20, 24), (340, 640, 24, 20),
                (55, 195, 30, 30), (620, 195, 30, 30), (55, 615, 30, 30), (620, 615, 30, 30),
            };
            var dark = 0;
            foreach (var (x0, y0, w, h) in regions)
            {
                for (var y = y0; y < y0 + h; y++)
                for (var x = x0; x < x0 + w; x++)
                {
                    var c = img[x, y];
                    if (c.A > 200 && c.R < 60 && c.G < 60 && c.B < 80)
                        dark++;
                }
            }

            return dark;
        }

        var ritualDark = CountDarkArrowPixels(ritual);
        var linkDark = CountDarkArrowPixels(link);
        Assert.True(linkDark > 800, $"Link should have dark arrow markers, got {linkDark}");
        Assert.True(ritualDark < 400, $"Ritual must not have Link arrow markers, got {ritualDark}");
        Assert.True(linkDark > ritualDark * 3,
            $"Link arrow density ({linkDark}) must far exceed Ritual ({ritualDark})");

        // Byte-level inequality (hash-equivalent check without shipping expected digests).
        Assert.False(ImagesEqual(ritual, link), "Ritual.png must not be identical to Link.png");

        Assert.Equal(CardFrameStyle.Ritual, CardFrameTemplates.InferStyle("Relinquished", "[Spellcaster/Ritual/Effect]"));
        Assert.Equal(CardFrameStyle.Link, CardFrameTemplates.InferStyle("Accesscode", "[Cyberse/Link/Effect]"));
    }

    private static bool ImagesEqual(Image<Rgba32> a, Image<Rgba32> b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return false;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            if (!a[x, y].Equals(b[x, y]))
                return false;
        }

        return true;
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

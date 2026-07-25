using Floowan.Core.Assets;
using Floowan.Core.Data;
using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class LinkMarkerAndArrowOverlayTests
{
    // Known CARD_Prop records (bytes 0-7) from Master Duel data.unity3d dumps.
    // Packed ATK/DEF at bytes 4-7: ATK/10 in bits 0-8, markers in bits 9-17 (low 8).
    public static TheoryData<string, byte[], LinkMarkerMask, int> KnownLinkProps => new()
    {
        // Link Spider — Bottom only; ATK 1000
        {
            "Link Spider",
            [0xEA, 0x32, 0x6B, 0xC5, 0x64, 0x80, 0xE0, 0x02],
            LinkMarkerMask.Down,
            1000
        },
        // Accesscode Talker — Top+Left+Right+Bottom; ATK 2300
        {
            "Accesscode Talker",
            [0xB8, 0x3A, 0xAB, 0xD0, 0xE6, 0xB4, 0xE0, 0x02],
            LinkMarkerMask.Up | LinkMarkerMask.Left | LinkMarkerMask.Right | LinkMarkerMask.Down,
            2300
        },
        // Honeybot — Left+Right; ATK 1900
        {
            "Honeybot",
            [0xEB, 0x32, 0x6B, 0xC8, 0xBE, 0x30, 0xE0, 0x02],
            LinkMarkerMask.Left | LinkMarkerMask.Right,
            1900
        },
        // Decode Talker — Top+BL+BR; ATK 2300
        {
            "Decode Talker",
            [0xEC, 0x32, 0xAB, 0xCC, 0xE6, 0x44, 0xE1, 0x02],
            LinkMarkerMask.Up | LinkMarkerMask.DownLeft | LinkMarkerMask.DownRight,
            2300
        },
        // Imduk — Top; ATK 800
        {
            "Imduk",
            [0x1E, 0x33, 0xAB, 0xC5, 0x50, 0x04, 0x20, 0x00],
            LinkMarkerMask.Up,
            800
        },
        // Secure Gardna — Right; ATK 1000
        {
            "Secure Gardna",
            [0x66, 0x34, 0x6B, 0xC4, 0x64, 0x20, 0xE0, 0x02],
            LinkMarkerMask.Right,
            1000
        },
    };

    [Theory]
    [MemberData(nameof(KnownLinkProps))]
    public void CardProp_DecodeLinkMarkers_MatchesKnownCards(
        string name,
        byte[] record,
        LinkMarkerMask expected,
        int expectedAtk)
    {
        Assert.Equal(8, record.Length);
        var packed = (uint)(
            record[4] | (record[5] << 8) | (record[6] << 16) | (record[7] << 24));
        Assert.Equal("Link", CardPropTypeDecoder.InferLabel(record[2], record[3]));
        Assert.Equal(expectedAtk, CardPropTypeDecoder.DecodeAtk(packed));
        Assert.Equal(expected, CardPropTypeDecoder.DecodeLinkMarkers(packed));
        _ = name;
    }

    [Fact]
    public void CardProp_ParseLinkMarkerMap_ReadsSyntheticBlob()
    {
        // Header (8 bytes) + Accesscode record
        var blob = new byte[16];
        var accesscode = KnownLinkProps.First(r => (string)r[0]! == "Accesscode Talker");
        var record = (byte[])accesscode[1]!;
        record.CopyTo(blob, 8);

        var map = CardPropTypeDecoder.ParseLinkMarkerMap(blob);
        Assert.True(map.TryGetValue(15032, out var markers));
        Assert.Equal(
            LinkMarkerMask.Up | LinkMarkerMask.Left | LinkMarkerMask.Right | LinkMarkerMask.Down,
            markers);
    }

    [Fact]
    public void LinkMarkerMask_RoundTripsYgoPro()
    {
        var ygo = LinkMarkerMaskConvert.YgoProTop
                  | LinkMarkerMaskConvert.YgoProLeft
                  | LinkMarkerMaskConvert.YgoProRight
                  | LinkMarkerMaskConvert.YgoProBottom;
        var md = LinkMarkerMaskConvert.FromYgoProMask(ygo);
        Assert.Equal(
            LinkMarkerMask.Up | LinkMarkerMask.Left | LinkMarkerMask.Right | LinkMarkerMask.Down,
            md);
        Assert.Equal(ygo, LinkMarkerMaskConvert.ToYgoProMask(md));
    }

    [Fact]
    public void LinkArrowOverlay_Apply_DrawsOnlyActiveDirectionsAsLitOrange()
    {
        using var canvas = new Image<Rgba32>(
            OverFrameConstants.Width,
            OverFrameConstants.Height,
            new Rgba32(200, 200, 200, 255));

        // Only Bottom arrow (Link Spider).
        LinkArrowOverlay.Apply(canvas, LinkMarkerMask.Down);

        var bottom = CountLitOrange(canvas, new Rectangle(296, 715, 113, 48));
        var top = CountLitOrange(canvas, new Rectangle(296, 147, 113, 47));
        var left = CountLitOrange(canvas, new Rectangle(45, 396, 46, 118));
        Assert.True(bottom > 200, $"Bottom arrow should be lit orange, got {bottom} lit px");
        Assert.True(top < 30, $"Top arrow must stay off, got {top}");
        Assert.True(left < 30, $"Left arrow must stay off, got {left}");
    }

    [Fact]
    public void LinkArrowOverlay_Apply_MasquerenaMask_LightsOnlyBottomDiagonals()
    {
        // I:P Masquerena catalog mask 160 = SW + SE. Start from the real Link frame
        // (all eight dark inactive markers), then light only the active bits.
        var masquerena = LinkMarkerMask.DownLeft | LinkMarkerMask.DownRight;
        Assert.Equal(160, (byte)masquerena);

        using var canvas = CardFrameTemplates.Load(CardFrameStyle.Link);
        var dlBefore = CountLitOrange(canvas, new Rectangle(41, 656, 108, 107));
        var drBefore = CountLitOrange(canvas, new Rectangle(551, 656, 108, 107));
        var upBefore = CountLitOrange(canvas, new Rectangle(296, 147, 113, 47));
        var leftBefore = CountLitOrange(canvas, new Rectangle(45, 396, 46, 118));
        var downBefore = CountLitOrange(canvas, new Rectangle(296, 715, 113, 48));

        LinkArrowOverlay.Apply(canvas, masquerena);

        var dlDelta = CountLitOrange(canvas, new Rectangle(41, 656, 108, 107)) - dlBefore;
        var drDelta = CountLitOrange(canvas, new Rectangle(551, 656, 108, 107)) - drBefore;
        var upDelta = CountLitOrange(canvas, new Rectangle(296, 147, 113, 47)) - upBefore;
        var leftDelta = CountLitOrange(canvas, new Rectangle(45, 396, 46, 118)) - leftBefore;
        var downDelta = CountLitOrange(canvas, new Rectangle(296, 715, 113, 48)) - downBefore;

        // Largest-CC triangle glyph is ~780–820 px per corner (not the full L-bevel).
        Assert.True(dlDelta > 500, $"SW triangle should gain lit orange, delta {dlDelta}");
        Assert.True(drDelta > 500, $"SE triangle should gain lit orange, delta {drDelta}");
        Assert.True(Math.Abs(upDelta) < 40, $"Top must stay dark/inactive, delta {upDelta}");
        Assert.True(Math.Abs(leftDelta) < 40, $"Left must stay dark/inactive, delta {leftDelta}");
        Assert.True(Math.Abs(downDelta) < 40, $"Bottom must stay dark/inactive, delta {downDelta}");
    }

    [Fact]
    public void LinkArrowOverlay_Apply_CompositesMetalBorderBlackInsetAndGlow()
    {
        // Active anatomy: drop halo → bright silver/white rim → black inset → orange fill.
        // Must NOT flood the card L-corner chrome orange.
        using var canvas = new Image<Rgba32>(
            OverFrameConstants.Width,
            OverFrameConstants.Height,
            new Rgba32(80, 120, 180, 255)); // bright blue so shadow darkening is obvious
        LinkArrowOverlay.Apply(
            canvas,
            LinkMarkerMask.DownLeft | LinkMarkerMask.DownRight | LinkMarkerMask.Up);

        Assert.True(IsLitOrange(canvas[69, 700]), "DL triangle glyph should be lit");
        Assert.True(IsLitOrange(canvas[635, 700]), "DR triangle glyph should be lit");
        Assert.True(IsLitOrange(canvas[351, 159]), "Up triangle glyph should be lit");

        Assert.True(IsBrightRim(canvas[65, 692]), "DL should gain bright silver/white rim");
        Assert.True(IsBrightRim(canvas[632, 692]), "DR should gain bright silver/white rim");
        Assert.True(IsBrightRim(canvas[350, 151]), "Up should gain bright silver/white rim");

        Assert.True(IsBlackInset(canvas[68, 698]), "DL black inset between rim and glow");
        Assert.True(IsBlackInset(canvas[635, 698]), "DR black inset between rim and glow");

        // Soft drop halo outside the rim darkens the blue background.
        Assert.True(IsDarkenedByShadow(canvas[67, 688], new Rgba32(80, 120, 180, 255)),
            "DL should cast a dark drop halo");
        Assert.True(IsDarkenedByShadow(canvas[349, 148], new Rgba32(80, 120, 180, 255)),
            "Up should cast a dark drop halo");

        // Card L-corner chrome outside the triangular housing must stay non-orange.
        Assert.False(IsLitOrange(canvas[45, 700]), "Far DL L-corner must not be orange");
        Assert.False(IsLitOrange(canvas[660, 700]), "Far DR L-corner must not be orange");
    }

    [Fact]
    public void LinkArrowOverlay_Apply_DiagonalRimIsSmooth()
    {
        // Integer disk-dilate of jagged Link.png glyphs made L/R/SW rims wobble
        // (thick-thin stair-steps). Geometric AA SDF should keep SW hypotenuse rim
        // thickness stable along the long diagonal.
        using var canvas = new Image<Rgba32>(
            OverFrameConstants.Width,
            OverFrameConstants.Height,
            new Rgba32(40, 50, 70, 255));
        LinkArrowOverlay.Apply(canvas, LinkMarkerMask.DownLeft | LinkMarkerMask.Left);

        static int RimThicknessAlongRay(
            Image<Rgba32> img,
            float x0,
            float y0,
            float dx,
            float dy)
        {
            var len = MathF.Sqrt(dx * dx + dy * dy);
            dx /= len;
            dy /= len;
            var first = -1;
            var last = -1;
            for (var t = 0; t < 48; t++)
            {
                var x = (int)MathF.Round(x0 + dx * t);
                var y = (int)MathF.Round(y0 + dy * t);
                if ((uint)x >= (uint)img.Width || (uint)y >= (uint)img.Height)
                    break;
                if (IsBrightRim(img[x, y]))
                {
                    if (first < 0)
                        first = t;
                    last = t;
                }
                else if (first >= 0 && t > last + 2)
                    break;
            }

            return first < 0 ? 0 : last - first + 1;
        }

        // Rays roughly perpendicular to the SW hypotenuse (tip ~69,738 → NE).
        var swA = RimThicknessAlongRay(canvas, 58f, 742f, 0.7f, -0.7f);
        var swB = RimThicknessAlongRay(canvas, 72f, 728f, 0.7f, -0.7f);
        var swC = RimThicknessAlongRay(canvas, 86f, 714f, 0.7f, -0.7f);
        Assert.True(swA is >= 5 and <= 16, $"SW rim thickness A={swA}");
        Assert.True(swB is >= 5 and <= 16, $"SW rim thickness B={swB}");
        Assert.True(swC is >= 5 and <= 16, $"SW rim thickness C={swC}");
        Assert.True(Math.Abs(swA - swB) <= 4 && Math.Abs(swB - swC) <= 4,
            $"SW rim wobble too high: {swA},{swB},{swC}");

        // Left arrow still lights and keeps a continuous bright rim near the tip.
        Assert.True(IsLitOrange(canvas[58, 453]), "Left tip fill should be lit");
        Assert.True(IsBrightRim(canvas[80, 408]) || IsBrightRim(canvas[70, 420]),
            "Left should gain a bright rim");
    }

    [Fact]
    public void LinkArrowOverlay_ToLitArrowPixel_CenterBrighterThanEdge()
    {
        var ink = new Rgba32(8, 8, 10, 255);
        var center = LinkArrowOverlay.ToLitArrowPixel(ink, edgeT: 0f);
        var edge = LinkArrowOverlay.ToLitArrowPixel(ink, edgeT: 1f);
        Assert.True(center.R > 200 && center.G > 180,
            $"Center should be bright yellow-orange, got {center}");
        Assert.True(edge.R > 200 && edge.G < center.G && edge.G > edge.B,
            $"Edge should be deeper red-orange, got {edge}");
        Assert.True(center.G - edge.G > 40, "Center must be clearly brighter/yellower than edge");
    }

    [Fact]
    public void LinkArrowOverlay_ToMetalPixel_OuterBrighterThanInner()
    {
        var outer = LinkArrowOverlay.ToMetalPixel(1f);
        var inner = LinkArrowOverlay.ToMetalPixel(0f);
        Assert.True(IsBrightRim(outer), $"Outer rim should be bright silver/white, got {outer}");
        Assert.True(IsBrightRim(inner), $"Inner rim should stay light silver, got {inner}");
        Assert.True(outer.R > inner.R && outer.G > inner.G, "Outer rim should be brighter");
        Assert.True(outer.R >= 250 && outer.G >= 250, "Outer rim should be nearly white");
    }

    [Fact]
    public void LinkArrowOverlay_ToShadowPixel_PeaksNearRimAndFadesOut()
    {
        var nearRim = LinkArrowOverlay.ToShadowPixel(8.5f);   // just outside metal r=8
        var mid = LinkArrowOverlay.ToShadowPixel(10.5f);
        var outer = LinkArrowOverlay.ToShadowPixel(12.5f);
        var insideRim = LinkArrowOverlay.ToShadowPixel(6f);   // inside metal — no shadow
        Assert.True(nearRim.A > mid.A && mid.A > outer.A, "Shadow alpha must fall off outward");
        Assert.Equal(0, insideRim.A);
        Assert.True(nearRim.A >= 120, $"Near-rim shadow should be visible, got A={nearRim.A}");
    }

    private static bool IsLitOrange(Rgba32 c) =>
        c.A > 180 && c.R > 160 && c.R > c.G + 15 && c.G > c.B;

    private static bool IsBrightRim(Rgba32 c)
    {
        if (c.A < 180)
            return false;
        var lum = (c.R + c.G + c.B) / 3;
        // Bright neutral silver/white — not orange, not mid-grey dull metal.
        return lum is >= 180 and <= 255
               && Math.Abs(c.R - c.G) <= 25
               && Math.Abs(c.G - c.B) <= 30
               && c.R < c.G + 40;
    }

    private static bool IsBlackInset(Rgba32 c) =>
        c.A > 180 && (c.R + c.G + c.B) / 3 <= 28;

    private static bool IsDarkenedByShadow(Rgba32 painted, Rgba32 originalBg)
    {
        var paintedLum = (painted.R + painted.G + painted.B) / 3;
        var bgLum = (originalBg.R + originalBg.G + originalBg.B) / 3;
        return paintedLum < bgLum - 25 && !IsLitOrange(painted) && !IsBrightRim(painted);
    }

    private static int CountLitOrange(Image<Rgba32> img, Rectangle zone)
    {
        var n = 0;
        for (var y = zone.Top; y < zone.Bottom; y++)
        for (var x = zone.Left; x < zone.Right; x++)
        {
            if (IsLitOrange(img[x, y]))
                n++;
        }

        return n;
    }

    [Fact]
    public void LinkArrowOverlay_NeedsArrowOverlay_OnlyLink()
    {
        Assert.True(LinkArrowOverlay.NeedsArrowOverlay(CardFrameStyle.Link));
        Assert.False(LinkArrowOverlay.NeedsArrowOverlay(CardFrameStyle.Effect));
        Assert.False(LinkArrowOverlay.NeedsArrowOverlay(CardFrameStyle.PendulumEffect));
    }

    [Fact]
    public void BlueEyes_AtkDef_StillDecodeWithNineBitPacking()
    {
        // Blue-Eyes White Dragon prop dump: ATK 3000 DEF 2500
        byte[] record = [0xA7, 0x0F, 0x40, 0x60, 0x2C, 0xF5, 0x21, 0x00];
        var packed = (uint)(
            record[4] | (record[5] << 8) | (record[6] << 16) | (record[7] << 24));
        Assert.Equal(3000, CardPropTypeDecoder.DecodeAtk(packed));
        Assert.Equal(2500, CardPropTypeDecoder.DecodeDef(packed));
    }

    [Fact]
    public void CardLinkMarkerLoader_CancelledToken_ThrowsBeforeScan()
    {
        var loader = new CardLinkMarkerLoader();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            loader.GetOrLoadMap(
                Path.Combine(Path.GetTempPath(), "floowan-missing-localdata"),
                progress: null,
                cts.Token));
    }
}

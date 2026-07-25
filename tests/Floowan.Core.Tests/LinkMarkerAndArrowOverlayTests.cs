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
    public void LinkArrowOverlay_Apply_DrawsOnlyActiveDirections()
    {
        using var canvas = new Image<Rgba32>(
            OverFrameConstants.Width,
            OverFrameConstants.Height,
            new Rgba32(200, 200, 200, 255));

        // Only Bottom arrow (Link Spider).
        LinkArrowOverlay.Apply(canvas, LinkMarkerMask.Down);

        static int CountDark(Image<Rgba32> img, Rectangle zone)
        {
            var n = 0;
            for (var y = zone.Top; y < zone.Bottom; y++)
            for (var x = zone.Left; x < zone.Right; x++)
            {
                var c = img[x, y];
                if (c.A > 180 && c.R < 70 && c.G < 70 && c.B < 90)
                    n++;
            }

            return n;
        }

        var bottom = CountDark(canvas, new Rectangle(296, 715, 113, 48));
        var top = CountDark(canvas, new Rectangle(296, 147, 113, 47));
        var left = CountDark(canvas, new Rectangle(45, 396, 46, 118));
        Assert.True(bottom > 200, $"Bottom arrow should be drawn, got {bottom} dark px");
        Assert.True(top < 30, $"Top arrow must stay off, got {top}");
        Assert.True(left < 30, $"Left arrow must stay off, got {left}");
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

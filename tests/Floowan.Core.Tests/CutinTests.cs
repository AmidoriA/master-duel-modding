using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Floowan.Core.Cutins;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Floowan.Core.Tests;

/// <summary>
/// The C# cut-in port must agree byte for byte with scripts/cutin/monster_cutin.py and video_cutin.py
/// (shared registry and build keys). Expected values below were produced by the Python tools.
/// </summary>
public class CutinTests
{
    [Fact]
    public void HashName_MatchesGameFileNames()
    {
        Assert.Equal("2478d405", CutinNaming.HashName("Duel/Timeline/Duel/MonsterCutIn/tcg/P11016/SD/P11016"));
        Assert.Equal("2478d405", CutinNaming.HashName("#/Duel/Timeline/Duel/MonsterCutIn/tcg/P11016/SD/P11016"));
    }

    [Fact]
    public void CabName_IsMd5OfBundleName()
    {
        Assert.Equal("CAB-35cd5e42fe7a850db230c87ac841b26c", CutinNaming.CabName(CutinNaming.BundleName("2478d405")));
    }

    [Fact]
    public void ManifestCrypt_MatchesPythonAndRoundTrips()
    {
        var plain = Encoding.UTF8.GetBytes("{\"informations\":[]}");
        var enc = CutinNaming.ManifestCrypt(plain);
        Assert.Equal("eeb8aec3ad52053f23d8627b608f1c3365aeb8", Convert.ToHexString(enc).ToLowerInvariant());
        Assert.Equal(plain, CutinNaming.ManifestCrypt(enc));
    }

    [Fact]
    public void PyJson_MatchesPythonDumps()
    {
        var head = new JsonArray(1, 21149, "custom", 19172, (int?)null, false);
        Assert.Equal("[1, 21149, \"custom\", 19172, null, false]", PyJson.Dumps(head));

        var ents = JsonNode.Parse("[{\"dependencies\":[\"a\"],\"assetName\":\"x\u00e9\"}]");
        Assert.Equal("[{\"assetName\": \"x\\u00e9\", \"dependencies\": [\"a\"]}]", PyJson.Dumps(ents, sortKeys: true));

        var man = JsonNode.Parse("{\"informations\":[{\"assetName\":\"\u00e9\",\"dependencies\":[]}]}");
        var compact = Encoding.UTF8.GetBytes(PyJson.Dumps(man, compact: true, ensureAscii: false));
        Assert.Equal("7b22696e666f726d6174696f6e73223a5b7b2261737365744e616d65223a22c3a9222c22646570656e64656e63696573223a5b5d7d5d7d",
            Convert.ToHexString(compact).ToLowerInvariant());
    }

    [Fact]
    public void GridFor_MatchesVideoCutin()
    {
        Assert.Equal((6, 10), VideoFlipbook.GridFor(60, 1924, 1084));
        Assert.Equal((8, 15), VideoFlipbook.GridFor(120, 1924, 1084));
        Assert.Null(VideoFlipbook.GridFor(200, 1924, 1084));
    }

    [Fact]
    public void PickFrames_SpreadsTheClipEvenly()
    {
        var picks = VideoFlipbook.PickFrames(new VideoInfo(1920, 1080, 32, 65 / 32.0), new VideoCutinOptions());
        Assert.Equal(60, picks.Length);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], picks[..8]);
        Assert.Equal([61, 62, 63], picks[^3..]);

        var trimmed = VideoFlipbook.PickFrames(new VideoInfo(1920, 1080, 30, 10), new VideoCutinOptions { Start = 2, End = 4 });
        Assert.Equal(60, trimmed.Length);
        Assert.Equal(60, trimmed[0]);
        Assert.Equal(119, trimmed[^1]);
    }

    [Fact]
    public void FrameSize_ShrinksUntilTheSheetFits()
    {
        Assert.Equal((1920, 1080), VideoFlipbook.FrameSize(new VideoInfo(1920, 1080, 30, 2), 60, 1080));
        var (w, h) = VideoFlipbook.FrameSize(new VideoInfo(1920, 1080, 30, 10), 180, 1080);
        Assert.NotNull(VideoFlipbook.GridFor(180, w + 4, h + 4));
        Assert.True(h < 1080);
    }

    [Fact]
    public void SkeletonJson_IsExactlyTheCutinLength()
    {
        var json = VideoFlipbook.SkeletonJson(60, 4425, 2489, new VideoCutinOptions(), 21149);
        using var doc = JsonDocument.Parse(json);
        var anim = doc.RootElement.GetProperty("animations").GetProperty("animation");
        Assert.Equal(2.0, CutinBuilder.AnimDuration(anim));
        var keys = anim.GetProperty("slots").GetProperty("frame").GetProperty("attachment");
        Assert.Equal(61, keys.GetArrayLength());
        Assert.Equal("frame_59", keys[60].GetProperty("name").GetString());
        Assert.Equal(60, doc.RootElement.GetProperty("skins")[0].GetProperty("attachments").GetProperty("frame").EnumerateObject().Count());
    }

    [Fact]
    public void Blit_RepeatsEdgePixelsIntoTheGutter()
    {
        using var canvas = new Image<Rgba32>(8, 8);
        using var frame = new Image<Rgba32>(2, 2);
        frame[0, 0] = new Rgba32(10, 0, 0, 255);
        frame[1, 0] = new Rgba32(20, 0, 0, 255);
        frame[0, 1] = new Rgba32(30, 0, 0, 255);
        frame[1, 1] = new Rgba32(40, 0, 0, 255);
        VideoFlipbook.Blit(canvas, frame, 3, 3, 2);
        Assert.Equal(10, canvas[1, 1].R);   // top-left corner gutter
        Assert.Equal(20, canvas[6, 2].R);   // top-right gutter
        Assert.Equal(40, canvas[6, 6].R);   // bottom-right corner gutter
        Assert.Equal(30, canvas[3, 4].R);   // the frame itself
        Assert.Equal(0, canvas[0, 0].A);    // outside the gutter stays empty
    }

    [Fact]
    public void Registry_KeepsUnknownFieldsAndRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "floowan-cutin-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "registry.json"),
                "{\"version\":1,\"player_dir\":\"D:\\\\x\",\"cutins\":{\"21149\":{\"mode\":\"custom\",\"template\":19172,\"content\":\"content\\\\21149\",\"files\":[\"a\"],\"build_key\":\"k\",\"extra\":42}},\"pristine\":{},\"written\":{\"a\":\"s\"},\"created\":[\"a\"],\"last_apply\":\"t\"}");
            var reg = CutinRegistry.Load(dir);
            var e = Assert.Single(reg.Entries);
            Assert.Equal(21149, e.CardId);
            Assert.True(e.IsCustom);
            Assert.Equal(19172, e.Template);
            Assert.Equal("k", e.BuildKey);
            Assert.Equal(Path.Combine(dir, "content", "21149"), reg.ContentDir(e));
            reg.Save();
            var again = CutinRegistry.Load(dir);
            Assert.Equal(42, again.Cutins["21149"]!["extra"]!.GetValue<int>());
            Assert.Equal("t", again.Root["last_apply"]!.GetValue<string>());
            Assert.Equal("D:\\x", again.PlayerDir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Pack_ExportsAndReadsCards()
    {
        var dir = Path.Combine(Path.GetTempPath(), "floowan-cutin-pack-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(dir, "content", "500");
        Directory.CreateDirectory(content);
        try
        {
            foreach (var f in CutinRegistry.ContentFiles)
                File.WriteAllText(Path.Combine(content, f), f == "meta.json" ? "{\"card_id\":500}" : f);
            File.WriteAllText(Path.Combine(dir, "registry.json"),
                "{\"cutins\":{\"500\":{\"mode\":\"custom\",\"template\":19172,\"name\":\"A\",\"content\":\"content\\\\500\"},\"600\":{\"mode\":\"borrow\",\"src\":4007,\"name\":\"B\"}}}");
            var reg = CutinRegistry.Load(dir);
            var pack = Path.Combine(dir, "test" + CutinPack.Extension);
            CutinPack.Export(reg, [500, 600], pack);

            var cards = CutinPack.ReadCards(pack);
            Assert.Equal(2, cards.Count);
            Assert.Equal(CutinPackPayload.Content, cards.Single(c => c.CardId == 500).Payload);
            var borrow = cards.Single(c => c.CardId == 600);
            Assert.Equal(CutinPackPayload.Borrow, borrow.Payload);
            Assert.Equal(4007, borrow.Source);

            var work = Path.Combine(dir, "extract");
            Assert.Null(CutinPack.ExtractCard(pack, cards.Single(c => c.CardId == 500), work));
            Assert.Equal("{\"card_id\":500}", File.ReadAllText(Path.Combine(work, "meta.json")));
            Assert.Equal(CutinRegistry.ContentFiles.Length, Directory.GetFiles(work).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadBc7Dds_ReadsDx10Header()
    {
        var dds = new byte[148 + 16 * 4];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(dds, 0);
        BitConverter.GetBytes(8).CopyTo(dds, 12);    // height
        BitConverter.GetBytes(8).CopyTo(dds, 16);    // width
        Encoding.ASCII.GetBytes("DX10").CopyTo(dds, 84);
        dds[148] = 0xAB;
        var t = TexconvBc7Encoder.ReadBc7Dds(dds);
        Assert.Equal(8, t.Width);
        Assert.Equal(8, t.Height);
        Assert.Equal(64, t.Data.Length);
        Assert.Equal(0xAB, t.Data[0]);
    }
}

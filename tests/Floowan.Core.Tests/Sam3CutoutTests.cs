using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Tests;

public class Sam3CutoutTests
{
    [Theory]
    [InlineData(SubjectCutoutModel.IsnetAnime, "isnet-anime")]
    [InlineData(SubjectCutoutModel.Sam3, "sam3-vitmatte")]
    public void CutoutModelIds_RoundTrip(SubjectCutoutModel model, string id)
    {
        Assert.Equal(id, SubjectCutoutModels.ToId(model));
        Assert.Equal(model, SubjectCutoutModels.FromId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-else")]
    public void CutoutModelIds_UnknownFallsBackToIsnet(string? id) =>
        Assert.Equal(SubjectCutoutModel.IsnetAnime, SubjectCutoutModels.FromId(id));

    [Fact]
    public void EmbeddedPromptFeatures_CoverEveryPromptTheServiceUses()
    {
        var features = Sam3PromptFeatures.Embedded;
        Assert.Equal(32, features.SequenceLength);
        Assert.Equal(256, features.Dimension);
        foreach (var prompt in Sam3CutoutService.SubjectPrompts.Concat(Sam3CutoutService.HeldItemPrompts))
        {
            var (vector, mask) = features.Get(prompt);
            Assert.Equal(32 * 256, vector.Length);
            Assert.Equal(32, mask.Length);
            // Single-word prompts: start token, word, end token.
            Assert.Equal(3, mask.Sum());
            Assert.Contains(vector, v => v != 0f);
        }
    }

    [Fact]
    public void ModelFiles_HavePinnedHashesAndSizes()
    {
        Assert.NotEmpty(Sam3CutoutService.ModelFiles);
        foreach (var file in Sam3CutoutService.ModelFiles)
        {
            Assert.Equal(64, file.Sha256.Length);
            Assert.True(file.Size > 0);
            Assert.StartsWith("https://huggingface.co/", file.Url);
        }

        Assert.Equal(Sam3CutoutService.ModelFiles.Sum(f => f.Size), Sam3CutoutService.TotalDownloadBytes);
    }

    [Fact]
    public void UpsampleLogitsToMask_SplitsAtTheZeroCrossing()
    {
        // 4×4 grid: left two columns positive, right two negative.
        var logits = new float[16];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
            logits[y * 4 + x] = x < 2 ? 1f : -1f;

        var mask = Sam3MaskMath.UpsampleLogitsToMask(logits, 0, 4, 8, 8);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            Assert.Equal(x < 4, mask[y * 8 + x]);
    }

    [Fact]
    public void UpsampleLogitsToMask_ReadsTheRequestedPlane()
    {
        var logits = new float[2 * 4];
        for (var i = 0; i < 4; i++)
        {
            logits[i] = -1f;
            logits[4 + i] = 1f;
        }

        Assert.All(Sam3MaskMath.UpsampleLogitsToMask(logits, 0, 2, 5, 3), Assert.False);
        Assert.All(Sam3MaskMath.UpsampleLogitsToMask(logits, 4, 2, 5, 3), Assert.True);
    }

    [Fact]
    public void DilateThenErode_SinglePixel()
    {
        const int w = 7, h = 7;
        var mask = new bool[w * h];
        mask[3 * w + 3] = true;

        var grown = Sam3MaskMath.Dilate(mask, w, h, 1);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            Assert.Equal(Math.Abs(x - 3) <= 1 && Math.Abs(y - 3) <= 1, grown[y * w + x]);

        var shrunk = Sam3MaskMath.Erode(grown, w, h, 1);
        Assert.Equal(mask, shrunk);
    }

    [Fact]
    public void Erode_KeepsMaskThatTouchesTheBorder()
    {
        var full = Enumerable.Repeat(true, 25).ToArray();
        Assert.All(Sam3MaskMath.Erode(full, 5, 5, 2), Assert.True);
    }

    [Fact]
    public void BuildTrimap_MarksCoreBandAndOutside()
    {
        const int w = 30, h = 30;
        var mask = new bool[w * h];
        for (var y = 10; y < 20; y++)
        for (var x = 10; x < 20; x++)
            mask[y * w + x] = true;

        var trimap = Sam3MaskMath.BuildTrimap(mask, w, h, 2);
        Assert.Equal(Sam3MaskMath.TrimapForeground, trimap[15 * w + 15]);
        Assert.Equal(Sam3MaskMath.TrimapUnknown, trimap[10 * w + 10]); // inside, at the edge
        Assert.Equal(Sam3MaskMath.TrimapUnknown, trimap[15 * w + 8]); // outside, within the band
        Assert.Equal((byte)0, trimap[15 * w + 5]);
        Assert.Equal((byte)0, trimap[0]);
    }

    [Fact]
    public void IsAttachedHeldItem_KeepsTouchingItemsOnly()
    {
        const int w = 100, h = 100;
        var subject = new bool[w * h];
        for (var y = 30; y < 70; y++)
        for (var x = 30; x < 50; x++)
            subject[y * w + x] = true;
        var near = Sam3MaskMath.Dilate(subject, w, h, 6);

        bool[] Box(int x0, int x1)
        {
            var m = new bool[w * h];
            for (var y = 40; y < 60; y++)
            for (var x = x0; x < x1; x++)
                m[y * w + x] = true;
            return m;
        }

        Assert.True(Sam3MaskMath.IsAttachedHeldItem(Box(52, 70), subject, near)); // sword next to the hand
        Assert.False(Sam3MaskMath.IsAttachedHeldItem(Box(75, 95), subject, near)); // prop across the scene
        Assert.False(Sam3MaskMath.IsAttachedHeldItem(Box(32, 48), subject, near)); // adds nothing new
    }

    /// <summary>
    /// Runs the real models when they are already in the default folder (a developer machine
    /// that used the feature); a no-op elsewhere, like the other model-backed tests.
    /// </summary>
    [Fact]
    public void PredictMask_ReturnsSourceSizedMask_WhenModelsPresent()
    {
        using var service = new Sam3CutoutService();
        if (!service.AreModelsPresent())
            return;

        using var image = new Image<Rgba32>(200, 150, new Rgba32(30, 30, 60));
        using var mask = service.PredictMask(image, includeHeldItems: true);
        Assert.Equal(image.Width, mask.Width);
        Assert.Equal(image.Height, mask.Height);
        Assert.NotNull(service.ExecutionDevice);
    }

    [Theory]
    [InlineData(Sam3MaskMath.TrimapForeground, 0.1f, 255)]
    [InlineData(0, 0.9f, 0)]
    [InlineData(Sam3MaskMath.TrimapUnknown, 0.5f, 127)]
    [InlineData(Sam3MaskMath.TrimapUnknown, 1.4f, 255)]
    [InlineData(Sam3MaskMath.TrimapUnknown, -0.2f, 0)]
    public void ComposeTrimapAlpha_UsesAlphaOnlyInTheBand(byte trimap, float alpha, byte expected) =>
        Assert.Equal(expected, Sam3MaskMath.ComposeTrimapAlpha(trimap, alpha));

    [Fact]
    public void MatteModelFile_IsTheViTMatteEntry()
    {
        Assert.Equal(Sam3CutoutService.MatteModel, Sam3CutoutService.MatteModelFile.RelativePath);
        Assert.Contains(Sam3CutoutService.MatteModelFile, Sam3CutoutService.ModelFiles);
    }

    [Fact]
    public void RefineMaskEdge_EmptyMask_ReturnsCopyWithoutLoadingModels()
    {
        // Uses an empty folder: an empty mask must not need ViTMatte at all.
        var dir = Path.Combine(Path.GetTempPath(), "floowan-matte-empty-" + Guid.NewGuid().ToString("N"));
        using var service = new Sam3CutoutService(dir);
        using var image = new Image<Rgba32>(40, 30, new Rgba32(10, 10, 10));
        using var mask = new Image<L8>(40, 30, new L8(100)); // below the keep threshold everywhere

        using var refined = service.RefineMaskEdge(image, mask, keepThreshold: 140);

        Assert.NotSame(mask, refined);
        Assert.Equal(new L8(100), refined[20, 15]);
        Assert.Null(service.MatteDevice);
    }

    [Fact]
    public void RefineMaskEdge_RejectsMismatchedSizes()
    {
        using var service = new Sam3CutoutService(Path.Combine(Path.GetTempPath(), "floowan-matte-size"));
        using var image = new Image<Rgba32>(40, 30);
        using var mask = new Image<L8>(30, 40, new L8(255));
        Assert.Throws<ArgumentException>(() => service.RefineMaskEdge(image, mask, 140));
    }

    [Fact]
    public void RefineMaskEdge_ThrowsWhenTheMatteModelIsMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "floowan-matte-missing-" + Guid.NewGuid().ToString("N"));
        using var service = new Sam3CutoutService(dir);
        using var image = new Image<Rgba32>(40, 30);
        using var mask = new Image<L8>(40, 30, new L8(255));
        Assert.False(service.IsMatteModelPresent());
        Assert.Throws<InvalidOperationException>(() => service.RefineMaskEdge(image, mask, 140));
    }

    /// <summary>
    /// Real ViTMatte when installed (a no-op elsewhere): a blocky, speckled mask of a bright disc
    /// on a dark background should snap to the disc, drop a far-off speckle, and keep the core solid.
    /// </summary>
    [Fact]
    public void RefineMaskEdge_SnapsABlockyMaskToTheShape_WhenModelPresent()
    {
        using var service = new Sam3CutoutService();
        if (!service.IsMatteModelPresent())
            return;

        const int size = 256, radius = 70, cx = 128, cy = 128, block = 16;
        bool InDisc(int x, int y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;

        using var image = new Image<Rgba32>(size, size, new Rgba32(20, 24, 40));
        using var coarse = new Image<L8>(size, size);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            if (InDisc(x, y))
                image[x, y] = new Rgba32(235, 225, 210);
            // Low-resolution style mask: whole 16 px blocks whose centre is in the disc.
            var bx = x / block * block + block / 2;
            var by = y / block * block + block / 2;
            if (InDisc(bx, by))
                coarse[x, y] = new L8(220);
        }

        // A background speckle well away from the disc.
        for (var y = 10; y < 16; y++)
        for (var x = 10; x < 16; x++)
            coarse[x, y] = new L8(200);

        using var refined = service.RefineMaskEdge(image, coarse, keepThreshold: 140);

        int Errors(Image<L8> m)
        {
            var n = 0;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if ((m[x, y].PackedValue >= 140) != InDisc(x, y))
                    n++;
            return n;
        }

        Assert.Equal(255, refined[cx, cy].PackedValue);
        Assert.Equal(0, refined[size - 1, size - 1].PackedValue);
        Assert.True(refined[12, 12].PackedValue < 140, "background speckle should be dropped");
        Assert.True(Errors(refined) < Errors(coarse) / 2,
            $"edge errors: refined {Errors(refined)} vs coarse {Errors(coarse)}");
        Assert.NotNull(service.MatteDevice);
    }
}

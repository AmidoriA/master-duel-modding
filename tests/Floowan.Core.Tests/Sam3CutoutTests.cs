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
}

using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Floowan.Core.Imaging;

/// <summary>
/// One-click over-frame art generation using rembg's <c>isnet-anime</c> session.
/// Runs the ONNX model natively in .NET, so Python/rembg does not need to be
/// installed. The model is downloaded and verified on first use.
/// </summary>
public sealed class AutoOverFrameArtService : IDisposable
{
    public const string ModelName = "isnet-anime.onnx";
    public const string ModelUrl =
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-anime.onnx";
    public const string ModelMd5 = "6f184e756bb3bd901c8849220a83e38e";

    /// <summary>Matches rembg <c>DisSession</c> / isnet-anime normalize size.</summary>
    private const int ModelSize = 1024;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    // rembg isnet-anime uses std (1,1,1), unlike u2netp's ImageNet std.
    private static readonly float[] StdDev = [1f, 1f, 1f];

    private readonly HttpClient _httpClient;
    private readonly string _modelPath;
    private InferenceSession? _session;

    public AutoOverFrameArtService(string? modelDirectory = null, HttpClient? httpClient = null)
    {
        modelDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Floowan",
            "models");
        _modelPath = Path.Combine(modelDirectory, ModelName);
        _httpClient = httpClient ?? new HttpClient();
    }

    public string ModelPath => _modelPath;

    public async Task CreateAsync(
        string sourceImagePath,
        string outputPngPath,
        CardFrameStyle frameStyle = CardFrameStyle.Effect,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("Source card art was not found.", sourceImagePath);

        await EnsureModelAsync(progress, cancellationToken).ConfigureAwait(false);
        progress?.Report("Removing background with isnet-anime…");

        await Task.Run(() =>
        {
            using var loaded = Image.Load<Rgba32>(sourceImagePath);
            // Refuse framed / OF sources — cropping a nested card still nests frames.
            // Pendulum 512×1024 is cropped to the top art band before rembg.
            using var source = OverFrameAutoArtComposer.RequireCleanIllustrationSource(loaded);
            if (OverFrameAutoArtComposer.IsOverFrameTextureSize(loaded.Width, loaded.Height))
            {
                progress?.Report(
                    "Source was 704×1024 — verified clean illustration before rembg…");
            }
            else if (CardArtTextureSizes.IsPendulum(loaded.Width, loaded.Height) ||
                     CardArtTextureSizes.HasPendulumAspect(loaded.Width, loaded.Height))
            {
                progress?.Report(
                    $"Source was Pendulum {loaded.Width}×{loaded.Height} — using top art crop for rembg…");
            }

            using var mask = PredictMask(source);
            progress?.Report($"Compositing subject onto {frameStyle} frame (704×1024)…");
            using var result = OverFrameAutoArtComposer.Compose(source, mask, frameStyle);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);
            result.Save(outputPngPath, new PngEncoder());
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report("Automatic over-frame art is ready for review.");
    }

    private async Task EnsureModelAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(_modelPath) && HasExpectedChecksum(_modelPath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
        var tempPath = _modelPath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            progress?.Report("Downloading the rembg isnet-anime model (~168 MB, first use only)…");
            using var response = await _httpClient.GetAsync(
                ModelUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                if (total > 0)
                    progress?.Report($"Downloading isnet-anime model… {downloaded * 100 / total.Value}%");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();

            if (!HasExpectedChecksum(tempPath))
                throw new InvalidDataException("Downloaded isnet-anime model failed its MD5 integrity check.");

            File.Move(tempPath, _modelPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    private Image<L8> PredictMask(Image<Rgba32> source)
    {
        _session ??= new InferenceSession(_modelPath);
        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(ModelSize, ModelSize),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        // rembg: im_ary / max(im_ary) then (x - mean) / std
        var maximum = 1f;
        for (var y = 0; y < resized.Height; y++)
        {
            var row = resized.DangerousGetPixelRowMemory(y).Span;
            foreach (var pixel in row)
                maximum = Math.Max(maximum, Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)));
        }

        var input = new DenseTensor<float>([1, 3, ModelSize, ModelSize]);
        for (var y = 0; y < ModelSize; y++)
        {
            var row = resized.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < ModelSize; x++)
            {
                var pixel = row[x];
                input[0, 0, y, x] = (pixel.R / maximum - Mean[0]) / StdDev[0];
                input[0, 1, y, x] = (pixel.G / maximum - Mean[1]) / StdDev[1];
                input[0, 2, y, x] = (pixel.B / maximum - Mean[2]) / StdDev[2];
            }
        }

        var inputName = _session.InputMetadata.Keys.First();
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        var prediction = results.First().AsTensor<float>().ToArray();
        // isnet-anime outputs [1,1,1024,1024]; keep last HxW plane if extra dims exist.
        if (prediction.Length < ModelSize * ModelSize)
            throw new InvalidDataException("isnet-anime returned an unexpected output shape.");

        var offset = prediction.Length - ModelSize * ModelSize;
        var minimum = float.MaxValue;
        var maximumPrediction = float.MinValue;
        for (var i = offset; i < prediction.Length; i++)
        {
            minimum = Math.Min(minimum, prediction[i]);
            maximumPrediction = Math.Max(maximumPrediction, prediction[i]);
        }

        var range = Math.Max(maximumPrediction - minimum, 1e-6f);
        var mask = new Image<L8>(ModelSize, ModelSize);
        for (var y = 0; y < ModelSize; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < ModelSize; x++)
            {
                var normalized = Math.Clamp(
                    (prediction[offset + y * ModelSize + x] - minimum) / range,
                    0f,
                    1f);
                row[x] = new L8((byte)MathF.Round(normalized * 255));
            }
        }

        mask.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = source.Size,
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
        return mask;
    }

    private static bool HasExpectedChecksum(string path)
    {
        using var stream = File.OpenRead(path);
        var checksum = Convert.ToHexString(MD5.HashData(stream));
        return checksum.Equals(ModelMd5, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _httpClient.Dispose();
    }
}

using System.IO.Compression;
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
/// SAM 2 (Hiera-Tiny) ONNX subject cutout: paint-region or point prompts.
/// Runs in-process with Microsoft.ML.OnnxRuntime (no Python sidecar). Models are
/// downloaded on first use into <c>%LOCALAPPDATA%\Floowan\models\sam2</c> and are
/// not committed to the repo.
/// </summary>
public sealed class Sam2PointCutoutService : IDisposable
{
    /// <summary>Image-space prompt for the SAM 2 decoder (label 1 = positive, 2/3 = box corners).</summary>
    public readonly record struct PromptPoint(float X, float Y, float Label);
    public const string ModelVariant = "sam2_hiera_tiny";
    public const string EncoderFileName = "sam2_hiera_tiny.encoder.onnx";
    public const string DecoderFileName = "sam2_hiera_tiny.decoder.onnx";
    public const string BundleZipName = "sam2_hiera_tiny.zip";

    /// <summary>
    /// Pre-exported ONNX pair from
    /// https://huggingface.co/vietanhdev/segment-anything-2-onnx-models (samexporter layout).
    /// </summary>
    public const string BundleZipUrl =
        "https://huggingface.co/vietanhdev/segment-anything-2-onnx-models/resolve/main/sam2_hiera_tiny.zip";

    /// <summary>SHA-256 of <see cref="BundleZipName"/> (Hugging Face X-Linked-ETag).</summary>
    public const string BundleZipSha256 =
        "7454c3afd835b2acaad863afe3acb11f4e4af039c96e886989ad3873a338e1ec";

    public const long BundleZipExpectedBytes = 154_902_833;

    /// <summary>
    /// When false, Desktop hides the SAM paint-selection entry. Default true; set env
    /// <c>FLOOWAN_SAM2=0</c> to disable without rebuilding.
    /// </summary>
    public static bool IsFeatureEnabled
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("FLOOWAN_SAM2");
            if (string.IsNullOrWhiteSpace(env))
                return true;
            return !(env is "0" or "false" or "False" or "FALSE" or "off" or "OFF");
        }
    }

    /// <summary>Minimum painted alpha treated as brush coverage when deriving SAM prompts.</summary>
    public const byte PaintKeepThreshold = 32;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] StdDev = [0.229f, 0.224f, 0.225f];

    private readonly HttpClient _httpClient;
    private readonly string _modelDirectory;
    private readonly string _encoderPath;
    private readonly string _decoderPath;
    private readonly ArtUpscaleService _artUpscale;
    private readonly object _sessionLock = new();
    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private int _encoderHeight = 1024;
    private int _encoderWidth = 1024;

    public Sam2PointCutoutService(string? modelDirectory = null, HttpClient? httpClient = null)
    {
        modelDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Floowan",
            "models",
            "sam2");
        _modelDirectory = modelDirectory;
        _encoderPath = Path.Combine(_modelDirectory, EncoderFileName);
        _decoderPath = Path.Combine(_modelDirectory, DecoderFileName);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var modelsRoot = Path.GetDirectoryName(_modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                         ?? Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Floowan",
                             "models");
        _artUpscale = new ArtUpscaleService(Path.Combine(modelsRoot, "realesrgan"));
    }

    public string ModelDirectory => _modelDirectory;
    public string EncoderPath => _encoderPath;
    public string DecoderPath => _decoderPath;

    /// <summary>
    /// Runs SAM2 on cleaned card illustration with a single positive point in
    /// <paramref name="pointX"/>/<paramref name="pointY"/> source-pixel coordinates.
    /// Returns RGB source + L8 mask (caller disposes both).
    /// </summary>
    public Task<(Image<Rgba32> Source, Image<L8> Mask)> PrepareSubjectWithPointAsync(
        string sourceImagePath,
        float pointX,
        float pointY,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return PrepareSubjectWithPromptsAsync(
            sourceImagePath,
            [new PromptPoint(pointX, pointY, Label: 1f)],
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Runs SAM2 using prompts derived from a user-painted region (bounding box +
    /// positive points inside the paint). Returns RGB source + L8 mask (caller disposes both).
    /// </summary>
    public async Task<(Image<Rgba32> Source, Image<L8> Mask)> PrepareSubjectWithPaintedRegionAsync(
        string sourceImagePath,
        Image<L8> paintMask,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paintMask);
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("Source card art was not found.", sourceImagePath);

        // Validate paint vs cleaned art size off the UI thread before prompting.
        var sizeOk = await Task.Run(
            () =>
            {
                using var prepared = PrepareCleanSource(sourceImagePath, progress: null);
                return prepared.Source.Width == paintMask.Width
                       && prepared.Source.Height == paintMask.Height;
            },
            cancellationToken).ConfigureAwait(false);

        if (!sizeOk)
        {
            throw new ArgumentException(
                "Paint mask size does not match cleaned card art. Re-open the paint editor and try again.");
        }

        var prompts = BuildPromptsFromPaintMask(paintMask);
        if (prompts.Count == 0)
        {
            throw new InvalidOperationException(
                "Paint a region on the art before running SAM 2.");
        }

        progress?.Report($"SAM 2: {prompts.Count} prompt(s) from painted region…");
        return await PrepareSubjectWithPromptsAsync(
            sourceImagePath,
            prompts,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs SAM2 with explicit image-space prompts (positive points and/or box corners).
    /// </summary>
    public async Task<(Image<Rgba32> Source, Image<L8> Mask)> PrepareSubjectWithPromptsAsync(
        string sourceImagePath,
        IReadOnlyList<PromptPoint> prompts,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("Source card art was not found.", sourceImagePath);
        if (prompts is null || prompts.Count == 0)
            throw new ArgumentException("At least one SAM 2 prompt is required.", nameof(prompts));

        await EnsureModelsAsync(progress, cancellationToken).ConfigureAwait(false);
        if (ArtUpscaleService.IsFeatureEnabled)
            await _artUpscale.EnsureModelAsync(progress, cancellationToken).ConfigureAwait(false);
        progress?.Report("Running SAM 2 cutout…");

        // Capture for Task.Run closure.
        var promptList = prompts.ToArray();
        return await Task.Run(() =>
        {
            using var prepared = PrepareCleanSource(sourceImagePath, progress);
            var source = prepared.Source.Clone();
            Image<L8>? mask = null;
            try
            {
                EnsureSessions();
                var clamped = ClampPrompts(promptList, source.Width, source.Height);
                mask = PredictMask(source, clamped);

                var keep = CountOpaque(mask);
                if (keep == 0)
                {
                    throw new InvalidOperationException(
                        "SAM 2 found no opaque subject for that painted region. Paint a different area.");
                }

                Image<Rgba32>? bg = null;
                _artUpscale.UpscalePreparedLayersInPlace(ref source, ref mask, ref bg, progress);

                progress?.Report("SAM 2 subject mask ready.");
                return (source, mask);
            }
            catch
            {
                source.Dispose();
                mask?.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds SAM prompts from a painted L8 region: box corners (labels 2/3) plus
    /// positive points (centroid and grid samples) inside the paint.
    /// </summary>
    public static IReadOnlyList<PromptPoint> BuildPromptsFromPaintMask(
        Image<L8> paintMask,
        byte paintThreshold = PaintKeepThreshold)
    {
        ArgumentNullException.ThrowIfNull(paintMask);
        var w = paintMask.Width;
        var h = paintMask.Height;
        if (w <= 0 || h <= 0)
            return Array.Empty<PromptPoint>();

        var minX = w;
        var minY = h;
        var maxX = -1;
        var maxY = -1;
        long sumX = 0;
        long sumY = 0;
        var count = 0;

        for (var y = 0; y < h; y++)
        {
            var row = paintMask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < w; x++)
            {
                if (row[x].PackedValue < paintThreshold)
                    continue;

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                sumX += x;
                sumY += y;
                count++;
            }
        }

        if (count == 0 || maxX < minX || maxY < minY)
            return Array.Empty<PromptPoint>();

        var prompts = new List<PromptPoint>(10)
        {
            // SAM box corners (samexporter / SAM labels 2 = top-left, 3 = bottom-right).
            new((float)minX, (float)minY, Label: 2f),
            new((float)maxX, (float)maxY, Label: 3f),
            new(sumX / (float)count, sumY / (float)count, Label: 1f)
        };

        // Up to 4 additional positives on a 2×2 grid inside the bbox, only if painted.
        var grid = new (float U, float V)[]
        {
            (0.25f, 0.25f), (0.75f, 0.25f), (0.25f, 0.75f), (0.75f, 0.75f)
        };
        var boxW = Math.Max(maxX - minX, 1);
        var boxH = Math.Max(maxY - minY, 1);
        foreach (var (u, v) in grid)
        {
            var x = minX + u * boxW;
            var y = minY + v * boxH;
            var ix = (int)Math.Clamp(MathF.Round(x), 0, w - 1);
            var iy = (int)Math.Clamp(MathF.Round(y), 0, h - 1);
            if (paintMask[ix, iy].PackedValue < paintThreshold)
                continue;
            prompts.Add(new PromptPoint(x, y, Label: 1f));
        }

        return prompts;
    }

    /// <summary>
    /// Per-pixel max alpha — used to add a new SAM selection onto an existing subject.
    /// </summary>
    public static Image<L8> UnionMasks(Image<L8> existing, Image<L8> addition)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(addition);
        if (existing.Width != addition.Width || existing.Height != addition.Height)
            throw new ArgumentException("Mask dimensions must match for union.");

        var result = existing.Clone();
        for (var y = 0; y < result.Height; y++)
        {
            var dst = result.DangerousGetPixelRowMemory(y).Span;
            var add = addition.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < dst.Length; x++)
            {
                if (add[x].PackedValue > dst[x].PackedValue)
                    dst[x] = add[x];
            }
        }

        return result;
    }

    /// <summary>
    /// Clears pixels in <paramref name="existing"/> wherever <paramref name="removal"/> is
    /// opaque (at/above <see cref="OverFrameAutoArtComposer.MaskKeepThreshold"/>).
    /// Used by Click-mode right-click remove.
    /// </summary>
    public static Image<L8> SubtractMasks(Image<L8> existing, Image<L8> removal)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(removal);
        if (existing.Width != removal.Width || existing.Height != removal.Height)
            throw new ArgumentException("Mask dimensions must match for subtract.");

        var threshold = OverFrameAutoArtComposer.MaskKeepThreshold;
        var result = existing.Clone();
        for (var y = 0; y < result.Height; y++)
        {
            var dst = result.DangerousGetPixelRowMemory(y).Span;
            var rem = removal.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < dst.Length; x++)
            {
                if (rem[x].PackedValue >= threshold)
                    dst[x] = new L8(0);
            }
        }

        return result;
    }

    /// <summary>
    /// Packs an L8 selection mask into BGRA32 highlight pixels with a hard keep threshold.
    /// Selected pixels (≥ threshold) get solid overlay color+alpha; others stay transparent.
    /// Display-only — does not mutate the source mask or change cutout semantics.
    /// </summary>
    public static void WriteBinaryMaskHighlightBgra(
        Image<L8> mask,
        byte b,
        byte g,
        byte r,
        byte overlayAlpha,
        Span<byte> bgraPixels,
        int stride,
        byte keepThreshold = OverFrameAutoArtComposer.MaskKeepThreshold)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var w = mask.Width;
        var h = mask.Height;
        if (stride < w * 4)
            throw new ArgumentException("BGRA stride must be at least width * 4.", nameof(stride));
        if (bgraPixels.Length < stride * h)
            throw new ArgumentException("BGRA buffer too small for mask dimensions.", nameof(bgraPixels));

        bgraPixels.Clear();
        for (var y = 0; y < h; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            var dest = y * stride;
            for (var x = 0; x < w; x++)
            {
                if (row[x].PackedValue >= keepThreshold)
                {
                    bgraPixels[dest] = b;
                    bgraPixels[dest + 1] = g;
                    bgraPixels[dest + 2] = r;
                    bgraPixels[dest + 3] = overlayAlpha;
                }

                dest += 4;
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="mask"/> as alpha onto a clone of <paramref name="source"/>.
    /// </summary>
    public static Image<Rgba32> ApplyMaskAsAlpha(Image<Rgba32> source, Image<L8> mask)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);
        if (source.Width != mask.Width || source.Height != mask.Height)
            throw new ArgumentException("Source and mask dimensions must match.");

        var cutout = source.Clone();
        for (var y = 0; y < cutout.Height; y++)
        {
            var pixels = cutout.DangerousGetPixelRowMemory(y).Span;
            var maskRow = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < pixels.Length; x++)
            {
                ref var px = ref pixels[x];
                px.A = maskRow[x].PackedValue;
            }
        }

        return cutout;
    }

    /// <summary>
    /// Maps a click in a Uniform-stretched preview host to source image pixels.
    /// Returns false when the click falls outside the displayed image.
    /// </summary>
    public static bool TryMapPreviewClickToImage(
        double hostX,
        double hostY,
        double hostWidth,
        double hostHeight,
        int imageWidth,
        int imageHeight,
        out float imageX,
        out float imageY)
    {
        imageX = 0;
        imageY = 0;
        if (hostWidth <= 0 || hostHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
            return false;

        var scale = Math.Min(hostWidth / imageWidth, hostHeight / imageHeight);
        if (scale <= 0)
            return false;

        var displayW = imageWidth * scale;
        var displayH = imageHeight * scale;
        var offsetX = (hostWidth - displayW) * 0.5;
        var offsetY = (hostHeight - displayH) * 0.5;
        var localX = (hostX - offsetX) / scale;
        var localY = (hostY - offsetY) / scale;
        if (localX < 0 || localY < 0 || localX >= imageWidth || localY >= imageHeight)
            return false;

        imageX = (float)localX;
        imageY = (float)localY;
        return true;
    }

    /// <summary>
    /// Scales a point from original image pixels into encoder input space
    /// (samexporter <c>SAM2ImageDecoder.prepare_points</c>).
    /// </summary>
    public static (float X, float Y) ScalePointToEncoder(
        float imageX,
        float imageY,
        int originalWidth,
        int originalHeight,
        int encoderWidth,
        int encoderHeight)
    {
        if (originalWidth <= 0 || originalHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(originalWidth));

        var x = imageX / originalWidth * encoderWidth;
        var y = imageY / originalHeight * encoderHeight;
        return (x, y);
    }

    public async Task EnsureModelsAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Existence, SHA-256 (~155 MB zip), extract, and ONNX session load must never
        // run on a WPF UI thread — all of that sync work is Task.Run'd below.
        var modelsReady = await Task.Run(
            () => File.Exists(_encoderPath) && File.Exists(_decoderPath),
            cancellationToken).ConfigureAwait(false);

        if (!modelsReady)
        {
            Directory.CreateDirectory(_modelDirectory);
            var zipPath = Path.Combine(_modelDirectory, BundleZipName);
            var tempZip = zipPath + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                var needsDownload = await Task.Run(
                    () => !File.Exists(zipPath) || !HasExpectedZipChecksum(zipPath),
                    cancellationToken).ConfigureAwait(false);

                if (needsDownload)
                {
                    progress?.Report(
                        $"Downloading SAM 2 Tiny ONNX (~{BundleZipExpectedBytes / (1024 * 1024)} MB, first use only)…");
                    using var response = await _httpClient.GetAsync(
                        BundleZipUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? BundleZipExpectedBytes;
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await using (var output = new FileStream(
                                     tempZip,
                                     FileMode.Create,
                                     FileAccess.Write,
                                     FileShare.None,
                                     81920,
                                     useAsync: true))
                    {
                        var buffer = new byte[81920];
                        long downloaded = 0;
                        var lastPercent = -1;
                        int read;
                        while ((read = await input.ReadAsync(buffer, cancellationToken)
                                       .ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                                .ConfigureAwait(false);
                            downloaded += read;
                            if (total > 0)
                            {
                                var percent = (int)(downloaded * 100 / total);
                                if (percent != lastPercent)
                                {
                                    lastPercent = percent;
                                    progress?.Report($"Downloading SAM 2 Tiny… {percent}%");
                                }
                            }
                        }

                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await Task.Run(
                        () =>
                        {
                            if (!HasExpectedZipChecksum(tempZip))
                            {
                                throw new InvalidDataException(
                                    "Downloaded SAM 2 model zip failed its SHA-256 integrity check.");
                            }

                            File.Move(tempZip, zipPath, overwrite: true);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                progress?.Report("Extracting SAM 2 encoder/decoder ONNX…");
                await Task.Run(
                    () =>
                    {
                        ZipFile.ExtractToDirectory(zipPath, _modelDirectory, overwriteFiles: true);
                        if (!File.Exists(_encoderPath) || !File.Exists(_decoderPath))
                        {
                            throw new FileNotFoundException(
                                $"SAM 2 zip did not contain {EncoderFileName} and {DecoderFileName}.");
                        }

                        // Keep only the ONNX pair — remove the archive so models/ stays tidy
                        // (same spirit as rembg/.download temp cleanup).
                        try
                        {
                            if (File.Exists(zipPath))
                                File.Delete(zipPath);
                        }
                        catch
                        {
                            /* best effort */
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempZip))
                        File.Delete(tempZip);
                }
                catch
                {
                    /* best effort */
                }
            }
        }
        else
        {
            // Prior runs may have left the zip beside the extracted ONNX files.
            await Task.Run(
                () =>
                {
                    var leftoverZip = Path.Combine(_modelDirectory, BundleZipName);
                    try
                    {
                        if (File.Exists(leftoverZip))
                            File.Delete(leftoverZip);
                    }
                    catch
                    {
                        /* best effort */
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        // Warm encoder/decoder sessions during prepare (not on first preview click).
        if (_encoder is null || _decoder is null)
        {
            progress?.Report("Loading SAM 2 ONNX sessions…");
            await Task.Run(() => EnsureSessions(), cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureSessions()
    {
        lock (_sessionLock)
        {
            if (_encoder is not null && _decoder is not null)
                return;

            _encoder = new InferenceSession(_encoderPath);
            _decoder = new InferenceSession(_decoderPath);

            var shape = _encoder.InputMetadata.Values.First().Dimensions;
            if (shape.Length >= 4 && shape[2] > 0 && shape[3] > 0)
            {
                _encoderHeight = shape[2];
                _encoderWidth = shape[3];
            }
        }
    }

    private Image<L8> PredictMask(Image<Rgba32> source, IReadOnlyList<PromptPoint> prompts)
    {
        EnsureSessions();
        var embeddings = EncodeImage(source);
        try
        {
            return DecodeMask(source, embeddings, prompts);
        }
        finally
        {
            embeddings.Dispose();
        }
    }

    private EncoderOutputs EncodeImage(Image<Rgba32> source)
    {
        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(_encoderWidth, _encoderHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        var input = new DenseTensor<float>([1, 3, _encoderHeight, _encoderWidth]);
        for (var y = 0; y < _encoderHeight; y++)
        {
            var row = resized.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < _encoderWidth; x++)
            {
                var pixel = row[x];
                input[0, 0, y, x] = (pixel.R / 255f - Mean[0]) / StdDev[0];
                input[0, 1, y, x] = (pixel.G / 255f - Mean[1]) / StdDev[1];
                input[0, 2, y, x] = (pixel.B / 255f - Mean[2]) / StdDev[2];
            }
        }

        var inputName = _encoder!.InputMetadata.Keys.First();
        using var results = _encoder.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        var tensors = results.Select(r => r.AsTensor<float>().Clone()).ToArray();
        if (tensors.Length < 3)
            throw new InvalidDataException("SAM 2 encoder returned fewer than 3 outputs.");

        // samexporter: outputs[0], [1], [2] => high_res_feats_0, high_res_feats_1, image_embed
        return new EncoderOutputs(tensors[0], tensors[1], tensors[2]);
    }

    private Image<L8> DecodeMask(
        Image<Rgba32> source,
        EncoderOutputs embeddings,
        IReadOnlyList<PromptPoint> prompts)
    {
        var n = prompts.Count;
        var pointCoords = new DenseTensor<float>([1, n, 2]);
        var pointLabels = new DenseTensor<float>([1, n]);
        for (var i = 0; i < n; i++)
        {
            var (encX, encY) = ScalePointToEncoder(
                prompts[i].X,
                prompts[i].Y,
                source.Width,
                source.Height,
                _encoderWidth,
                _encoderHeight);
            pointCoords[0, i, 0] = encX;
            pointCoords[0, i, 1] = encY;
            pointLabels[0, i] = prompts[i].Label;
        }

        var maskH = Math.Max(_encoderHeight / 4, 1);
        var maskW = Math.Max(_encoderWidth / 4, 1);
        var maskInput = new DenseTensor<float>([1, 1, maskH, maskW]);
        var hasMaskInput = new DenseTensor<float>([1]);
        hasMaskInput[0] = 0f;

        var feeds = BuildDecoderFeeds(
            embeddings,
            pointCoords,
            pointLabels,
            maskInput,
            hasMaskInput);

        using var results = _decoder!.Run(feeds);
        var outputs = results.ToList();
        if (outputs.Count < 2)
            throw new InvalidDataException("SAM 2 decoder returned unexpected outputs.");

        // Prefer named outputs when present; else samexporter order: masks, iou_predictions.
        var masksTensor = FindOutput(outputs, "masks", "low_res_masks") ?? outputs[0].AsTensor<float>();
        var scoresTensor = FindOutput(outputs, "iou_predictions", "scores", "iou_preds")
                           ?? outputs[1].AsTensor<float>();

        var bestIndex = ArgMax(scoresTensor);
        var maskPlane = ExtractMaskPlane(masksTensor, bestIndex);
        var mask = LogitsToMaskImage(maskPlane, masksTensor);
        mask.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = source.Size,
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
        return mask;
    }

    private static PromptPoint[] ClampPrompts(IReadOnlyList<PromptPoint> prompts, int width, int height)
    {
        var maxX = Math.Max(width - 1, 0);
        var maxY = Math.Max(height - 1, 0);
        var clamped = new PromptPoint[prompts.Count];
        for (var i = 0; i < prompts.Count; i++)
        {
            var p = prompts[i];
            clamped[i] = new PromptPoint(
                Math.Clamp(p.X, 0f, maxX),
                Math.Clamp(p.Y, 0f, maxY),
                p.Label);
        }

        return clamped;
    }

    private List<NamedOnnxValue> BuildDecoderFeeds(
        EncoderOutputs embeddings,
        DenseTensor<float> pointCoords,
        DenseTensor<float> pointLabels,
        DenseTensor<float> maskInput,
        DenseTensor<float> hasMaskInput)
    {
        var names = _decoder!.InputMetadata.Keys.ToList();
        var feeds = new List<NamedOnnxValue>(names.Count);

        // Match samexporter positional order when names are unfamiliar, but prefer names.
        Tensor<float>[] positional =
        [
            embeddings.ImageEmbed,
            embeddings.HighRes0,
            embeddings.HighRes1,
            pointCoords,
            pointLabels,
            maskInput,
            hasMaskInput
        ];

        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var lower = name.ToLowerInvariant();
            Tensor<float> tensor;
            if (lower.Contains("image_embed") || lower is "image_embeddings" or "image_embedding")
                tensor = embeddings.ImageEmbed;
            else if (lower.Contains("high_res_feats_0") || lower.Contains("high_res_feat_0") ||
                     lower.EndsWith("feats_0") || lower.Contains("backbone_fpn_0"))
                tensor = embeddings.HighRes0;
            else if (lower.Contains("high_res_feats_1") || lower.Contains("high_res_feat_1") ||
                     lower.EndsWith("feats_1") || lower.Contains("backbone_fpn_1"))
                tensor = embeddings.HighRes1;
            else if (lower.Contains("point_coord"))
                tensor = pointCoords;
            else if (lower.Contains("point_label"))
                tensor = pointLabels;
            else if (lower.Contains("has_mask"))
                tensor = hasMaskInput;
            else if (lower.Contains("mask_input") || lower is "mask_inputs")
                tensor = maskInput;
            else if (i < positional.Length)
                tensor = positional[i];
            else
                throw new InvalidDataException($"Unrecognized SAM 2 decoder input '{name}'.");

            feeds.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
        }

        return feeds;
    }

    private static Tensor<float>? FindOutput(
        IReadOnlyList<DisposableNamedOnnxValue> outputs,
        params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = outputs.FirstOrDefault(o =>
                o.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.AsTensor<float>();
        }

        return null;
    }

    private static int ArgMax(Tensor<float> scores)
    {
        var best = 0;
        var bestScore = float.NegativeInfinity;
        var i = 0;
        foreach (var score in scores)
        {
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }

            i++;
        }

        return best;
    }

    private static float[] ExtractMaskPlane(Tensor<float> masks, int maskIndex)
    {
        var dims = masks.Dimensions.ToArray();
        // Common shapes: [1, C, H, W] or [C, H, W]
        if (dims.Length == 4)
        {
            var c = dims[1];
            var h = dims[2];
            var w = dims[3];
            var idx = Math.Clamp(maskIndex, 0, Math.Max(c - 1, 0));
            var plane = new float[h * w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                    plane[y * w + x] = masks[0, idx, y, x];
            }

            return plane;
        }

        if (dims.Length == 3)
        {
            var c = dims[0];
            var h = dims[1];
            var w = dims[2];
            var idx = Math.Clamp(maskIndex, 0, Math.Max(c - 1, 0));
            var plane = new float[h * w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                    plane[y * w + x] = masks[idx, y, x];
            }

            return plane;
        }

        throw new InvalidDataException($"Unsupported SAM 2 mask tensor rank {dims.Length}.");
    }

    private static Image<L8> LogitsToMaskImage(float[] logits, Tensor<float> masksTensor)
    {
        var dims = masksTensor.Dimensions.ToArray();
        var h = dims.Length == 4 ? dims[2] : dims[1];
        var w = dims.Length == 4 ? dims[3] : dims[2];
        if (logits.Length != h * w)
            throw new InvalidDataException("SAM 2 mask plane size mismatch.");

        // samexporter thresholds logits at 0 (= probability 0.5). Map via sigmoid to alpha.
        var mask = new Image<L8>(w, h);
        for (var y = 0; y < h; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < w; x++)
            {
                var logit = logits[y * w + x];
                var prob = 1f / (1f + MathF.Exp(-logit));
                var alpha = (byte)Math.Clamp((int)MathF.Round(prob * 255f), 0, 255);
                row[x] = new L8(alpha);
            }
        }

        return mask;
    }

    private static PreparedSource PrepareCleanSource(
        string sourceImagePath,
        IProgress<string>? progress)
    {
        var loaded = Image.Load<Rgba32>(sourceImagePath);
        var loadedW = loaded.Width;
        var loadedH = loaded.Height;
        var source = OverFrameAutoArtComposer.RequireCleanIllustrationSource(loaded);
        if (!ReferenceEquals(loaded, source))
            loaded.Dispose();

        if (OverFrameAutoArtComposer.IsOverFrameTextureSize(loadedW, loadedH))
            progress?.Report("Source was 704×1024 — cropped art window before SAM 2…");
        else if (CardArtTextureSizes.IsPendulumNativeCanvas(loadedW, loadedH) ||
                 CardArtTextureSizes.IsPendulum(loadedW, loadedH) ||
                 CardArtTextureSizes.HasPendulumAspect(loadedW, loadedH))
        {
            progress?.Report(
                $"Source was Pendulum {loadedW}×{loadedH} — using 3:4 art for SAM 2…");
        }

        return new PreparedSource(source);
    }

    private static int CountOpaque(Image<L8> mask)
    {
        var keep = 0;
        for (var y = 0; y < mask.Height; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < row.Length; x++)
            {
                if (row[x].PackedValue >= OverFrameAutoArtComposer.MaskKeepThreshold)
                    keep++;
            }
        }

        return keep;
    }

    private static bool HasExpectedZipChecksum(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return hash.Equals(BundleZipSha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _encoder?.Dispose();
            _decoder?.Dispose();
            _encoder = null;
            _decoder = null;
        }

        _httpClient.Dispose();
        _artUpscale.Dispose();
    }

    private sealed class PreparedSource(Image<Rgba32> source) : IDisposable
    {
        public Image<Rgba32> Source { get; } = source;
        public void Dispose() => Source.Dispose();
    }

    private sealed class EncoderOutputs(
        Tensor<float> highRes0,
        Tensor<float> highRes1,
        Tensor<float> imageEmbed) : IDisposable
    {
        public Tensor<float> HighRes0 { get; } = highRes0;
        public Tensor<float> HighRes1 { get; } = highRes1;
        public Tensor<float> ImageEmbed { get; } = imageEmbed;

        public void Dispose()
        {
            // DenseTensor from Clone() is GC-managed; no native dispose required.
        }
    }
}

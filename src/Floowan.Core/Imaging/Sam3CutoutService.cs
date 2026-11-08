using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Floowan.Core.Imaging;

/// <summary>
/// Subject cutout with SAM 3 text prompts plus ViTMatte edge placement, all in-process on ONNX
/// Runtime (no Python). SAM 3 finds the card's character / monster from fixed words
/// ("character", "monster", …), optionally adds held items that touch it (weapon, wings, hat, …),
/// then ViTMatte refines a band around that outline at full resolution. Runs on the DirectML
/// GPU provider when available, otherwise CPU (about 1.5 s vs 17 s per card on a desktop).
/// Models (~1.1 GB) download on first use into <c>%LOCALAPPDATA%\Floowan\models\sam3cutout</c>.
/// </summary>
public sealed class Sam3CutoutService : IDisposable
{
    public sealed record ModelFile(string RelativePath, string Url, long Size, string Sha256);

    private const string Sam3Repo = "https://huggingface.co/wW-Ww/sam3-ONNX/resolve/main/";
    private const string VitMatteRepo = "https://huggingface.co/Xenova/vitmatte-small-composition-1k/resolve/main/onnx/";

    public const string VisionModel = "vision_fp16.onnx";
    public const string DetectorModel = "detector.onnx";
    public const string MatteModel = "vitmatte-small-composition-1k.onnx";

    /// <summary>Pinned downloads. External-data files must keep their names next to their graph.</summary>
    public static IReadOnlyList<ModelFile> ModelFiles { get; } =
    [
        new(VisionModel, Sam3Repo + "vision_fp16.onnx", 5780933,
            "e47a46c3facb14675eaafb2841c592897f84d09e80b6e805d4ec3f0eb1084220"),
        new(VisionModel + ".data", Sam3Repo + "vision_fp16.onnx.data", 921428992,
            "e7fea269dbebea013537810b1a05affa677b3f5053ef6a93c131726e1ce0608e"),
        new(DetectorModel, Sam3Repo + "detector.onnx", 2690377,
            "f90162580a07b018fd2af7db3960b24435d0802b073078b3f8e55e23c1fa9979"),
        new(DetectorModel + ".data", Sam3Repo + "detector.onnx.data", 101799296,
            "9d34bac595f6feffb3f01116aebf01c343e87e711a4852557b440af0d5d0dae7"),
        new(MatteModel, VitMatteRepo + "model.onnx", 103885865,
            "bf28d2e0be2c073286e88d60ad649d7123da2749a2d99133fd1098d5887e0225"),
    ];

    public static long TotalDownloadBytes { get; } = ModelFiles.Sum(f => f.Size);

    /// <summary>Words that select the subject itself.</summary>
    public static IReadOnlyList<string> SubjectPrompts { get; } = ["character", "monster", "person", "creature"];

    /// <summary>Words for things the subject wears or holds; kept only when they touch the subject.</summary>
    public static IReadOnlyList<string> HeldItemPrompts { get; } =
        ["weapon", "sword", "axe", "staff", "wings", "hat", "cape", "tail"];

    public const float SubjectScoreThreshold = 0.35f;
    public const float HeldItemScoreThreshold = 0.40f;

    /// <summary>Px each side of the SAM 3 outline that ViTMatte re-decides.</summary>
    public const int TrimapBand = 12;

    /// <summary>Px a held item may be away from the subject and still count as touching.</summary>
    public const int HeldItemTouchRadius = 6;

    private const int VisionSize = 1008;
    private const int MaskGrid = 288;
    private const int MaxMatteSide = 1536;

    private readonly string _modelDirectory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly bool _forceCpu;
    private readonly object _sessionLock = new();
    private InferenceSession? _vision;
    private InferenceSession? _detector;
    private InferenceSession? _matte;

    public Sam3CutoutService(string? modelDirectory = null, HttpClient? httpClient = null)
    {
        _modelDirectory = modelDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Floowan",
            "models",
            "sam3cutout");
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _forceCpu = string.Equals(Environment.GetEnvironmentVariable("FLOOWAN_SAM3_CPU"), "1", StringComparison.Ordinal);
    }

    public string ModelDirectory => _modelDirectory;

    /// <summary>"GPU (DirectML)" or "CPU" once sessions exist; null before first use.</summary>
    public string? ExecutionDevice { get; private set; }

    /// <summary>Device of the ViTMatte session ("GPU (DirectML)" / "CPU"); null before first use.</summary>
    public string? MatteDevice { get; private set; }

    public bool AreModelsPresent() => ModelFiles.All(IsFileReady);

    /// <summary>The ViTMatte file alone, for <see cref="RefineMaskEdge"/>.</summary>
    public static ModelFile MatteModelFile { get; } = ModelFiles.Single(f => f.RelativePath == MatteModel);

    public bool IsMatteModelPresent() => IsFileReady(MatteModelFile);

    public Task EnsureModelsAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        EnsureFilesAsync(ModelFiles, "SAM 3 + ViTMatte models", progress, cancellationToken);

    /// <summary>
    /// Downloads only ViTMatte (~100 MB) for <see cref="RefineMaskEdge"/>; SAM 3 itself is not needed.
    /// </summary>
    public Task EnsureMatteModelAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        EnsureFilesAsync([MatteModelFile], "ViTMatte edge model", progress, cancellationToken);

    private async Task EnsureFilesAsync(
        IReadOnlyList<ModelFile> files,
        string label,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var missing = await Task.Run(() => files.Where(f => !IsFileReady(f)).ToList(), cancellationToken)
            .ConfigureAwait(false);
        if (missing.Count == 0)
            return;

        Directory.CreateDirectory(_modelDirectory);
        var total = missing.Sum(f => f.Size);
        long done = 0;
        var lastPercent = -1;
        progress?.Report($"Downloading {label} (~{total / (1024 * 1024)} MB, first use only)…");
        foreach (var file in missing)
        {
            var path = PathOf(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using var response = await _httpClient.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        done += read;
                        var percent = (int)(done * 100 / Math.Max(1, total));
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report($"Downloading {label}… {percent}%");
                        }
                    }
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Downloaded {file.RelativePath} failed its SHA-256 integrity check.");
                File.Move(tempPath, path, overwrite: true);
                await File.WriteAllTextAsync(MarkerOf(file), file.Sha256, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Subject alpha at <paramref name="source"/> size (0–255). All zero when SAM 3 finds no
    /// character / monster. Call <see cref="EnsureModelsAsync"/> first. Not thread-safe.
    /// </summary>
    public Image<L8> PredictMask(Image<Rgba32> source, bool includeHeldItems = true, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureSessions();
        try
        {
            return RunPipeline(source, includeHeldItems, progress);
        }
        catch (OnnxRuntimeException) when (ExecutionDevice != "CPU")
        {
            // Some drivers accept the DirectML session but fail at run time: retry once on CPU.
            progress?.Report("GPU run failed — retrying SAM 3 on CPU (slower)…");
            ResetSessions(forceCpu: true);
            return RunPipeline(source, includeHeldItems, progress);
        }
    }

    /// <summary>
    /// Re-places the edge of any coarse subject mask with ViTMatte, the same edge step SAM 3 uses.
    /// The mask is cut at <paramref name="keepThreshold"/> (the over-frame composer's keep
    /// threshold), then ViTMatte decides <paramref name="band"/> px either side of that outline:
    /// inside is 255, outside 0, the band is ViTMatte's alpha. Removes speckles and stair-stepped
    /// edges from low-resolution masks (SAM 2); does not change what was selected beyond the band.
    /// Returns a new mask at <paramref name="source"/> size, or a copy of <paramref name="mask"/>
    /// when it is empty. Needs only the ViTMatte file (<see cref="EnsureMatteModelAsync"/>).
    /// Not thread-safe.
    /// </summary>
    public Image<L8> RefineMaskEdge(
        Image<Rgba32> source,
        Image<L8> mask,
        byte keepThreshold,
        int band = TrimapBand,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Width != source.Width || mask.Height != source.Height)
            throw new ArgumentException("Mask size must match the source image.", nameof(mask));

        var width = source.Width;
        var height = source.Height;
        var coarse = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < width; x++)
                coarse[y * width + x] = row[x].PackedValue >= keepThreshold;
        }

        if (!Sam3MaskMath.Any(coarse))
            return mask.Clone();

        EnsureMatteSession();
        progress?.Report($"Refining the edge with ViTMatte ({MatteDevice})…");
        var trimap = Sam3MaskMath.BuildTrimap(coarse, width, height, band);
        float[] alpha;
        try
        {
            alpha = RunMatte(source, trimap);
        }
        catch (OnnxRuntimeException) when (MatteDevice != "CPU")
        {
            progress?.Report("GPU run failed — retrying ViTMatte on CPU (slower)…");
            ResetMatteSession(forceCpu: true);
            alpha = RunMatte(source, trimap);
        }

        return ComposeMatteMask(trimap, alpha, width, height);
    }

    private Image<L8> RunPipeline(Image<Rgba32> source, bool includeHeldItems, IProgress<string>? progress)
    {
        var width = source.Width;
        var height = source.Height;
        progress?.Report($"Finding the subject with SAM 3 ({ExecutionDevice})…");

        using var visionOutputs = _vision!.Run(
            [NamedOnnxValue.CreateFromTensor("pixel_values", BuildVisionInput(source))],
            ["features_288", "features_144", "features_72"]);
        var features = visionOutputs.ToDictionary(v => v.Name, v => v.AsTensor<float>());

        var subject = new bool[width * height];
        foreach (var prompt in SubjectPrompts)
        {
            foreach (var m in Detect(features, prompt, SubjectScoreThreshold, width, height))
                Sam3MaskMath.UnionInto(subject, m);
        }

        if (!Sam3MaskMath.Any(subject))
            return new Image<L8>(width, height);

        if (includeHeldItems)
        {
            var main = (bool[])subject.Clone();
            var near = Sam3MaskMath.Dilate(main, width, height, HeldItemTouchRadius);
            foreach (var prompt in HeldItemPrompts)
            {
                foreach (var m in Detect(features, prompt, HeldItemScoreThreshold, width, height))
                {
                    if (Sam3MaskMath.IsAttachedHeldItem(m, main, near))
                        Sam3MaskMath.UnionInto(subject, m);
                }
            }
        }

        progress?.Report("Placing the subject edge with ViTMatte…");
        var trimap = Sam3MaskMath.BuildTrimap(subject, width, height, TrimapBand);
        var alpha = RunMatte(source, trimap);
        return ComposeMatteMask(trimap, alpha, width, height);
    }

    /// <summary>Trimap foreground → 255, background → 0, unknown band → ViTMatte alpha.</summary>
    private static Image<L8> ComposeMatteMask(byte[] trimap, float[] alpha, int width, int height)
    {
        var mask = new Image<L8>(width, height);
        for (var y = 0; y < height; y++)
        {
            var row = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                row[x] = new L8(Sam3MaskMath.ComposeTrimapAlpha(trimap[i], alpha[i]));
            }
        }

        return mask;
    }

    private static DenseTensor<float> BuildVisionInput(Image<Rgba32> source)
    {
        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(VisionSize, VisionSize),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic
        }));
        var tensor = new DenseTensor<float>([1, 3, VisionSize, VisionSize]);
        var buffer = tensor.Buffer.Span;
        var plane = VisionSize * VisionSize;
        for (var y = 0; y < VisionSize; y++)
        {
            var row = resized.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < VisionSize; x++)
            {
                var p = row[x];
                var i = y * VisionSize + x;
                buffer[i] = p.R / 127.5f - 1f;
                buffer[plane + i] = p.G / 127.5f - 1f;
                buffer[2 * plane + i] = p.B / 127.5f - 1f;
            }
        }

        return tensor;
    }

    private List<bool[]> Detect(
        IReadOnlyDictionary<string, Tensor<float>> features,
        string prompt,
        float threshold,
        int width,
        int height)
    {
        var (textFeatures, textMask) = Sam3PromptFeatures.Embedded.Get(prompt);
        var seqLen = textMask.Length;
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("features_288", features["features_288"]),
            NamedOnnxValue.CreateFromTensor("features_144", features["features_144"]),
            NamedOnnxValue.CreateFromTensor("features_72", features["features_72"]),
            NamedOnnxValue.CreateFromTensor("text_features",
                new DenseTensor<float>(textFeatures, [1, seqLen, textFeatures.Length / seqLen])),
            NamedOnnxValue.CreateFromTensor("text_mask", new DenseTensor<long>(textMask, [1, seqLen])),
        };
        using var outputs = _detector!.Run(inputs, ["pred_logits", "presence_logits", "pred_masks"]);
        var byName = outputs.ToDictionary(v => v.Name, v => v.AsTensor<float>());
        var logits = byName["pred_logits"].ToArray();
        var presence = Sigmoid(byName["presence_logits"].ToArray()[0]);
        var masks = ((DenseTensor<float>)byName["pred_masks"]).Buffer.Span;
        var plane = MaskGrid * MaskGrid;

        var found = new List<bool[]>();
        for (var q = 0; q < logits.Length; q++)
        {
            if (Sigmoid(logits[q]) * presence <= threshold)
                continue;
            var m = Sam3MaskMath.UpsampleLogitsToMask(masks, q * plane, MaskGrid, width, height);
            if (Sam3MaskMath.Any(m))
                found.Add(m);
        }

        return found;
    }

    private float[] RunMatte(Image<Rgba32> source, byte[] trimap)
    {
        var width = source.Width;
        var height = source.Height;
        var scale = Math.Min(1f, MaxMatteSide / (float)Math.Max(width, height));
        var mw = Math.Max(1, (int)MathF.Round(width * scale));
        var mh = Math.Max(1, (int)MathF.Round(height * scale));

        using var image = scale < 1f
            ? source.Clone(ctx => ctx.Resize(mw, mh, KnownResamplers.Bicubic))
            : source.Clone();
        var tri = scale < 1f ? ResizeNearest(trimap, width, height, mw, mh) : trimap;

        var pw = (mw + 31) / 32 * 32;
        var ph = (mh + 31) / 32 * 32;
        var tensor = new DenseTensor<float>([1, 4, ph, pw]);
        var buffer = tensor.Buffer.Span;
        var plane = pw * ph;
        for (var y = 0; y < mh; y++)
        {
            var row = image.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < mw; x++)
            {
                var p = row[x];
                var i = y * pw + x;
                buffer[i] = p.R / 127.5f - 1f;
                buffer[plane + i] = p.G / 127.5f - 1f;
                buffer[2 * plane + i] = p.B / 127.5f - 1f;
                buffer[3 * plane + i] = tri[y * mw + x] / 255f;
            }
        }

        using var outputs = _matte!.Run([NamedOnnxValue.CreateFromTensor("pixel_values", tensor)]);
        var alphas = ((DenseTensor<float>)outputs.First().AsTensor<float>()).Buffer.Span;
        var small = new float[mw * mh];
        for (var y = 0; y < mh; y++)
            alphas.Slice(y * pw, mw).CopyTo(small.AsSpan(y * mw, mw));
        if (scale >= 1f)
            return small;

        using var alphaImage = new Image<L16>(mw, mh);
        for (var y = 0; y < mh; y++)
        {
            var row = alphaImage.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < mw; x++)
                row[x] = new L16((ushort)(Math.Clamp(small[y * mw + x], 0f, 1f) * 65535f));
        }

        alphaImage.Mutate(ctx => ctx.Resize(width, height, KnownResamplers.Triangle));
        var full = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = alphaImage.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < width; x++)
                full[y * width + x] = row[x].PackedValue / 65535f;
        }

        return full;
    }

    private static byte[] ResizeNearest(byte[] src, int w, int h, int nw, int nh)
    {
        var dst = new byte[nw * nh];
        for (var y = 0; y < nh; y++)
        {
            var sy = Math.Min(h - 1, (int)((y + 0.5f) * h / nh));
            for (var x = 0; x < nw; x++)
                dst[y * nw + x] = src[sy * w + Math.Min(w - 1, (int)((x + 0.5f) * w / nw))];
        }

        return dst;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private void EnsureSessions()
    {
        lock (_sessionLock)
        {
            if (_vision is not null && _detector is not null && _matte is not null)
                return;
            if (!AreModelsPresent())
                throw new InvalidOperationException("SAM 3 cutout models are missing. Call EnsureModelsAsync first.");
            // A matte-only session from RefineMaskEdge is replaced so all three share one device.
            DisposeSessions();
            CreateSessions(useGpu: !_forceCpu);
        }
    }

    /// <summary>ViTMatte alone (for <see cref="RefineMaskEdge"/>); reuses the full set when loaded.</summary>
    private void EnsureMatteSession()
    {
        lock (_sessionLock)
        {
            if (_matte is not null)
                return;
            if (!IsMatteModelPresent())
                throw new InvalidOperationException("The ViTMatte model is missing. Call EnsureMatteModelAsync first.");
            CreateMatteSession(useGpu: !_forceCpu);
        }
    }

    private void ResetMatteSession(bool forceCpu)
    {
        lock (_sessionLock)
        {
            _matte?.Dispose();
            _matte = null;
            MatteDevice = null;
            CreateMatteSession(useGpu: !forceCpu && !_forceCpu);
        }
    }

    private void CreateMatteSession(bool useGpu)
    {
        if (useGpu)
        {
            try
            {
                _matte = CreateSession(MatteModel, gpu: true);
                MatteDevice = "GPU (DirectML)";
                return;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                _matte?.Dispose();
                _matte = null;
            }
        }

        _matte = CreateSession(MatteModel, gpu: false);
        MatteDevice = "CPU";
    }

    private void ResetSessions(bool forceCpu)
    {
        lock (_sessionLock)
        {
            DisposeSessions();
            CreateSessions(useGpu: !forceCpu && !_forceCpu);
        }
    }

    private void CreateSessions(bool useGpu)
    {
        if (useGpu)
        {
            try
            {
                _vision = CreateSession(VisionModel, gpu: true);
                _detector = CreateSession(DetectorModel, gpu: true);
                _matte = CreateSession(MatteModel, gpu: true);
                ExecutionDevice = "GPU (DirectML)";
                MatteDevice = ExecutionDevice;
                return;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                DisposeSessions();
            }
        }

        _vision = CreateSession(VisionModel, gpu: false);
        _detector = CreateSession(DetectorModel, gpu: false);
        _matte = CreateSession(MatteModel, gpu: false);
        ExecutionDevice = "CPU";
        MatteDevice = ExecutionDevice;
    }

    private InferenceSession CreateSession(string relativePath, bool gpu)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // The SAM 3 graphs log harmless constant-folding / node-placement warnings.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        if (gpu)
        {
            // DirectML requires these two settings.
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }

        return new InferenceSession(Path.Combine(_modelDirectory, relativePath), options);
    }

    private void DisposeSessions()
    {
        _vision?.Dispose();
        _detector?.Dispose();
        _matte?.Dispose();
        _vision = null;
        _detector = null;
        _matte = null;
        ExecutionDevice = null;
        MatteDevice = null;
    }

    private string PathOf(ModelFile file) => Path.Combine(_modelDirectory, file.RelativePath);

    private string MarkerOf(ModelFile file) => PathOf(file) + ".sha256";

    /// <summary>Size check every time; the full SHA-256 runs once at download and is remembered in a marker file.</summary>
    private bool IsFileReady(ModelFile file)
    {
        var path = PathOf(file);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
                return false;
            var marker = MarkerOf(file);
            if (File.Exists(marker) && File.ReadAllText(marker).Trim().Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return true;

            // Present but unverified (e.g. copied in by hand): hash it once.
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
            File.WriteAllText(marker, file.Sha256);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
            DisposeSessions();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

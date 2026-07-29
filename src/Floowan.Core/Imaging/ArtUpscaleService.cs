using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Floowan.Core.Imaging;

/// <summary>
/// Local Real-ESRGAN (anime video v3) ONNX upscaler for over-frame composition.
/// When source illustration is Master Duel <c>512×512</c> (or similar 512-class art),
/// upscales ×2 to <c>1024×1024</c> (or ×2 Pendulum) before Cover resize into the OF
/// art hole — sharper than Lanczos alone. Model is downloaded once into
/// <c>%LOCALAPPDATA%\Floowan\models\realesrgan</c>.
/// </summary>
public sealed class ArtUpscaleService : IDisposable
{
    public const string ModelName = "RealESR-AnimeVideo-v3_x4.onnx";

    /// <summary>
    /// Lightweight anime-tuned Real-ESRGAN v3 ONNX (BSD-3-Clause weights / conversion).
    /// Native scale is ×4; Floowan runs ×4 then Lanczos-down to the OF ×2 target.
    /// </summary>
    public const string ModelUrl =
        "https://huggingface.co/tidus2102/Real-ESRGAN/resolve/main/RealESR-AnimeVideo-v3_x4.onnx";

    public const string ModelSha256 =
        "00ece3ac21c43ee31459216b5174b2cea0c5325044c5142aeb840f4890e175ff";

    public const long ModelExpectedBytes = 2_495_473;

    /// <summary>Baked network upscale factor for <see cref="ModelName"/>.</summary>
    public const int ModelScale = 4;

    /// <summary>Desired OF compose upscale relative to MD illustration sizes.</summary>
    public const int TargetScale = 2;

    /// <summary>
    /// When false, OF skips Real-ESRGAN and keeps existing Lanczos Cover path.
    /// Default true; set env <c>FLOOWAN_OF_UPSCALE=0</c> to disable without rebuilding.
    /// </summary>
    public static bool IsFeatureEnabled
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("FLOOWAN_OF_UPSCALE");
            if (string.IsNullOrWhiteSpace(env))
                return true;
            return !(env is "0" or "false" or "False" or "FALSE" or "off" or "OFF");
        }
    }

    private readonly HttpClient _httpClient;
    private readonly string _modelPath;
    private readonly object _sessionLock = new();
    private InferenceSession? _session;

    public ArtUpscaleService(string? modelDirectory = null, HttpClient? httpClient = null)
    {
        modelDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Floowan",
            "models",
            "realesrgan");
        _modelPath = Path.Combine(modelDirectory, ModelName);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    public string ModelPath => _modelPath;

    /// <summary>
    /// True when art should be Real-ESRGAN-upscaled before OF compose (512-class MD
    /// illusts / near-512 squares). Already ≥1024 on the short side is skipped.
    /// </summary>
    public static bool NeedsOverFrameArtUpscale(int width, int height)
    {
        if (!IsFeatureEnabled || width <= 0 || height <= 0)
            return false;

        if (Math.Min(width, height) >= CardArtTextureSizes.NormalWidth * TargetScale)
            return false;

        // Canonical Master Duel illustration sizes.
        if (CardArtTextureSizes.IsNormal(width, height))
            return true;

        if (CardArtTextureSizes.IsPendulum(width, height))
            return true;

        // Near-512 square / 3:4 exports (tolerance for odd encodes).
        const int minSide = 480;
        const int maxSide = 560;
        if (Math.Min(width, height) >= minSide &&
            Math.Max(width, height) <= maxSide &&
            CardArtTextureSizes.HasNormalAspect(width, height))
            return true;

        if (CardArtTextureSizes.HasPendulumAspect(width, height) &&
            width >= minSide &&
            width <= maxSide)
            return true;

        return false;
    }

    /// <summary>OF target size after ×2 (512→1024, 512×683→1024×1366).</summary>
    public static (int Width, int Height) GetOverFrameUpscaleTargetSize(int width, int height) =>
        (Math.Max(1, width * TargetScale), Math.Max(1, height * TargetScale));

    public async Task EnsureModelAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsFeatureEnabled)
            return;

        // Hashing / existence must not run on the WPF UI thread.
        var ready = await Task.Run(
            () => File.Exists(_modelPath) && HasExpectedChecksum(_modelPath),
            cancellationToken).ConfigureAwait(false);
        if (ready)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
        var tempPath = _modelPath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            progress?.Report(
                $"Downloading Real-ESRGAN anime upscale model (~{ModelExpectedBytes / (1024 * 1024)} MB, first use only)…");
            using var response = await _httpClient.GetAsync(
                ModelUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? ModelExpectedBytes;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            var buffer = new byte[81920];
            long downloaded = 0;
            var lastPercent = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                if (total > 0)
                {
                    var percent = (int)(downloaded * 100 / total);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report($"Downloading Real-ESRGAN model… {percent}%");
                    }
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();

            await Task.Run(
                () =>
                {
                    if (!HasExpectedChecksum(tempPath))
                    {
                        throw new InvalidDataException(
                            "Downloaded Real-ESRGAN model failed its SHA-256 integrity check.");
                    }

                    File.Move(tempPath, _modelPath, overwrite: true);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// When <see cref="NeedsOverFrameArtUpscale"/> is true, returns a new ×2 RGB image
    /// (caller disposes). Otherwise returns <paramref name="source"/> unchanged.
    /// </summary>
    public Image<Rgba32> UpscaleRgbForOverFrameIfNeeded(
        Image<Rgba32> source,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!NeedsOverFrameArtUpscale(source.Width, source.Height))
            return source;

        var (targetW, targetH) = GetOverFrameUpscaleTargetSize(source.Width, source.Height);
        progress?.Report($"Upscaling illustration to {targetW}×{targetH} (Real-ESRGAN)…");
        return UpscaleRgbToTarget(source, targetW, targetH);
    }

    /// <summary>
    /// Upscales RGB + matching L8 mask when needed. Returns new images when upscaled
    /// (caller disposes); otherwise returns the same instances.
    /// </summary>
    public (Image<Rgba32> Source, Image<L8> Mask) UpscaleSubjectPairForOverFrameIfNeeded(
        Image<Rgba32> source,
        Image<L8> mask,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);
        if (source.Width != mask.Width || source.Height != mask.Height)
            throw new ArgumentException("Source and mask must share the same size.");

        if (!NeedsOverFrameArtUpscale(source.Width, source.Height))
            return (source, mask);

        var (targetW, targetH) = GetOverFrameUpscaleTargetSize(source.Width, source.Height);
        progress?.Report($"Upscaling illustration to {targetW}×{targetH} (Real-ESRGAN)…");
        var upscaled = UpscaleRgbToTarget(source, targetW, targetH);
        var upscaledMask = mask.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(targetW, targetH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
        return (upscaled, upscaledMask);
    }

    /// <summary>
    /// Upscales subject RGB/mask and optional background when OF upscale applies.
    /// Disposes replaced inputs when new images are produced.
    /// </summary>
    public void UpscalePreparedLayersInPlace(
        ref Image<Rgba32> source,
        ref Image<L8> mask,
        ref Image<Rgba32>? background,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);

        if (!NeedsOverFrameArtUpscale(source.Width, source.Height))
            return;

        var (newSource, newMask) = UpscaleSubjectPairForOverFrameIfNeeded(source, mask, progress);
        if (!ReferenceEquals(newSource, source))
        {
            source.Dispose();
            source = newSource;
        }

        if (!ReferenceEquals(newMask, mask))
        {
            mask.Dispose();
            mask = newMask;
        }

        if (background is null)
            return;

        if (NeedsOverFrameArtUpscale(background.Width, background.Height) ||
            background.Width != source.Width ||
            background.Height != source.Height)
        {
            var targetW = source.Width;
            var targetH = source.Height;
            Image<Rgba32> next;
            if (background.Width == targetW / TargetScale &&
                background.Height == targetH / TargetScale &&
                NeedsOverFrameArtUpscale(background.Width, background.Height))
            {
                next = UpscaleRgbToTarget(background, targetW, targetH);
            }
            else
            {
                next = background.Clone(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(targetW, targetH),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3
                }));
            }

            background.Dispose();
            background = next;
        }
    }

    public Image<Rgba32> UpscaleRgbToTarget(Image<Rgba32> source, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth));

        EnsureSession();

        // Native animevideov3 ONNX is ×4; crop float output then Lanczos to OF ×2 target.
        var modelOutW = source.Width * ModelScale;
        var modelOutH = source.Height * ModelScale;

        var input = new DenseTensor<float>([1, 3, source.Height, source.Width]);
        for (var y = 0; y < source.Height; y++)
        {
            var row = source.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < row.Length; x++)
            {
                var p = row[x];
                input[0, 0, y, x] = p.R / 255f;
                input[0, 1, y, x] = p.G / 255f;
                input[0, 2, y, x] = p.B / 255f;
            }
        }

        float[] outputPlane;
        int outH;
        int outW;
        lock (_sessionLock)
        {
            var inputName = _session!.InputMetadata.Keys.First();
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
            var tensor = results.First().AsTensor<float>();
            var dims = tensor.Dimensions.ToArray();
            if (dims.Length != 4 || dims[1] != 3)
                throw new InvalidDataException("Real-ESRGAN returned an unexpected output shape.");

            outH = dims[2];
            outW = dims[3];
            if (outW != modelOutW || outH != modelOutH)
            {
                // Still accept consistent ×4-ish results; resize to target afterward.
            }

            outputPlane = tensor.ToArray();
        }

        using var rgba4x = new Image<Rgba32>(outW, outH);
        var planeSize = outW * outH;
        for (var y = 0; y < outH; y++)
        {
            var row = rgba4x.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < outW; x++)
            {
                var i = y * outW + x;
                var r = (byte)Math.Clamp((int)MathF.Round(outputPlane[i] * 255f), 0, 255);
                var g = (byte)Math.Clamp((int)MathF.Round(outputPlane[planeSize + i] * 255f), 0, 255);
                var b = (byte)Math.Clamp((int)MathF.Round(outputPlane[planeSize * 2 + i] * 255f), 0, 255);
                // Preserve alpha via Lanczos after RGB decode (source usually opaque).
                row[x] = new Rgba32(r, g, b, 255);
            }
        }

        using var alpha = new Image<L8>(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            var srcRow = source.DangerousGetPixelRowMemory(y).Span;
            var aRow = alpha.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < srcRow.Length; x++)
                aRow[x] = new L8(srcRow[x].A);
        }

        alpha.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(outW, outH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        for (var y = 0; y < outH; y++)
        {
            var row = rgba4x.DangerousGetPixelRowMemory(y).Span;
            var aRow = alpha.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < row.Length; x++)
            {
                var p = row[x];
                row[x] = new Rgba32(p.R, p.G, p.B, aRow[x].PackedValue);
            }
        }

        if (outW == targetWidth && outH == targetHeight)
            return rgba4x.Clone();

        return rgba4x.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(targetWidth, targetHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
    }

    private void EnsureSession()
    {
        if (_session is not null)
            return;

        lock (_sessionLock)
        {
            if (_session is not null)
                return;
            if (!File.Exists(_modelPath))
                throw new FileNotFoundException(
                    "Real-ESRGAN model was not found. Call EnsureModelAsync first.",
                    _modelPath);
            _session = new InferenceSession(_modelPath);
        }
    }

    private static bool HasExpectedChecksum(string path)
    {
        using var stream = File.OpenRead(path);
        var checksum = Convert.ToHexString(SHA256.HashData(stream));
        return checksum.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }

        _httpClient.Dispose();
    }
}

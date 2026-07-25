using System.Security.Cryptography;
using Floowan.Core.Data;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Floowan.Core.Imaging;

/// <summary>
/// One-click over-frame art generation using SkyTNT
/// <c>anime-segmentation</c> (<c>isnetis</c>) ONNX.
/// Runs the model natively in .NET (no Python). The ONNX file is downloaded and
/// verified on first use from Hugging Face <c>skytnt/anime-seg</c>.
/// </summary>
public sealed class AutoOverFrameArtService : IDisposable
{
    /// <summary>SkyTNT recommended ISNet anime character model.</summary>
    public const string ModelName = "isnetis.onnx";

    /// <summary>Official Hugging Face resolve URL for <see cref="ModelName"/>.</summary>
    public const string ModelUrl =
        "https://huggingface.co/skytnt/anime-seg/resolve/main/isnetis.onnx";

    /// <summary>
    /// MD5 of the published <c>isnetis.onnx</c> (same bytes historically mirrored as
    /// rembg <c>isnet-anime.onnx</c>).
    /// </summary>
    public const string ModelMd5 = "6f184e756bb3bd901c8849220a83e38e";

    /// <summary>Legacy rembg release filename; migrated when checksum matches.</summary>
    public const string LegacyModelName = "isnet-anime.onnx";

    /// <summary>Matches SkyTNT <c>isnet_is</c> / HF Space default <c>img-size</c>.</summary>
    public const int ModelSize = 1024;

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
        CancellationToken cancellationToken = default,
        int subjectOffsetX = 0,
        int subjectOffsetY = 0,
        LinkMarkerMask? linkMarkers = null)
    {
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("Source card art was not found.", sourceImagePath);

        await EnsureModelAsync(progress, cancellationToken).ConfigureAwait(false);
        progress?.Report("Removing background with SkyTNT isnetis…");

        await Task.Run(() =>
        {
            using var prepared = PrepareCleanSource(sourceImagePath, progress);
            using var mask = PredictMask(prepared.Source);
            progress?.Report($"Compositing subject onto {frameStyle} frame (704×1024)…");
            using var result = OverFrameAutoArtComposer.Compose(
                prepared.Source,
                mask,
                frameStyle,
                subjectOffsetX: subjectOffsetX,
                subjectOffsetY: subjectOffsetY);
            ApplyLinkArrowsIfNeeded(result, frameStyle, linkMarkers, progress);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);
            result.Save(outputPngPath, new PngEncoder());
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report("Automatic over-frame art is ready for review.");
    }

    /// <summary>
    /// Loads a user-provided subject image and uses its existing alpha channel as the
    /// subject mask (no segmentation). Caller must dispose both images.
    /// </summary>
    public static (Image<Rgba32> Source, Image<L8> Mask) LoadSubjectFromAlpha(
        string sourceImagePath,
        IProgress<string>? progress = null)
    {
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("Source card art was not found.", sourceImagePath);

        progress?.Report("Reading subject alpha mask…");
        var source = Image.Load<Rgba32>(sourceImagePath);
        var mask = new Image<L8>(source.Width, source.Height);
        var keep = 0;
        for (var y = 0; y < source.Height; y++)
        {
            var pixels = source.DangerousGetPixelRowMemory(y).Span;
            var maskRow = mask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < pixels.Length; x++)
            {
                var alpha = pixels[x].A;
                maskRow[x] = new L8(alpha);
                if (alpha >= OverFrameAutoArtComposer.MaskKeepThreshold)
                    keep++;
            }
        }

        if (keep == 0)
        {
            source.Dispose();
            mask.Dispose();
            throw new InvalidOperationException(
                "No opaque subject found in the image alpha channel. " +
                "Provide a PNG (or other image) with an alpha layer around the subject.");
        }

        progress?.Report("Subject alpha mask ready.");
        return (source, mask);
    }

    /// <summary>
    /// Runs SkyTNT anime-segmentation on card art and returns the cleaned illustration +
    /// subject mask for Custom OF Card Art layering. Caller must dispose both images.
    /// Does not compose a frame (unlike <see cref="CreateAsync"/>).
    /// Kept as <c>PrepareSubjectWithRembgAsync</c> for stable callers.
    /// </summary>
    public async Task<(Image<Rgba32> Source, Image<L8> Mask)> PrepareSubjectWithRembgAsync(
        string sourceImagePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("Source card art was not found.", sourceImagePath);

        await EnsureModelAsync(progress, cancellationToken).ConfigureAwait(false);
        progress?.Report("Removing background with SkyTNT isnetis…");

        return await Task.Run(() =>
        {
            using var prepared = PrepareCleanSource(sourceImagePath, progress);
            var source = prepared.Source.Clone();
            Image<L8>? mask = null;
            try
            {
                mask = PredictMask(source);
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

                if (keep == 0)
                {
                    throw new InvalidOperationException(
                        "Anime segmentation found no opaque subject in the live card art. " +
                        "Try Select subject… with a PNG that already has alpha.");
                }

                progress?.Report("Subject cutout mask ready.");
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
    /// Composes a previously prepared subject onto a frame (no model inference).
    /// Defaults to <see cref="OverFrameComposeMode.CustomArtOnly"/> for the Custom OF dialog.
    /// Optional <paramref name="background"/> Cover-fills the art hole under the frame
    /// (CustomArtOnly only; ignored for Auto-create). Optional background scale
    /// (shared ×0.5–×4 with Card Art; ×1 = Cover) and H/V pan move that Cover within
    /// the hole without overflowing it (when scale ≥ ×1).
    /// </summary>
    public static void ComposePreparedSubject(
        Image<Rgba32> source,
        Image<L8> mask,
        string outputPngPath,
        CardFrameStyle frameStyle = CardFrameStyle.Effect,
        int subjectOffsetX = 0,
        int subjectOffsetY = 0,
        float subjectScale = 1f,
        OverFrameComposeMode composeMode = OverFrameComposeMode.CustomArtOnly,
        Image<Rgba32>? background = null,
        float backgroundScale = 1f,
        int backgroundOffsetX = 0,
        int backgroundOffsetY = 0,
        LinkMarkerMask? linkMarkers = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);
        using var result = OverFrameAutoArtComposer.Compose(
            source,
            mask,
            frameStyle,
            subjectOffsetX: subjectOffsetX,
            subjectOffsetY: subjectOffsetY,
            subjectScale: subjectScale,
            composeMode: composeMode,
            background: background,
            backgroundScale: backgroundScale,
            backgroundOffsetX: backgroundOffsetX,
            backgroundOffsetY: backgroundOffsetY);
        ApplyLinkArrowsIfNeeded(result, frameStyle, linkMarkers);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);
        result.Save(outputPngPath, new PngEncoder());
    }

    /// <summary>
    /// Topmost Link-arrow redraw after OF compose (subject already on canvas). No-op
    /// unless frame is Link and <paramref name="linkMarkers"/> is set (including
    /// <see cref="LinkMarkerMask.None"/>, which skips redraw — pass null when markers
    /// are unknown). Active bits are painted lit orange/red; inactive stay dark on the frame.
    /// </summary>
    public static void ApplyLinkArrowsIfNeeded(
        Image<Rgba32> canvas,
        CardFrameStyle frameStyle,
        LinkMarkerMask? linkMarkers,
        IProgress<string>? progress = null)
    {
        if (!LinkArrowOverlay.NeedsArrowOverlay(frameStyle) || linkMarkers is null)
            return;
        LinkArrowOverlay.Apply(canvas, linkMarkers.Value);
        progress?.Report(
            linkMarkers.Value == LinkMarkerMask.None
                ? "Link frame: no active arrows to redraw."
                : $"Lighting Link arrows ({LinkMarkerMaskConvert.Count(linkMarkers.Value)} directions)…");
    }

    private static PreparedSource PrepareCleanSource(
        string sourceImagePath,
        IProgress<string>? progress)
    {
        var loaded = Image.Load<Rgba32>(sourceImagePath);
        var loadedW = loaded.Width;
        var loadedH = loaded.Height;
        // Crop OF / Pendulum canvases to illustration bounds before segmentation.
        var source = OverFrameAutoArtComposer.RequireCleanIllustrationSource(loaded);
        if (!ReferenceEquals(loaded, source))
            loaded.Dispose();

        if (OverFrameAutoArtComposer.IsOverFrameTextureSize(loadedW, loadedH))
        {
            progress?.Report("Source was 704×1024 — cropped art window before cutout…");
        }
        else if (CardArtTextureSizes.IsPendulumNativeCanvas(loadedW, loadedH) ||
                 CardArtTextureSizes.IsPendulum(loadedW, loadedH) ||
                 CardArtTextureSizes.HasPendulumAspect(loadedW, loadedH))
        {
            progress?.Report(
                $"Source was Pendulum {loadedW}×{loadedH} — using 3:4 art for cutout…");
        }

        return new PreparedSource(source);
    }

    private sealed class PreparedSource(Image<Rgba32> source) : IDisposable
    {
        public Image<Rgba32> Source { get; } = source;
        public void Dispose() => Source.Dispose();
    }

    private async Task EnsureModelAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        TryMigrateLegacyModel();

        if (File.Exists(_modelPath) && HasExpectedChecksum(_modelPath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
        var tempPath = _modelPath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            progress?.Report(
                "Downloading SkyTNT isnetis model (~168 MB, first use only)…");
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
                    progress?.Report($"Downloading isnetis model… {downloaded * 100 / total.Value}%");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();

            if (!HasExpectedChecksum(tempPath))
                throw new InvalidDataException("Downloaded isnetis model failed its MD5 integrity check.");

            File.Move(tempPath, _modelPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Reuse a previously downloaded rembg <c>isnet-anime.onnx</c> when it matches
    /// the SkyTNT <c>isnetis.onnx</c> checksum (byte-identical release).
    /// </summary>
    private void TryMigrateLegacyModel()
    {
        if (File.Exists(_modelPath) && HasExpectedChecksum(_modelPath))
            return;

        var legacyPath = Path.Combine(
            Path.GetDirectoryName(_modelPath)!,
            LegacyModelName);
        if (!File.Exists(legacyPath) || !HasExpectedChecksum(legacyPath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
        File.Copy(legacyPath, _modelPath, overwrite: true);
    }

    private Image<L8> PredictMask(Image<Rgba32> source)
    {
        _session ??= new InferenceSession(_modelPath);

        // SkyTNT inference.py / HF Space app.py get_mask:
        //   img/255 → aspect-preserving letterbox into s×s zeros → NCHW → run →
        //   crop pad → resize to original. Mask values are already ~[0,1].
        var letterbox = ComputeLetterbox(source.Width, source.Height, ModelSize);
        using var content = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(letterbox.ContentWidth, letterbox.ContentHeight),
            Mode = ResizeMode.Stretch,
            // cv2.resize default is bilinear; Triangle is the closest ImageSharp match.
            Sampler = KnownResamplers.Triangle
        }));

        var input = new DenseTensor<float>([1, 3, ModelSize, ModelSize]);
        for (var y = 0; y < letterbox.ContentHeight; y++)
        {
            var row = content.DangerousGetPixelRowMemory(y).Span;
            var destY = letterbox.PadTop + y;
            for (var x = 0; x < letterbox.ContentWidth; x++)
            {
                var pixel = row[x];
                var destX = letterbox.PadLeft + x;
                input[0, 0, destY, destX] = pixel.R / 255f;
                input[0, 1, destY, destX] = pixel.G / 255f;
                input[0, 2, destY, destX] = pixel.B / 255f;
            }
        }

        // Prefer the exported SkyTNT name "img"; fall back to the session's first input.
        var inputName = _session.InputMetadata.ContainsKey("img")
            ? "img"
            : _session.InputMetadata.Keys.First();
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        var output = results.First().AsTensor<float>();
        var prediction = output.ToArray();
        if (prediction.Length < ModelSize * ModelSize)
            throw new InvalidDataException("isnetis returned an unexpected output shape.");

        // Exported mask is [1,1,H,W] (or equivalent); use the last HxW plane.
        var offset = prediction.Length - ModelSize * ModelSize;
        using var squareMask = new Image<L8>(ModelSize, ModelSize);
        for (var y = 0; y < ModelSize; y++)
        {
            var row = squareMask.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < ModelSize; x++)
            {
                var value = Math.Clamp(prediction[offset + y * ModelSize + x], 0f, 1f);
                row[x] = new L8((byte)MathF.Round(value * 255f));
            }
        }

        // Crop letterbox content, then stretch back to the source size.
        var cropped = squareMask.Clone(ctx => ctx.Crop(new Rectangle(
            letterbox.PadLeft,
            letterbox.PadTop,
            letterbox.ContentWidth,
            letterbox.ContentHeight)));
        cropped.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = source.Size,
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Triangle
        }));
        return cropped;
    }

    /// <summary>
    /// SkyTNT letterbox geometry: fit the longer side to <paramref name="size"/>,
    /// pad the shorter axis with zeros (split with floor-half on top/left).
    /// </summary>
    public static LetterboxLayout ComputeLetterbox(int width, int height, int size = ModelSize)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image size must be positive.");
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        int contentH, contentW;
        if (height > width)
        {
            contentH = size;
            contentW = Math.Max(1, (int)(size * (double)width / height));
        }
        else
        {
            contentW = size;
            contentH = Math.Max(1, (int)(size * (double)height / width));
        }

        var padH = size - contentH;
        var padW = size - contentW;
        return new LetterboxLayout(
            ContentWidth: contentW,
            ContentHeight: contentH,
            PadLeft: padW / 2,
            PadTop: padH / 2);
    }

    public readonly record struct LetterboxLayout(
        int ContentWidth,
        int ContentHeight,
        int PadLeft,
        int PadTop);

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

using System.Security.Cryptography;
using Floowan.Core.Backup;
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
    private readonly ArtUpscaleService _artUpscale;
    private InferenceSession? _session;

    public AutoOverFrameArtService(string? modelDirectory = null, HttpClient? httpClient = null)
    {
        modelDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Floowan",
            "models");
        _modelPath = Path.Combine(modelDirectory, ModelName);
        _httpClient = httpClient ?? new HttpClient();
        // RemBG weights live in models/; Real-ESRGAN lives in models/realesrgan/.
        _artUpscale = new ArtUpscaleService(Path.Combine(modelDirectory, "realesrgan"));
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

        // OF always uses gradient chrome; solid dropdown / inference styles are mapped here.
        frameStyle = CardFrameTemplates.ToOfGradientStyle(frameStyle);

        await EnsureModelAsync(progress, cancellationToken).ConfigureAwait(false);
        await EnsureArtUpscaleModelAsync(progress, cancellationToken).ConfigureAwait(false);
        progress?.Report("Removing background with isnet-anime…");

        await Task.Run(() =>
        {
            using var prepared = PrepareCleanSource(sourceImagePath, progress);
            var source = prepared.Source;
            using var predictedMask = PredictMask(source);
            var mask = predictedMask;
            Image<Rgba32>? ownedSource = null;
            Image<L8>? ownedMask = null;
            try
            {
                (ownedSource, ownedMask) = _artUpscale.UpscaleSubjectPairForOverFrameIfNeeded(
                    source, mask, progress);
                if (!ReferenceEquals(ownedSource, source))
                    source = ownedSource;
                if (!ReferenceEquals(ownedMask, mask))
                    mask = ownedMask;

                progress?.Report($"Compositing subject onto {frameStyle} frame (704×1024)…");
                using var result = OverFrameAutoArtComposer.Compose(
                    source,
                    mask,
                    frameStyle,
                    subjectOffsetX: subjectOffsetX,
                    subjectOffsetY: subjectOffsetY);
                ApplyLinkArrowsIfNeeded(result, frameStyle, linkMarkers, progress);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);
                result.Save(outputPngPath, new PngEncoder());
            }
            finally
            {
                if (ownedSource is not null && !ReferenceEquals(ownedSource, prepared.Source))
                    ownedSource.Dispose();
                if (ownedMask is not null && !ReferenceEquals(ownedMask, predictedMask))
                    ownedMask.Dispose();
            }
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report("Automatic over-frame art is ready for review.");
    }

    /// <summary>
    /// Same as <see cref="CreateAsync"/>, but also returns editable Custom OF layers
    /// (rembg subject + mask + clean illustration as Cover background). Caller disposes
    /// the three images. Subject scale for CustomArtOnly edit ≈ <see cref="OverFrameAutoArtComposer.OverflowScale"/>
    /// so reopening Edit matches Auto overflow.
    /// </summary>
    public async Task<(Image<Rgba32> Subject, Image<L8> Mask, Image<Rgba32> Background)> CreateCapturingEditLayersAsync(
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

        frameStyle = CardFrameTemplates.ToOfGradientStyle(frameStyle);

        await EnsureModelAsync(progress, cancellationToken).ConfigureAwait(false);
        await EnsureArtUpscaleModelAsync(progress, cancellationToken).ConfigureAwait(false);
        progress?.Report("Removing background with isnet-anime…");

        return await Task.Run(() =>
        {
            using var prepared = PrepareCleanSource(sourceImagePath, progress);
            var background = prepared.Source.Clone();
            var subject = prepared.Source.Clone();
            Image<L8>? mask = null;
            try
            {
                mask = PredictMask(subject);
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
                        "rembg found no opaque subject in the card art for Auto OF.");
                }

                Image<Rgba32>? bg = background;
                _artUpscale.UpscalePreparedLayersInPlace(ref subject, ref mask, ref bg, progress);
                background = bg!;

                progress?.Report($"Compositing subject onto {frameStyle} frame (704×1024)…");
                using var result = OverFrameAutoArtComposer.Compose(
                    subject,
                    mask,
                    frameStyle,
                    subjectOffsetX: subjectOffsetX,
                    subjectOffsetY: subjectOffsetY);
                ApplyLinkArrowsIfNeeded(result, frameStyle, linkMarkers, progress);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPngPath))!);
                result.Save(outputPngPath, new PngEncoder());
                progress?.Report("Automatic over-frame art is ready for review.");
                return (subject, mask, background);
            }
            catch
            {
                subject.Dispose();
                mask?.Dispose();
                background.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a user-provided subject image and uses its existing alpha channel as the
    /// subject mask (no rembg). Caller must dispose both images.
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
    /// Same as <see cref="LoadSubjectFromAlpha"/>, then Real-ESRGAN ×2 when the art is
    /// 512-class (OF compose). Ensures the upscale model is present first.
    /// </summary>
    public async Task<(Image<Rgba32> Source, Image<L8> Mask)> LoadSubjectFromAlphaUpscaledAsync(
        string sourceImagePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureArtUpscaleModelAsync(progress, cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                var (source, mask) = LoadSubjectFromAlpha(sourceImagePath, progress);
                try
                {
                    Image<Rgba32>? bg = null;
                    _artUpscale.UpscalePreparedLayersInPlace(ref source, ref mask, ref bg, progress);
                    return (source, mask);
                }
                catch
                {
                    source.Dispose();
                    mask.Dispose();
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs rembg (isnet-anime) on card art and returns the cleaned illustration +
    /// subject mask for Custom OF Card Art layering. Caller must dispose both images.
    /// Does not compose a frame (unlike <see cref="CreateAsync"/>).
    /// </summary>
    public async Task<(Image<Rgba32> Source, Image<L8> Mask)> PrepareSubjectWithRembgAsync(
        string sourceImagePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("Source card art was not found.", sourceImagePath);

        await EnsureModelAsync(progress, cancellationToken).ConfigureAwait(false);
        await EnsureArtUpscaleModelAsync(progress, cancellationToken).ConfigureAwait(false);
        progress?.Report("Removing background with isnet-anime…");

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
                        "rembg found no opaque subject in the live card art. " +
                        "Try Select subject… with a PNG that already has alpha.");
                }

                Image<Rgba32>? bg = null;
                _artUpscale.UpscalePreparedLayersInPlace(ref source, ref mask, ref bg, progress);

                progress?.Report("Subject rembg mask ready.");
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
        frameStyle = CardFrameTemplates.ToOfGradientStyle(frameStyle);
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
        // Crop OF / Pendulum canvases to illustration bounds before rembg.
        var source = OverFrameAutoArtComposer.RequireCleanIllustrationSource(loaded);
        if (!ReferenceEquals(loaded, source))
            loaded.Dispose();

        if (OverFrameAutoArtComposer.IsOverFrameTextureSize(loadedW, loadedH))
        {
            progress?.Report("Source was 704×1024 — cropped art window before rembg…");
        }
        else if (CardArtTextureSizes.IsPendulumNativeCanvas(loadedW, loadedH) ||
                 CardArtTextureSizes.IsPendulum(loadedW, loadedH) ||
                 CardArtTextureSizes.HasPendulumAspect(loadedW, loadedH))
        {
            progress?.Report(
                $"Source was Pendulum {loadedW}×{loadedH} — using 3:4 art for rembg…");
        }

        return new PreparedSource(source);
    }

    private sealed class PreparedSource(Image<Rgba32> source) : IDisposable
    {
        public Image<Rgba32> Source { get; } = source;
        public void Dispose() => Source.Dispose();
    }

    private Task EnsureArtUpscaleModelAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        _artUpscale.EnsureModelAsync(progress, cancellationToken);

    /// <summary>
    /// Ensures the Real-ESRGAN model is present (download once), then upscales any
    /// 512-class Custom OF edit layers and runs transform compat so Cover scales /
    /// canvas offsets keep the same relative layout at 1024.
    /// When <c>Changed</c> is true, returned images replace the inputs (old instances
    /// were disposed) and the caller should re-persist to <c>of_edit_layer</c>.
    /// When false, returned images are the same instances as the inputs.
    /// </summary>
    public async Task<(
            bool Changed,
            Image<Rgba32>? Subject,
            Image<L8>? Mask,
            Image<Rgba32>? Background)> UpscaleSavedEditLayersIfNeededAsync(
        Image<Rgba32>? subject,
        Image<L8>? mask,
        Image<Rgba32>? background,
        CustomOverframeStageState state,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!OfEditLayerUpscaleCompat.NeedsLayerUpscale(subject, mask, background))
            return (false, subject, mask, background);

        await EnsureArtUpscaleModelAsync(progress, cancellationToken).ConfigureAwait(false);

        if (!OfEditLayerUpscaleCompat.TryGetReferenceLayerSize(
                subject, mask, background, out var fromW, out var fromH))
            return (false, subject, mask, background);

        var factor = OfEditLayerUpscaleCompat.ResolveSpatialUpscaleFactor(fromW, fromH);
        var subj = subject;
        var m = mask;
        var bg = background;
        var progressLocal = progress;
        var artUpscale = _artUpscale;
        var changed = await Task.Run(
            () => artUpscale.UpscaleEditLayersInPlace(ref subj, ref m, ref bg, progressLocal),
            cancellationToken).ConfigureAwait(false);
        if (!changed)
            return (false, subj, m, bg);

        OfEditLayerUpscaleCompat.AdjustTransformsAfterLayerUpscale(state, factor);
        return (true, subj, m, bg);
    }

    private async Task EnsureModelAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // MD5 of ~168 MB must not run on the WPF UI thread.
        var ready = await Task.Run(
            () => File.Exists(_modelPath) && HasExpectedChecksum(_modelPath),
            cancellationToken).ConfigureAwait(false);
        if (ready)
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
            var lastPercent = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                if (total > 0)
                {
                    var percent = (int)(downloaded * 100 / total.Value);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report($"Downloading isnet-anime model… {percent}%");
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
                            "Downloaded isnet-anime model failed its MD5 integrity check.");
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
        _artUpscale.Dispose();
        _httpClient.Dispose();
    }
}

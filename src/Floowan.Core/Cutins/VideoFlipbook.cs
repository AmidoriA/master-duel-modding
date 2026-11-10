using System.Globalization;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Floowan.Core.Cutins;

public sealed record VideoInfo(int Width, int Height, double Fps, double DurationSeconds)
{
    public int FrameCount => (int)Math.Round(DurationSeconds * Fps, MidpointRounding.ToEven);
}

/// <summary>Decodes video frames in order (Windows Media Foundation in the desktop app).</summary>
public interface IVideoFrameReader
{
    VideoInfo Probe(string path);

    /// <summary>Decode frames in order; frames for which <paramref name="keep"/> is false are skipped without copying.</summary>
    void ReadFrames(string path, Func<int, bool> keep, Action<int, Image<Rgba32>> onFrame, CancellationToken cancellationToken);
}

public sealed record VideoCutinOptions
{
    /// <summary>Cut-in length. The user's rule: always 2.0 s; the whole clip (or Start..End) is spread over it.</summary>
    public double Seconds { get; init; } = 2.0;
    public double Fps { get; init; } = 30;
    public int FrameHeight { get; init; } = 1080;
    public double Start { get; init; }
    public double? End { get; init; }
    /// <summary>&gt;1 hides the frame edges at the screen border.</summary>
    public double Overscan { get; init; } = 1.03;
    public double FadeIn { get; init; } = 0.08;
    public double FadeOut { get; init; } = 0.25;
}

public sealed record VideoFlipbookResult(
    string ContentDir,
    int Frames,
    int FrameWidth,
    int FrameHeight,
    (int Width, int Height) HdPage,
    (int Width, int Height) SdPage,
    double ClipSeconds,
    double Seconds,
    IReadOnlyList<Image<Rgba32>> PreviewFrames,
    double PreviewFps);

/// <summary>
/// A video as a Spine flipbook cut-in (C# port of scripts/cutin/video_cutin.py, full-screen mode): one slot whose
/// region attachment is swapped frame_0..frame_N-1 at even steps, all frames on one sheet (&lt;= 16384 px a side,
/// 2 px bled gutters so bilinear filtering never samples a neighbouring frame), sized to the game camera.
/// </summary>
public static class VideoFlipbook
{
    public const int MaxSide = 16384;
    public const int Gutter = 2;
    public const string PageName = "P19172.png";

    /// <summary>Skeleton units the game camera shows vertically for template P19172 (prefab scale 0.24, ortho 2.9).</summary>
    public const double GameViewHeight = 2 * 2.9 / (0.01 * 0.24);

    /// <summary>Sheet grid (columns, rows) with the smallest area, then the squarest; null when N frames don't fit.</summary>
    public static (int Cols, int Rows)? GridFor(int n, int cellWidth, int cellHeight)
    {
        (long Area, long Squareness, int Cols, int Rows)? best = null;
        for (var cols = 1; cols <= n; cols++)
        {
            var rows = (n + cols - 1) / cols;
            long w = (long)cols * cellWidth, h = (long)rows * cellHeight;
            if (w > MaxSide || h > MaxSide)
                continue;
            var key = (w * h, Math.Abs(w - h));
            if (best is null || key.Item1 < best.Value.Area || (key.Item1 == best.Value.Area && key.Item2 < best.Value.Squareness))
                best = (key.Item1, key.Item2, cols, rows);
        }
        return best is { } b ? (b.Cols, b.Rows) : null;
    }

    /// <summary>Source frame indices to use: N evenly spaced frames of Start..End (same choice as video_cutin.py).</summary>
    public static int[] PickFrames(VideoInfo info, VideoCutinOptions o)
    {
        var fps = info.Fps > 0 ? info.Fps : 30;
        var total = Math.Max(1, info.FrameCount);
        var first = Math.Clamp((int)Math.Round(o.Start * fps, MidpointRounding.ToEven), 0, total - 1);
        var last = o.End is { } end ? Math.Min(total - 1, (int)Math.Round(end * fps, MidpointRounding.ToEven) - 1) : total - 1;
        var span = Math.Max(1, last - first + 1);
        var n = Math.Min(Math.Max(1, (int)Math.Round(o.Seconds * o.Fps, MidpointRounding.ToEven)), span);
        return Enumerable.Range(0, n).Select(i => first + (int)(i * (double)span / n)).ToArray();
    }

    /// <summary>Number of source frames in Start..End (the clip that is spread over Seconds).</summary>
    public static int ClipFrames(VideoInfo info, VideoCutinOptions o)
    {
        var fps = info.Fps > 0 ? info.Fps : 30;
        var total = Math.Max(1, info.FrameCount);
        var first = Math.Clamp((int)Math.Round(o.Start * fps, MidpointRounding.ToEven), 0, total - 1);
        var last = o.End is { } end ? Math.Min(total - 1, (int)Math.Round(end * fps, MidpointRounding.ToEven) - 1) : total - 1;
        return Math.Max(1, last - first + 1);
    }

    public static (int Width, int Height) FrameSize(VideoInfo info, int n, int frameHeight)
    {
        var fh = Math.Min(frameHeight, info.Height);
        var fw = (int)Math.Round(info.Width * (double)fh / info.Height, MidpointRounding.ToEven);
        while (GridFor(n, fw + 2 * Gutter, fh + 2 * Gutter) is null)
        {
            fh = (int)(fh * 0.95);
            fw = (int)Math.Round(info.Width * (double)fh / info.Height, MidpointRounding.ToEven);
        }
        return (fw, fh);
    }

    public static VideoFlipbookResult Build(string videoPath, int cardId, string name, VideoCutinOptions o, IVideoFrameReader reader,
        string outDir, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outDir);
        var info = reader.Probe(videoPath);
        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException("The video has no picture.");
        var picks = PickFrames(info, o);
        var n = picks.Length;
        var (fw, fh) = FrameSize(info, n, o.FrameHeight);
        int cw = fw + 2 * Gutter, ch = fh + 2 * Gutter;
        var (cols, rows) = GridFor(n, cw, ch)!.Value;
        int hdW = cols * cw, hdH = rows * ch;
        int sdW = R(hdW * 0.5), sdH = R(hdH * 0.5);
        int sfw = R(fw * 0.5), sfh = R(fh * 0.5);
        var unitsH = GameViewHeight * o.Overscan;
        var unitsW = unitsH * fw / fh;
        const int pw = 960, ph = 540;
        var ppu = ph / GameViewHeight;
        int dw = Math.Max(1, R(unitsW * ppu)), dh = Math.Max(1, R(unitsH * ppu));
        var clipSeconds = ClipFrames(info, o) / (info.Fps > 0 ? info.Fps : 30);
        progress?.Report($"Reading {n} frames ({fw}x{fh}) from the video…");

        using var hd = new Image<Rgba32>(hdW, hdH);
        using var sd = new Image<Rgba32>(sdW, sdH);
        var small = new Image<Rgba32>?[n];
        var slotsBySource = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            if (!slotsBySource.TryGetValue(picks[i], out var list))
                slotsBySource[picks[i]] = list = [];
            list.Add(i);
        }

        void Place(int slot, Image<Rgba32> frame)
        {
            int c = slot % cols, r = slot / cols;
            using var f = frame.Clone(x => x.Resize(new ResizeOptions { Size = new Size(fw, fh), Sampler = KnownResamplers.Lanczos3, Mode = ResizeMode.Stretch }));
            Opaque(f);
            Blit(hd, f, c * cw + Gutter, r * ch + Gutter, Gutter);
            using var s = f.Clone(x => x.Resize(new ResizeOptions { Size = new Size(sfw, sfh), Sampler = KnownResamplers.Lanczos3, Mode = ResizeMode.Stretch }));
            Blit(sd, s, R(c * cw * 0.5) + 1, R(r * ch * 0.5) + 1, 1);
            small[slot] = f.Clone(x => x.Resize(dw, dh, KnownResamplers.Lanczos3));
        }

        Image<Rgba32>? lastFrame = null;
        var done = 0;
        try
        {
            reader.ReadFrames(videoPath, idx => slotsBySource.ContainsKey(idx), (idx, img) =>
            {
                foreach (var slot in slotsBySource[idx])
                    Place(slot, img);
                lastFrame?.Dispose();
                lastFrame = img.Clone();
                done += slotsBySource[idx].Count;
                if (done % 10 == 0)
                    progress?.Report($"Reading frames… {done * 100 / n}%");
            }, cancellationToken);
            if (lastFrame is null)
                throw new InvalidDataException("No frames could be decoded from the video.");
            for (var i = 0; i < n; i++)          // the decoder gave fewer frames than its duration said: hold the last one
                if (small[i] is null)
                    Place(i, lastFrame);
        }
        finally
        {
            lastFrame?.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Saving the frame sheets…");
        var png = new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed, ColorType = PngColorType.RgbWithAlpha };
        hd.SaveAsPng(Path.Combine(outDir, "hd.png"), png);
        sd.SaveAsPng(Path.Combine(outDir, "sd.png"), png);
        WriteAtlas(Path.Combine(outDir, "hd.atlas"), hdW, hdH, "1", n, i => (i % cols * cw + Gutter, i / cols * ch + Gutter, fw, fh));
        WriteAtlas(Path.Combine(outDir, "sd.atlas"), sdW, sdH, "0.5", n, i => (R(i % cols * cw * 0.5) + 1, R(i / cols * ch * 0.5) + 1, sfw, sfh));
        File.WriteAllText(Path.Combine(outDir, "skeleton.json"), SkeletonJson(n, unitsW, unitsH, o, cardId));

        var preview = PreviewFrames(small!, dw, dh, o, pw, ph);
        foreach (var s in small)
            s?.Dispose();
        SavePreviewGif(preview, Path.Combine(outDir, "preview.gif"), o.Fps);
        SavePreviewSheet(preview, Path.Combine(outDir, "preview_sheet.png"));

        var ext = Path.GetExtension(videoPath);
        var sourceName = "source" + (string.IsNullOrEmpty(ext) ? ".mp4" : ext.ToLowerInvariant());
        var sourceDst = Path.Combine(outDir, sourceName);
        if (!string.Equals(Path.GetFullPath(videoPath), Path.GetFullPath(sourceDst), StringComparison.OrdinalIgnoreCase))
            File.Copy(videoPath, sourceDst, overwrite: true);
        WriteJson(Path.Combine(outDir, "video.json"), w =>
        {
            w.WriteString("file", sourceName);
            w.WriteNumber("seconds", o.Seconds);
            w.WriteNumber("fps", o.Fps);
            w.WriteNumber("frame_height", o.FrameHeight);
            w.WriteNumber("start", o.Start);
            if (o.End is { } e) w.WriteNumber("end", e); else w.WriteNull("end");
            w.WriteNumber("overscan", o.Overscan);
            w.WriteNumber("fade_in", o.FadeIn);
            w.WriteNumber("fade_out", o.FadeOut);
        });
        WriteJson(Path.Combine(outDir, "meta.json"), w =>
        {
            w.WriteNumber("card_id", cardId);
            w.WriteString("name", name);
            w.WriteNumber("template", CutinNaming.DefaultTemplate);
            w.WriteNumber("duration", o.Seconds);
            w.WriteString("source", "video");
            w.WriteString("video", Path.GetFileName(videoPath));
            w.WriteNumber("frames", n);
            w.WriteStartArray("frame_px"); w.WriteNumberValue(fw); w.WriteNumberValue(fh); w.WriteEndArray();
            w.WriteStartObject("pages");
            w.WriteStartArray("hd"); w.WriteNumberValue(hdW); w.WriteNumberValue(hdH); w.WriteEndArray();
            w.WriteStartArray("sd"); w.WriteNumberValue(sdW); w.WriteNumberValue(sdH); w.WriteEndArray();
            w.WriteEndObject();
            w.WriteString("mode", "full");
            w.WriteString("built_by", "Floowan");
            w.WriteString("built", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        });
        return new VideoFlipbookResult(outDir, n, fw, fh, (hdW, hdH), (sdW, sdH), clipSeconds, o.Seconds, preview, o.Fps);
    }

    private static int R(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    private static void Opaque(Image<Rgba32> img) =>
        img.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < acc.Height; y++)
                foreach (ref var p in acc.GetRowSpan(y))
                    p.A = 255;
        });

    /// <summary>Copy <paramref name="frame"/> to (x, y) and repeat its edge pixels <paramref name="g"/> px outwards.</summary>
    public static void Blit(Image<Rgba32> canvas, Image<Rgba32> frame, int x, int y, int g)
    {
        int w = frame.Width, h = frame.Height;
        canvas.ProcessPixelRows(frame, (dst, src) =>
        {
            for (var yy = -g; yy < h + g; yy++)
            {
                var ty = y + yy;
                if (ty < 0 || ty >= dst.Height)
                    continue;
                var s = src.GetRowSpan(Math.Clamp(yy, 0, h - 1));
                var d = dst.GetRowSpan(ty);
                s.CopyTo(d.Slice(x, w));
                for (var k = 1; k <= g; k++)
                {
                    if (x - k >= 0) d[x - k] = s[0];
                    if (x + w - 1 + k < d.Length) d[x + w - 1 + k] = s[w - 1];
                }
            }
        });
    }

    private static void WriteAtlas(string path, int pageW, int pageH, string scale, int n, Func<int, (int X, int Y, int W, int H)> bounds)
    {
        var lines = new List<string> { PageName, $"size:{pageW},{pageH}", "filter:Linear,Linear", "pma:true", $"scale:{scale}" };
        for (var i = 0; i < n; i++)
        {
            var b = bounds(i);
            lines.Add($"frame_{i}");
            lines.Add($"bounds:{b.X},{b.Y},{b.W},{b.H}");
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    public static string SkeletonJson(int n, double unitsW, double unitsH, VideoCutinOptions o, int cardId)
    {
        var step = o.Seconds / n;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("skeleton");
            w.WriteString("hash", $"video{cardId}");
            w.WriteString("spine", "4.2.20");
            w.WriteNumber("x", -unitsW / 2);
            w.WriteNumber("y", -unitsH / 2);
            w.WriteNumber("width", Math.Round(unitsW, 2));
            w.WriteNumber("height", Math.Round(unitsH, 2));
            w.WriteNumber("fps", 60);
            w.WriteString("images", "./images/");
            w.WriteString("audio", "");
            w.WriteEndObject();
            w.WriteStartArray("bones");
            w.WriteStartObject(); w.WriteString("name", "root"); w.WriteEndObject();
            w.WriteStartObject(); w.WriteString("name", "Body"); w.WriteString("parent", "root"); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartArray("slots");
            w.WriteStartObject(); w.WriteString("name", "frame"); w.WriteString("bone", "Body"); w.WriteString("attachment", "frame_0"); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartArray("skins");
            w.WriteStartObject();
            w.WriteString("name", "default");
            w.WriteStartObject("attachments");
            w.WriteStartObject("frame");
            for (var i = 0; i < n; i++)
            {
                w.WriteStartObject($"frame_{i}");
                w.WriteNumber("width", Math.Round(unitsW, 2));
                w.WriteNumber("height", Math.Round(unitsH, 2));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("animations");
            w.WriteStartObject("animation");
            w.WriteStartObject("slots");
            w.WriteStartObject("frame");
            w.WriteStartArray("attachment");
            for (var i = 0; i < n; i++)
            {
                w.WriteStartObject(); w.WriteNumber("time", Math.Round(i * step, 4)); w.WriteString("name", $"frame_{i}"); w.WriteEndObject();
            }
            // the last key at Seconds holds the last frame, so the cut-in is exactly that long
            w.WriteStartObject(); w.WriteNumber("time", Math.Round(o.Seconds, 4)); w.WriteString("name", $"frame_{n - 1}"); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartArray("rgba");
            var times = new[] { 0.0, o.FadeIn, Math.Max(o.FadeIn, o.Seconds - o.FadeOut), o.Seconds };
            var alphas = new[] { o.FadeIn > 0 ? 0.0 : 1.0, 1.0, 1.0, o.FadeOut > 0 ? 0.0 : 1.0 };
            for (var i = 0; i < 4; i++)
            {
                w.WriteStartObject();
                w.WriteNumber("time", Math.Round(times[i], 4));
                w.WriteString("color", "ffffff" + R(alphas[i] * 255).ToString("x2", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Frames as the game shows them (in-game framing, fades), at <paramref name="o"/>.Fps over Seconds.</summary>
    public static List<Image<Rgba32>> PreviewFrames(IReadOnlyList<Image<Rgba32>> small, int dw, int dh, VideoCutinOptions o, int pw = 960, int ph = 540)
    {
        var n = small.Count;
        var step = o.Seconds / n;
        var x = (int)Math.Floor((pw - dw) / 2.0);
        var y = (int)Math.Floor((ph - dh) / 2.0);
        var frames = new List<Image<Rgba32>>();
        var count = R(o.Seconds * o.Fps) + 1;
        for (var fi = 0; fi < count; fi++)
        {
            var t = fi / o.Fps;
            var al = 1.0;
            if (o.FadeIn > 0 && t < o.FadeIn)
                al = t / o.FadeIn;
            if (o.FadeOut > 0 && t > o.Seconds - o.FadeOut)
                al = Math.Min(al, Math.Max(0, (o.Seconds - t) / o.FadeOut));
            var im = small[Math.Min(n - 1, (int)(t / step + 1e-6))];
            var canvas = new Image<Rgba32>(pw, ph, new Rgba32(40, 44, 52, 255));
            canvas.Mutate(c => c.DrawImage(im, new Point(x, y), (float)al));
            frames.Add(canvas);
        }
        return frames;
    }

    private static void SavePreviewGif(IReadOnlyList<Image<Rgba32>> frames, string path, double fps)
    {
        using var gif = frames[0].Clone(x => x.Resize(480, 270));
        var delay = Math.Max(2, (int)Math.Round(100 / fps));
        gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delay;
        for (var i = 1; i < frames.Count; i++)
        {
            using var f = frames[i].Clone(x => x.Resize(480, 270));
            var added = gif.Frames.AddFrame(f.Frames.RootFrame);
            added.Metadata.GetGifMetadata().FrameDelay = delay;
        }
        gif.Metadata.GetGifMetadata().RepeatCount = 0;
        gif.SaveAsGif(path);
    }

    private static void SavePreviewSheet(IReadOnlyList<Image<Rgba32>> frames, string path)
    {
        const int tw = 480, th = 270;
        using var sheet = new Image<Rgba32>(tw * 4, th * 2);
        for (var k = 0; k < 8; k++)
        {
            var fi = Math.Min(frames.Count - 1, R(k * (frames.Count - 1) / 7.0));
            using var t = frames[fi].Clone(x => x.Resize(tw, th));
            sheet.Mutate(c => c.DrawImage(t, new Point(k % 4 * tw, k / 4 * th), 1f));
        }
        sheet.SaveAsPng(path);
    }

    private static void WriteJson(string path, Action<Utf8JsonWriter> body)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        body(w);
        w.WriteEndObject();
    }

    /// <summary>Options saved beside a Floowan video cut-in (null for other cut-ins).</summary>
    public static (string SourceFile, VideoCutinOptions Options)? ReadVideoSettings(string contentDir)
    {
        var p = Path.Combine(contentDir, "video.json");
        if (!File.Exists(p))
            return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(p));
        var r = doc.RootElement;
        var file = Path.Combine(contentDir, r.GetProperty("file").GetString()!);
        if (!File.Exists(file))
            return null;
        double D(string k, double d) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
        var o = new VideoCutinOptions
        {
            Seconds = D("seconds", 2.0),
            Fps = D("fps", 30),
            FrameHeight = (int)D("frame_height", 1080),
            Start = D("start", 0),
            End = r.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null,
            Overscan = D("overscan", 1.03),
            FadeIn = D("fade_in", 0.08),
            FadeOut = D("fade_out", 0.25),
        };
        return (file, o);
    }
}

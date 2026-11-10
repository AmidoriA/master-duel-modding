using System.IO;
using System.Runtime.InteropServices;
using Floowan.Core.Cutins;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Vortice.MediaFoundation;
using VideoInfo = Floowan.Core.Cutins.VideoInfo;

namespace Floowan.Desktop;

/// <summary>
/// Decodes video with Windows Media Foundation (Source Reader, video processing to RGB32): the codecs Windows
/// already has (H.264/HEVC .mp4, .mov, .wmv; VP9/AV1 .webm with the free Microsoft Store extensions).
/// </summary>
public sealed class MediaFoundationVideoReader : IVideoFrameReader
{
    public VideoInfo Probe(string path)
    {
        Startup();
        try
        {
            using var reader = Open(path);
            using var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            MediaFactory.MFGetAttributeSize(cur, MediaTypeAttributeKeys.FrameSize, out var w, out var h).CheckError();
            var rate = cur.GetUInt64(MediaTypeAttributeKeys.FrameRate);
            var num = rate >> 32;
            var den = rate & 0xFFFFFFFF;
            var fps = den == 0 || num == 0 ? 30.0 : num / (double)den;
            double duration;
            try
            {
                var v = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
                duration = Convert.ToUInt64(v.Value) / 10_000_000.0;
            }
            catch
            {
                duration = 0;
            }
            if (duration <= 0)
                duration = CountFrames(reader) / fps;
            return new VideoInfo((int)w, (int)h, fps, duration);
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    public void ReadFrames(string path, Func<int, bool> keep, Action<int, Image<Rgba32>> onFrame, CancellationToken cancellationToken)
    {
        Startup();
        try
        {
            using var reader = Open(path);
            using var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            MediaFactory.MFGetAttributeSize(cur, MediaTypeAttributeKeys.FrameSize, out var uw, out var uh).CheckError();
            int w = (int)uw, h = (int)uh;
            var index = 0;
            byte[]? bgra = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out _);
                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                    break;
                if (sample is null)
                    continue;
                var i = index++;
                if (!keep(i))
                    continue;
                using var buf = sample.ConvertToContiguousBuffer();
                var stride = Copy(buf, w, h, ref bgra);
                using var img = new Image<Rgba32>(w, h);
                img.ProcessPixelRows(acc =>
                {
                    for (var y = 0; y < h; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        var s = y * stride;
                        for (var x = 0; x < w; x++, s += 4)
                            row[x] = new Rgba32(bgra![s + 2], bgra[s + 1], bgra[s], 255);
                    }
                });
                onFrame(i, img);
            }
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static void Startup() => MediaFactory.MFStartup(true).CheckError();

    private static IMFSourceReader Open(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Video not found.", path);
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true);
        IMFSourceReader reader;
        try
        {
            reader = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Windows could not open this video (unsupported format or codec). Try an .mp4 (H.264).", ex);
        }
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using var want = MediaFactory.MFCreateMediaType();
        want.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        want.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, want);
        return reader;
    }

    private static int CountFrames(IMFSourceReader reader)
    {
        var n = 0;
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out _);
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
                return n;
            if (sample is not null)
                n++;
        }
    }

    /// <summary>Copy a decoded RGB32 frame (top-down); returns the row stride. Decoders may pad rows (1080 -> 1088).</summary>
    private static int Copy(IMFMediaBuffer buf, int w, int h, ref byte[]? bgra)
    {
        using var b2 = buf.QueryInterfaceOrNull<IMF2DBuffer>();
        if (b2 is not null)
        {
            b2.Lock2D(out var line0, out var pitch);
            try
            {
                var stride = Math.Abs(pitch);
                if (bgra is null || bgra.Length < stride * h)
                    bgra = new byte[stride * h];
                for (var y = 0; y < h; y++)
                    Marshal.Copy(line0 + y * pitch, bgra, y * stride, Math.Min(stride, w * 4));
                return stride;
            }
            finally
            {
                b2.Unlock2D();
            }
        }
        buf.Lock(out var ptr, out _, out var len);
        try
        {
            var stride = w * 4;
            if (bgra is null || bgra.Length < stride * h)
                bgra = new byte[stride * h];
            Marshal.Copy(ptr, bgra, 0, Math.Min(len, stride * h));
            return stride;
        }
        finally
        {
            buf.Unlock();
        }
    }
}

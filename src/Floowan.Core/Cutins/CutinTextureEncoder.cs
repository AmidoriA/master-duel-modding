using System.Diagnostics;
using System.Security.Cryptography;

namespace Floowan.Core.Cutins;

/// <summary>BC7 data for a Unity Texture2D: rows bottom-up, as Unity stores them.</summary>
public sealed record EncodedTexture(byte[] Data, int Width, int Height);

public interface ICutinTextureEncoder
{
    /// <summary>Encode a (premultiplied) PNG page as BC7, flipped vertically for Unity.</summary>
    EncodedTexture EncodeBc7(string pngPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// BC7 through Microsoft's texconv (DirectXTex, MIT): DirectCompute on the GPU, CPU otherwise.
/// A 11544x10840 video sheet takes about 10-30 s on an RTX 5080 (pure C# BC7 took ~2 minutes).
/// </summary>
public sealed class TexconvBc7Encoder : ICutinTextureEncoder
{
    private readonly string _texconvPath;

    public TexconvBc7Encoder(string texconvPath) => _texconvPath = texconvPath;

    public EncodedTexture EncodeBc7(string pngPath, CancellationToken cancellationToken = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "floowan-texconv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var psi = new ProcessStartInfo(_texconvPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-nologo", "-vflip", "-f", "BC7_UNORM", "-m", "1", "-y", "-o", tmp, pngPath })
                psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start texconv.");
            var stdout = p.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = p.StandardError.ReadToEndAsync(cancellationToken);
            while (!p.WaitForExit(250))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            var dds = Path.Combine(tmp, Path.GetFileNameWithoutExtension(pngPath) + ".dds");
            if (p.ExitCode != 0 || !File.Exists(dds))
                throw new InvalidOperationException($"texconv failed ({p.ExitCode}): {stdout.Result}{stderr.Result}".Trim());
            return ReadBc7Dds(File.ReadAllBytes(dds));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Pixel data of a single-mip BC7 DDS (legacy 128-byte or DX10 148-byte header).</summary>
    public static EncodedTexture ReadBc7Dds(byte[] dds)
    {
        if (dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ')
            throw new InvalidDataException("Not a DDS file.");
        var height = BitConverter.ToInt32(dds, 12);
        var width = BitConverter.ToInt32(dds, 16);
        var fourCc = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
        var header = fourCc == "DX10" ? 148 : 128;
        var expected = (long)((width + 3) / 4) * ((height + 3) / 4) * 16;
        if (dds.Length - header < expected)
            throw new InvalidDataException($"DDS too short for {width}x{height} BC7.");
        return new EncodedTexture(dds.AsSpan(header, (int)expected).ToArray(), width, height);
    }
}

/// <summary>Finds texconv.exe, downloading the pinned DirectXTex release once (like the app's model weights).</summary>
public static class TexconvProvider
{
    public const string Version = "may2026";
    public const string Url = "https://github.com/microsoft/DirectXTex/releases/download/may2026/texconv.exe";
    public const string Sha256 = "dcfdec10244e02cf5037fba089c55fb7e1326b1c8181742d77d15fa5cb5eef06";

    public static string DownloadPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Floowan", "tools", $"texconv-{Version}.exe");

    /// <summary>tools\texconv.exe beside the app (if shipped), else the downloaded copy.</summary>
    public static string? FindExisting()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "tools", "texconv.exe");
        if (File.Exists(beside))
            return beside;
        return File.Exists(DownloadPath) && HasExpectedChecksum(DownloadPath) ? DownloadPath : null;
    }

    public static async Task<string> EnsureAsync(HttpClient http, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var existing = await Task.Run(FindExisting, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return existing;
        Directory.CreateDirectory(Path.GetDirectoryName(DownloadPath)!);
        var tmp = DownloadPath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            progress?.Report("Downloading texconv (Microsoft DirectXTex, ~1 MB, first use only)…");
            var bytes = await http.GetByteArrayAsync(Url, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(tmp, bytes, cancellationToken).ConfigureAwait(false);
            if (!HasExpectedChecksum(tmp))
                throw new InvalidDataException("Downloaded texconv.exe failed its SHA-256 integrity check.");
            File.Move(tmp, DownloadPath, overwrite: true);
            return DownloadPath;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static bool HasExpectedChecksum(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    }
}

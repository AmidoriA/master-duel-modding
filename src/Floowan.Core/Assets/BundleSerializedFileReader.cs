using System.Buffers;
using AssetsTools.NET;

namespace Floowan.Core.Assets;

/// <summary>
/// One serialized (non-resource) file decoded from a UnityFS AssetBundle.
/// <see cref="Data"/> holds <see cref="Length"/> bytes starting at <see cref="Offset"/>.
/// </summary>
public readonly record struct BundleSerializedFile(string Name, byte[] Data, int Offset, int Length)
{
    public MemoryStream OpenRead() => new(Data, Offset, Length, writable: false);
}

/// <summary>
/// Thrown when a bundle uses a block compression this reader does not decode (LZHAM or an
/// unknown id). Callers fall back to a full AssetsTools unpack.
/// </summary>
public sealed class UnsupportedBundleCompressionException(int compression)
    : NotSupportedException($"Bundle block compression {compression} is not supported by the partial reader.");

/// <summary>
/// Reads only the serialized-file entries (the <c>CAB-…</c> metadata) of a UnityFS AssetBundle,
/// without decompressing the <c>.resS</c> texture payload that follows them.
/// <para>
/// Card illustration bundles are one LZMA block of ~260 KB whose first ~4.6 KB is the serialized
/// file (container paths, Texture2D names); the rest is pixel data the catalog scan never reads.
/// Decoding stops once the bytes up to the end of the last serialized entry are produced, which
/// makes a catalog scan read a few KB per bundle instead of unpacking ~14 GB in total.
/// </para>
/// <para>
/// Not thread-safe: it reuses one LZMA decoder (its dictionary window is the large allocation)
/// and one scratch buffer. Use one instance per worker thread.
/// </para>
/// </summary>
public sealed class BundleSerializedFileReader
{
    private const int CompressionMask = 0x3F;
    private const int None = 0;
    private const int Lzma = 1;
    private const int Lz4 = 2;
    private const int Lz4Hc = 3;
    private const int LzmaPropertiesSize = 5;

    private readonly SevenZip.Compression.LZMA.Decoder _lzma = new();
    // Expandable: the LZMA SDK decoder may emit a few bytes past the requested length (a match
    // straddling the limit) and flushes its whole window, so a fixed-size target would throw.
    private readonly MemoryStream _lzmaOut = new(64 * 1024);

    /// <summary>
    /// Decodes every serialized entry of the bundle at <paramref name="bundlePath"/>.
    /// Returns an empty list when the bundle has no serialized entries.
    /// </summary>
    /// <exception cref="UnsupportedBundleCompressionException">A needed block uses LZHAM or an unknown codec.</exception>
    /// <exception cref="NotSupportedException">The file is not a UnityFS bundle.</exception>
    public IReadOnlyList<BundleSerializedFile> Read(string bundlePath)
    {
        using var stream = new FileStream(
            bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 16 * 1024);
        // Header + block/directory info only; the FileStream (disposed above) is the only resource.
        var bundle = new AssetBundleFile();
        bundle.Read(new AssetsFileReader(stream));
        return ReadSerializedEntries(bundle, stream);
    }

    private IReadOnlyList<BundleSerializedFile> ReadSerializedEntries(AssetBundleFile bundle, Stream stream)
    {
        var info = bundle.BlockAndDirInfo;
        long needed = 0;
        foreach (var dir in info.DirectoryInfos)
        {
            if (dir.IsSerialized)
                needed = Math.Max(needed, dir.Offset + dir.DecompressedSize);
        }

        if (needed == 0)
            return Array.Empty<BundleSerializedFile>();
        if (needed > int.MaxValue)
            throw new InvalidDataException($"Serialized data is too large ({needed} bytes).");

        var data = new byte[needed];
        var produced = 0;
        var blockStart = bundle.Header.GetFileDataOffset();
        foreach (var block in info.BlockInfos)
        {
            if (produced >= needed)
                break;

            var take = (int)Math.Min(block.DecompressedSize, needed - produced);
            stream.Position = blockStart;
            DecodeBlockPrefix(stream, block, data, produced, take);
            produced += take;
            blockStart += block.CompressedSize;
        }

        if (produced < needed)
            throw new InvalidDataException("Bundle blocks end before the serialized entries do.");

        var files = new List<BundleSerializedFile>(info.DirectoryInfos.Count);
        foreach (var dir in info.DirectoryInfos)
        {
            if (dir.IsSerialized)
                files.Add(new BundleSerializedFile(dir.Name, data, (int)dir.Offset, (int)dir.DecompressedSize));
        }

        return files;
    }

    private void DecodeBlockPrefix(Stream stream, AssetBundleBlockInfo block, byte[] target, int targetOffset, int take)
    {
        var compression = block.Flags & CompressionMask;
        switch (compression)
        {
            case None:
                stream.ReadExactly(target, targetOffset, take);
                break;

            case Lzma:
            {
                Span<byte> props = stackalloc byte[LzmaPropertiesSize];
                stream.ReadExactly(props);
                _lzma.SetDecoderProperties(props.ToArray());
                _lzmaOut.SetLength(0);
                _lzma.Code(stream, _lzmaOut, block.CompressedSize - LzmaPropertiesSize, take, null);
                if (_lzmaOut.Length < take)
                    throw new InvalidDataException("LZMA block ended early.");
                Buffer.BlockCopy(_lzmaOut.GetBuffer(), 0, target, targetOffset, take);
                break;
            }

            case Lz4:
            case Lz4Hc:
            {
                // LZ4 blocks are small (128 KiB in Unity bundles) and cannot stop mid-block,
                // so the whole block is decoded and only the needed prefix kept.
                var compressedSize = checked((int)block.CompressedSize);
                var decompressedSize = checked((int)block.DecompressedSize);
                var compressed = ArrayPool<byte>.Shared.Rent(compressedSize);
                var decompressed = ArrayPool<byte>.Shared.Rent(decompressedSize);
                try
                {
                    stream.ReadExactly(compressed, 0, compressedSize);
                    var written = LZ4ps.LZ4Codec.Decode64(
                        compressed, 0, compressedSize, decompressed, 0, decompressedSize, knownOutputLength: true);
                    if (written < take)
                        throw new InvalidDataException("LZ4 block ended early.");
                    Buffer.BlockCopy(decompressed, 0, target, targetOffset, take);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(compressed);
                    ArrayPool<byte>.Shared.Return(decompressed);
                }

                break;
            }

            default:
                throw new UnsupportedBundleCompressionException(compression);
        }
    }
}

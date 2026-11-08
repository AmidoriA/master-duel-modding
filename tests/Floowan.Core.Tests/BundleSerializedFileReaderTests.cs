using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Floowan.Core.Assets;

namespace Floowan.Core.Tests;

public class BundleSerializedFileReaderTests
{
    [Theory]
    [InlineData(AssetBundleCompressionType.None)]
    [InlineData(AssetBundleCompressionType.LZ4)]
    [InlineData(AssetBundleCompressionType.LZMA)]
    public void Read_ReturnsSerializedEntriesOnly(AssetBundleCompressionType compression)
    {
        // A serialized file larger than one 128 KiB LZ4 block, followed by a texture-sized
        // .resS payload the reader must not need.
        var serialized = Pattern(150_000, seed: 7);
        var resource = Pattern(262_144, seed: 3);
        var path = WriteBundle(compression, ("CAB-test", serialized, true), ("CAB-test.resS", resource, false));
        try
        {
            var files = new BundleSerializedFileReader().Read(path);

            var file = Assert.Single(files);
            Assert.Equal("CAB-test", file.Name);
            Assert.Equal(serialized.Length, file.Length);
            Assert.Equal(serialized, file.Data.AsSpan(file.Offset, file.Length).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ReusedReader_DecodesSuccessiveLzmaBundles()
    {
        // The LZMA decoder and scratch buffer are reused per worker thread.
        var reader = new BundleSerializedFileReader();
        for (var seed = 1; seed <= 3; seed++)
        {
            var serialized = Pattern(4_600 + seed, seed);
            var path = WriteBundle(
                AssetBundleCompressionType.LZMA,
                ("CAB-a", serialized, true),
                ("CAB-a.resS", Pattern(100_000, seed + 10), false));
            try
            {
                var file = Assert.Single(reader.Read(path));
                Assert.Equal(serialized, file.Data.AsSpan(file.Offset, file.Length).ToArray());
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Read_MultipleSerializedEntries_ReturnsEach()
    {
        var first = Pattern(3_000, seed: 1);
        var second = Pattern(5_000, seed: 2);
        var path = WriteBundle(
            AssetBundleCompressionType.LZ4,
            ("CAB-one", first, true),
            ("CAB-one.resS", Pattern(10_000, seed: 9), false),
            ("CAB-two", second, true));
        try
        {
            var files = new BundleSerializedFileReader().Read(path);

            Assert.Equal(new[] { "CAB-one", "CAB-two" }, files.Select(f => f.Name));
            Assert.Equal(first, files[0].Data.AsSpan(files[0].Offset, files[0].Length).ToArray());
            Assert.Equal(second, files[1].Data.AsSpan(files[1].Offset, files[1].Length).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_NonBundleFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"floowan-notabundle-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, Pattern(32 * 1024, seed: 5));
        try
        {
            Assert.ThrowsAny<Exception>(() => new BundleSerializedFileReader().Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_GameIllustrationBundle_MatchesFullUnpack()
    {
        if (GameSampleBundle.TryGetVanilla() is not { } sample)
            return; // Master Duel not installed

        var files = new BundleSerializedFileReader().Read(sample.Path);

        var am = new AssetsManager();
        try
        {
            var bundle = am.LoadBundleFile(sample.Path, unpackIfPacked: true);
            var dirs = bundle.file.BlockAndDirInfo.DirectoryInfos;
            var serializedDirs = dirs.Where(d => d.IsSerialized).ToList();
            Assert.Equal(serializedDirs.Select(d => d.Name), files.Select(f => f.Name));

            var reader = bundle.file.DataReader;
            for (var i = 0; i < files.Count; i++)
            {
                reader.Position = serializedDirs[i].Offset;
                var expected = reader.ReadBytes((int)serializedDirs[i].DecompressedSize);
                Assert.Equal(expected, files[i].Data.AsSpan(files[i].Offset, files[i].Length).ToArray());
            }
        }
        finally
        {
            am.UnloadAll();
        }
    }

    private static byte[] Pattern(int length, int seed)
    {
        // Compressible but not trivial, so LZ4/LZMA produce real matches and literals.
        var random = new Random(seed);
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(i % 97 < 60 ? (i * seed) % 251 : random.Next(256));
        return bytes;
    }

    private static string WriteBundle(
        AssetBundleCompressionType compression,
        params (string Name, byte[] Data, bool Serialized)[] entries)
    {
        var bundle = new AssetBundleFile
        {
            Header = new AssetBundleHeader
            {
                Signature = "UnityFS",
                Version = 7,
                GenerationVersion = "5.x.x",
                EngineVersion = "2020.3.41f1",
                FileStreamHeader = new AssetBundleFSHeader()
            },
            BlockAndDirInfo = new AssetBundleBlockAndDirInfo
            {
                BlockInfos = Array.Empty<AssetBundleBlockInfo>(),
                DirectoryInfos = new List<AssetBundleDirectoryInfo>()
            }
        };

        foreach (var (name, data, serialized) in entries)
        {
            var dir = AssetBundleDirectoryInfo.Create(name, serialized);
            dir.SetNewData(data);
            bundle.BlockAndDirInfo.DirectoryInfos.Add(dir);
        }

        var uncompressed = Path.Combine(Path.GetTempPath(), $"floowan-bundle-{Guid.NewGuid():N}");
        using (var writer = new AssetsFileWriter(uncompressed))
            bundle.Write(writer);

        if (compression == AssetBundleCompressionType.None)
            return uncompressed;

        var packed = uncompressed + "." + compression;
        try
        {
            var reread = new AssetBundleFile();
            using (var stream = File.OpenRead(uncompressed))
            {
                reread.Read(new AssetsFileReader(stream));
                using var writer = new AssetsFileWriter(packed);
                reread.Pack(writer, compression);
            }
        }
        finally
        {
            File.Delete(uncompressed);
        }

        return packed;
    }
}

using System.IO.Compression;

namespace Floowan.Core.Data;

/// <summary>
/// Decrypts Master Duel CARD_* TextAsset payloads (XOR + zlib), matching the
/// Floowandereeze ETL / akintos algorithm.
/// </summary>
public static class CardDataCrypto
{
    /// <summary>Default search ceiling when brute-forcing the crypto key.</summary>
    public const int DefaultMaxKey = 0x400;

    /// <summary>
    /// XOR obfuscation used before zlib compression on CARD_Name / CARD_Desc / CARD_Indx / CARD_Prop.
    /// </summary>
    public static void XorInPlace(byte[] data, int cryptoKey)
    {
        ArgumentNullException.ThrowIfNull(data);
        for (var i = 0; i < data.Length; i++)
        {
            var v = i + cryptoKey + 0x23D;
            v *= cryptoKey;
            v ^= i % 7;
            data[i] ^= (byte)(v & 0xFF);
        }
    }

    public static byte[]? TryDecrypt(byte[] encrypted, int cryptoKey, int minPlainLength = 1)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        if (encrypted.Length == 0)
            return null;

        var copy = (byte[])encrypted.Clone();
        XorInPlace(copy, cryptoKey);
        var plain = TryZlibDecompress(copy);
        if (plain is null || plain.Length < minPlainLength)
            return null;
        return plain;
    }

    public static byte[] Decrypt(byte[] encrypted, int cryptoKey)
    {
        var result = TryDecrypt(encrypted, cryptoKey);
        if (result is null || result.Length == 0)
            throw new InvalidOperationException($"CARD_* decrypt failed for crypto key 0x{cryptoKey:X}.");
        return result;
    }

    /// <summary>
    /// Finds a crypto key that zlib-decompresses <paramref name="encrypted"/> after XOR.
    /// Prefers <paramref name="preferredKey"/> when supplied and valid.
    /// </summary>
    public static int FindCryptoKey(
        byte[] encrypted,
        int? preferredKey = null,
        int maxKey = DefaultMaxKey,
        int minPlainLength = 64)
    {
        ArgumentNullException.ThrowIfNull(encrypted);

        if (preferredKey is int preferred && TryDecrypt(encrypted, preferred, minPlainLength) is not null)
            return preferred;

        for (var key = 0; key <= maxKey; key++)
        {
            if (TryDecrypt(encrypted, key, minPlainLength) is not null)
                return key;
        }

        throw new InvalidOperationException(
            $"Could not find a CARD_* crypto key that decompresses the payload (tried 0..0x{maxKey:X}).");
    }

    private static byte[]? TryZlibDecompress(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

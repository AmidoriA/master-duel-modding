using System.Security.Cryptography;
using System.Text;

namespace Floowan.Core.Cutins;

/// <summary>
/// Master Duel asset naming for Monster Cut-ins (see docs/summon-animation-research.md and
/// scripts/cutin/monster_cutin.py, which this mirrors so both tools agree on every path).
/// </summary>
public static class CutinNaming
{
    public const string TablePath = "Duel/ScriptableObject/CardIndividualData";
    public const string ManifestName = "manifest_tcg";
    public static readonly string[] ResourceTypes = ["SD", "HighEnd_HD"];

    /// <summary>Default template: a single-page cut-in whose 16 bundles are cloned for custom cut-ins.</summary>
    public const int DefaultTemplate = 19172;

    public static string CutinDir(int cardId) => $"Duel/Timeline/Duel/MonsterCutIn/tcg/P{cardId}/";

    public static string CutinRoot(int cardId, string resourceType) =>
        $"Duel/Timeline/Duel/MonsterCutIn/tcg/P{cardId}/{resourceType}/P{cardId}";

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>LocalFile.HashFileName: CRC-32 of the UTF-8 logical path (without "#/"), as 8 lowercase hex digits.</summary>
    public static string HashName(string logicalPath)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in Encoding.UTF8.GetBytes(logicalPath.Replace("#/", "")))
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return (c ^ 0xFFFFFFFFu).ToString("x8");
    }

    /// <summary>LocalData/&lt;player&gt;/0000/&lt;xx&gt;/&lt;hash&gt; for a logical bundle path.</summary>
    public static string BundleFile(string playerDir, string logicalPath)
    {
        var h = HashName(logicalPath);
        return Path.Combine(playerDir, "0000", h[..2], h);
    }

    /// <summary>The AssetBundle name the game uses for a bundle file: "xx/xxxxxxxx".</summary>
    public static string BundleName(string hash) => $"{hash[..2]}/{hash}";

    /// <summary>CAB name for a bundle we create: CAB-md5("xx/xxxxxxxx").</summary>
    public static string CabName(string bundleName) =>
        "CAB-" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(bundleName))).ToLowerInvariant();

    /// <summary>
    /// The manifest cipher (YgomSystem.SimpleCrypto): XOR with a .NET System.Random keystream seeded with
    /// the sum of the key's UTF-8 bytes ("manifest" = 855), except that inextp starts at 31. Symmetric.
    /// </summary>
    public static byte[] ManifestCrypt(ReadOnlySpan<byte> data, string key = "manifest")
    {
        const int mbig = int.MaxValue;
        const int mseed = 161803398;
        var seed = 0;
        foreach (var b in Encoding.UTF8.GetBytes(key))
            seed += b;
        var sa = new int[56];
        var mj = mseed - Math.Abs(seed);
        var mk = 1;
        sa[55] = mj;
        for (var i = 1; i < 55; i++)
        {
            var ii = 21 * i % 55;
            sa[ii] = mk;
            mk = mj - mk;
            if (mk < 0)
                mk += mbig;
            mj = sa[ii];
        }
        for (var r = 1; r < 5; r++)
        {
            for (var i = 1; i < 56; i++)
            {
                sa[i] -= sa[1 + (i + 30) % 55];
                if (sa[i] < 0)
                    sa[i] += mbig;
            }
        }

        int inext = 0, inextp = 31;
        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            if (++inext >= 56)
                inext = 1;
            if (++inextp >= 56)
                inextp = 1;
            var v = sa[inext] - sa[inextp];
            if (v < 0)
                v += mbig;
            sa[inext] = v;
            output[i] = (byte)(data[i] ^ ((int)(v * (1.0 / mbig) * 256.0) & 0xFF));
        }
        return output;
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Bytes(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

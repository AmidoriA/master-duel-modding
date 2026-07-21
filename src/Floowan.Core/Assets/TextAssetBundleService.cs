using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Floowan.Core.Assets;

/// <summary>
/// Reads and writes named TextAsset payloads inside Master Duel AssetBundles,
/// then LZ4-repacks (same write path as card-art replacement).
/// </summary>
public sealed class TextAssetBundleService : IDisposable
{
    public const string OfCardAssetName = "of_card_asset";

    private readonly string _classDataPath;

    public TextAssetBundleService(string? classDataPath = null)
    {
        _classDataPath = ClassDataLocator.FindClassDataPath(classDataPath);
    }

    public bool TryFindTextAsset(string bundlePath, string assetName, out byte[]? payload)
    {
        payload = null;
        try
        {
            payload = ReadTextAssetBytes(bundlePath, assetName);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public byte[] ReadTextAssetBytes(string bundlePath, string assetName = OfCardAssetName)
    {
        using var session = Open(bundlePath, assetName);
        return GetScriptBytes(session.BaseField);
    }

    public void WriteTextAssetBytes(
        string bundlePath,
        byte[] payload,
        string assetName = OfCardAssetName,
        string compression = "lz4")
    {
        if (!File.Exists(bundlePath))
            throw new FileNotFoundException("Bundle not found.", bundlePath);
        ArgumentNullException.ThrowIfNull(payload);

        var am = new AssetsManager();
        string? tempUncompressed = null;
        string? tempPacked = null;

        try
        {
            am.LoadClassPackage(_classDataPath);
            var bundleInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
            var assetsInst = am.LoadAssetsFileFromBundle(bundleInst, 0, false);
            am.LoadClassDatabaseFromPackage(assetsInst.file.Metadata.UnityVersion);

            var (texInfo, baseField) = FindTextAsset(am, assetsInst, assetName);
            SetScriptBytes(baseField, payload);
            texInfo.SetNewData(baseField);
            bundleInst.file.BlockAndDirInfo.DirectoryInfos[0].SetNewData(assetsInst.file);

            tempUncompressed = Path.Combine(Path.GetTempPath(), $"floowan-text-{Guid.NewGuid():N}.bundle");
            tempPacked = Path.Combine(Path.GetTempPath(), $"floowan-text-{Guid.NewGuid():N}.lz4");

            using (var writer = new AssetsFileWriter(tempUncompressed))
                bundleInst.file.Write(writer);

            am.UnloadAll();
            am = new AssetsManager();

            var packInst = am.LoadBundleFile(tempUncompressed);
            var packType = string.Equals(compression, "none", StringComparison.OrdinalIgnoreCase)
                ? AssetBundleCompressionType.None
                : AssetBundleCompressionType.LZ4;

            if (packType == AssetBundleCompressionType.None)
            {
                File.Copy(tempUncompressed, bundlePath, overwrite: true);
            }
            else
            {
                using (var packWriter = new AssetsFileWriter(tempPacked))
                    packInst.file.Pack(packWriter, packType);
                File.Copy(tempPacked, bundlePath, overwrite: true);
            }
        }
        finally
        {
            am.UnloadAll();
            try { if (tempUncompressed is not null && File.Exists(tempUncompressed)) File.Delete(tempUncompressed); } catch { /* ignore */ }
            try { if (tempPacked is not null && File.Exists(tempPacked)) File.Delete(tempPacked); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Quick check: does this bundle contain a TextAsset with the given name?
    /// </summary>
    public bool BundleContainsNamedTextAsset(string bundlePath, string assetName = OfCardAssetName)
    {
        if (!File.Exists(bundlePath))
            return false;

        long length;
        try { length = new FileInfo(bundlePath).Length; }
        catch { return false; }

        // Fast reject for large bundles when the asset name isn't present as plaintext.
        // Tiny bundles are always opened (LZ4 may hide the name string).
        const long smallBundleBytes = 512 * 1024;
        if (length > smallBundleBytes)
        {
            try
            {
                if (!FileContainsAscii(bundlePath, assetName))
                    return false;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            using var session = Open(bundlePath, assetName);
            return session.BaseField is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool FileContainsAscii(string path, string ascii)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(ascii);
        const int chunkSize = 1024 * 1024;
        var buffer = new byte[chunkSize + needle.Length];
        using var stream = File.OpenRead(path);
        var carry = 0;
        while (true)
        {
            var read = stream.Read(buffer, carry, chunkSize);
            if (read <= 0)
                break;
            var spanLen = carry + read;
            if (IndexOf(buffer, spanLen, needle) >= 0)
                return true;
            carry = Math.Min(needle.Length - 1, spanLen);
            if (carry > 0)
                Buffer.BlockCopy(buffer, spanLen - carry, buffer, 0, carry);
        }

        return false;
    }

    private TextAssetSession Open(string bundlePath, string assetName)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(_classDataPath);
        var bundleInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
        var assetsInst = am.LoadAssetsFileFromBundle(bundleInst, 0, false);
        am.LoadClassDatabaseFromPackage(assetsInst.file.Metadata.UnityVersion);
        var (_, baseField) = FindTextAsset(am, assetsInst, assetName);
        return new TextAssetSession(am, baseField);
    }

    private static (AssetFileInfo Info, AssetTypeValueField BaseField) FindTextAsset(
        AssetsManager am,
        AssetsFileInstance assetsInst,
        string assetName)
    {
        var infos = assetsInst.file.GetAssetsOfType(AssetClassID.TextAsset);
        foreach (var info in infos)
        {
            var baseField = am.GetBaseField(assetsInst, info);
            var name = baseField["m_Name"].AsString;
            if (string.Equals(name, assetName, StringComparison.Ordinal))
                return (info, baseField);
        }

        throw new InvalidOperationException($"TextAsset '{assetName}' not found in bundle.");
    }

    private static byte[] GetScriptBytes(AssetTypeValueField baseField)
    {
        var script = baseField["m_Script"];
        if (script.IsDummy)
            throw new InvalidOperationException("TextAsset has no m_Script field.");

        // AssetsTools may expose byte array directly or via Array children.
        try
        {
            var arr = script.AsByteArray;
            if (arr is { Length: > 0 } || arr is { Length: 0 })
                return arr;
        }
        catch
        {
            // fall through
        }

        var data = script["Array"];
        if (!data.IsDummy)
        {
            try
            {
                return data.AsByteArray;
            }
            catch
            {
                // fall through
            }
        }

        throw new InvalidOperationException("Could not read TextAsset m_Script bytes.");
    }

    private static void SetScriptBytes(AssetTypeValueField baseField, byte[] payload)
    {
        var script = baseField["m_Script"];
        if (script.IsDummy)
            throw new InvalidOperationException("TextAsset has no m_Script field.");

        try
        {
            script.AsByteArray = payload;
            return;
        }
        catch
        {
            // try Array child
        }

        var data = script["Array"];
        if (!data.IsDummy)
        {
            data.AsByteArray = payload;
            return;
        }

        throw new InvalidOperationException("Could not write TextAsset m_Script bytes.");
    }

    private static int IndexOf(byte[] haystack, int haystackLength, byte[] needle)
    {
        if (needle.Length == 0 || haystackLength < needle.Length)
            return -1;
        var limit = haystackLength - needle.Length;
        for (var i = 0; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }

    private static int IndexOf(byte[] haystack, byte[] needle) =>
        IndexOf(haystack, haystack.Length, needle);

    public void Dispose()
    {
        // no shared manager state
    }

    private sealed class TextAssetSession : IDisposable
    {
        private readonly AssetsManager _am;
        public AssetTypeValueField BaseField { get; }

        public TextAssetSession(AssetsManager am, AssetTypeValueField baseField)
        {
            _am = am;
            BaseField = baseField;
        }

        public void Dispose() => _am.UnloadAll();
    }
}

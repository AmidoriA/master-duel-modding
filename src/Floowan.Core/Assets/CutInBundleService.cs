using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Floowan.Core.Assets;
using Floowan.Core.Imaging;

namespace Floowan.Core.Spine;

/// <summary>
/// Replaces cut-in Texture2D + atlas/skeleton TextAssets inside Master Duel bundles.
/// </summary>
public sealed class CutInBundleService : IDisposable
{
    private readonly string _classDataPath;
    private readonly TextAssetBundleService _textAssets;

    public CutInBundleService(string? classDataPath = null)
    {
        _classDataPath = ClassDataLocator.FindClassDataPath(classDataPath);
        _textAssets = new TextAssetBundleService(_classDataPath);
    }

    public void Inject(CutInAssetSet targets, SpineCutInAssets assets, string compression = "lz4")
    {
        if (!targets.IsComplete)
            throw new InvalidOperationException(
                $"Cut-in P{targets.CutInId} is incomplete (need texture + atlas + skeleton).");

        ReplaceNamedTexture(
            targets.Texture!.BundlePath,
            targets.Texture.AssetName,
            assets.TexturePngPath,
            assets.Width,
            assets.Height,
            compression);

        var atlasBytes = Encoding.UTF8.GetBytes(assets.AtlasText);
        _textAssets.WriteTextAssetBytes(
            targets.Atlas!.BundlePath,
            atlasBytes,
            targets.Atlas.AssetName,
            compression);

        var jsonBytes = Encoding.UTF8.GetBytes(assets.SkeletonJson);
        _textAssets.WriteTextAssetBytes(
            targets.Skeleton!.BundlePath,
            jsonBytes,
            targets.Skeleton.AssetName,
            compression);
    }

    public void ReplaceNamedTexture(
        string bundlePath,
        string textureName,
        string replacementImagePath,
        int width,
        int height,
        string compression = "lz4")
    {
        if (!File.Exists(bundlePath))
            throw new FileNotFoundException("Bundle not found.", bundlePath);
        if (!File.Exists(replacementImagePath))
            throw new FileNotFoundException("Replacement image not found.", replacementImagePath);

        var am = new AssetsManager();
        string? tempUncompressed = null;
        string? tempPacked = null;

        try
        {
            am.LoadClassPackage(_classDataPath);
            var bundleInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
            var (assetsInst, texInfo, baseField) = FindTextureInBundle(am, bundleInst, textureName);
            var texture = TextureFile.ReadTextureFile(baseField);

            var rgba = ImagePreparation.PrepareRgba32TextureBytes(replacementImagePath, width, height);

            texture.m_TextureFormat = (int)TextureFormat.RGBA32;
            texture.m_Width = width;
            texture.m_Height = height;
            texture.m_MipCount = 1;
            texture.SetPictureData(rgba, width, height);
            texture.WriteTo(baseField);

            baseField["m_TextureFormat"].AsInt = (int)TextureFormat.RGBA32;
            if (!baseField["m_MipCount"].IsDummy)
                baseField["m_MipCount"].AsInt = 1;
            if (!baseField["m_CompleteImageSize"].IsDummy)
                baseField["m_CompleteImageSize"].AsInt = rgba.Length;
            if (!baseField["m_StreamData"].IsDummy)
            {
                baseField["m_StreamData"]["offset"].AsULong = 0;
                baseField["m_StreamData"]["size"].AsUInt = 0;
                baseField["m_StreamData"]["path"].AsString = "";
            }

            texInfo.SetNewData(baseField);
            var dirIndex = FindDirectoryIndex(bundleInst, assetsInst);
            bundleInst.file.BlockAndDirInfo.DirectoryInfos[dirIndex].SetNewData(assetsInst.file);

            tempUncompressed = Path.Combine(Path.GetTempPath(), $"floowan-cutin-{Guid.NewGuid():N}.bundle");
            tempPacked = Path.Combine(Path.GetTempPath(), $"floowan-cutin-{Guid.NewGuid():N}.lz4");

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

    private static (AssetsFileInstance Assets, AssetFileInfo Info, AssetTypeValueField BaseField)
        FindTextureInBundle(AssetsManager am, BundleFileInstance bundleInst, string textureName)
    {
        foreach (var entryName in bundleInst.file.GetAllFileNames())
        {
            AssetsFileInstance assetsInst;
            try { assetsInst = am.LoadAssetsFileFromBundle(bundleInst, entryName, false); }
            catch { continue; }

            am.LoadClassDatabaseFromPackage(assetsInst.file.Metadata.UnityVersion);
            foreach (var info in assetsInst.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                var baseField = am.GetBaseField(assetsInst, info);
                var name = baseField["m_Name"].AsString;
                if (string.Equals(name, textureName, StringComparison.OrdinalIgnoreCase))
                    return (assetsInst, info, baseField);
            }
        }

        throw new InvalidOperationException($"Texture2D '{textureName}' not found in bundle.");
    }

    private static int FindDirectoryIndex(BundleFileInstance bundleInst, AssetsFileInstance assetsInst)
    {
        var dirs = bundleInst.file.BlockAndDirInfo.DirectoryInfos;
        for (var i = 0; i < dirs.Count; i++)
        {
            if (string.Equals(dirs[i].Name, assetsInst.name, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    public void Dispose()
    {
        _textAssets.Dispose();
    }
}

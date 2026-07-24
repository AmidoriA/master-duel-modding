using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Floowan.Core.Imaging;

namespace Floowan.Core.Assets;

/// <summary>
/// Reads and replaces Texture2D card art inside Master Duel AssetBundles
/// using AssetsTools.NET (same family of tools as UABEA).
/// Replacement strategy mirrors Floowandereeze: encode as RGBA32, clear m_StreamData, rewrite bundle, LZ4 pack.
/// </summary>
public sealed class CardArtBundleService : IDisposable
{
    private readonly string _classDataPath;
    private AssetsManager? _sharedPreviewManager;

    public CardArtBundleService(string? classDataPath = null)
    {
        _classDataPath = ClassDataLocator.FindClassDataPath(classDataPath);
    }

    public TextureInfo ReadTextureInfo(string bundlePath)
    {
        using var session = Open(bundlePath);
        var tex = session.Texture;
        return new TextureInfo(tex.m_Name, tex.m_Width, tex.m_Height, tex.m_TextureFormat, tex.m_MipCount);
    }

    public void ExtractTexturePng(string bundlePath, string outputPngPath)
    {
        using var session = Open(bundlePath);
        session.Texture.FillPictureData(session.Assets);
        var decoded = session.Texture.DecodeTextureRaw(session.Texture.pictureData)
                      ?? throw new InvalidOperationException("Failed to decode Texture2D pixels.");
        // Pendulum live canvas is often 512×1024; Card Art export resizes full canvas to 512×683.
        ImagePreparation.SaveCardArtExportPng(
            decoded, session.Texture.m_Width, session.Texture.m_Height, outputPngPath, inputIsBgra: true);
    }

    public void ReplaceTexture(
        string bundlePath,
        string replacementImagePath,
        string compression = "lz4",
        int? overrideWidth = null,
        int? overrideHeight = null)
    {
        ReplaceTexture(bundlePath, replacementImagePath, new TextureReplaceOptions
        {
            Compression = compression,
            Width = overrideWidth,
            Height = overrideHeight
        });
    }

    public void ReplaceTexture(string bundlePath, string replacementImagePath, TextureReplaceOptions options)
    {
        if (!File.Exists(bundlePath))
            throw new FileNotFoundException("Bundle not found.", bundlePath);
        if (!File.Exists(replacementImagePath))
            throw new FileNotFoundException("Replacement image not found.", replacementImagePath);

        var compression = options.Compression ?? "lz4";
        var am = new AssetsManager();
        BundleFileInstance? bundleInst = null;
        AssetsFileInstance? assetsInst = null;
        string? tempUncompressed = null;
        string? tempPacked = null;

        try
        {
            am.LoadClassPackage(_classDataPath);
            bundleInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
            assetsInst = am.LoadAssetsFileFromBundle(bundleInst, 0, false);
            am.LoadClassDatabaseFromPackage(assetsInst.file.Metadata.UnityVersion);

            var texInfos = assetsInst.file.GetAssetsOfType(AssetClassID.Texture2D);
            if (texInfos.Count == 0)
                throw new InvalidOperationException("No Texture2D asset found in bundle.");

            var texInfo = texInfos[0];
            var baseField = am.GetBaseField(assetsInst, texInfo);
            var texture = TextureFile.ReadTextureFile(baseField);

            // Prefer live Texture2D dimensions so Pendulum (native canvas often
            // 512×1024; art aspect 3:4) and normal (512×512) keep their canvas.
            // Override only for OF.
            var targetWidth = options.Width
                ?? (texture.m_Width > 0 ? texture.m_Width : CardArtTextureSizes.NormalWidth);
            var targetHeight = options.Height
                ?? (texture.m_Height > 0 ? texture.m_Height : CardArtTextureSizes.NormalHeight);

            // Card-art path (no size override): letterbox on aspect mismatch, except
            // 3:4 Pendulum art onto the tall canvas which stretches (reverse of extract).
            // OF overrides keep Stretch.
            var preserveAspect = options.Width is null && options.Height is null;
            var rgba = ImagePreparation.PrepareRgba32TextureBytes(
                replacementImagePath, targetWidth, targetHeight, preserveAspect);

            texture.m_TextureFormat = (int)TextureFormat.RGBA32;
            texture.m_Width = targetWidth;
            texture.m_Height = targetHeight;
            texture.m_MipCount = 1;
            texture.SetPictureData(rgba, targetWidth, targetHeight);
            texture.WriteTo(baseField);

            // Explicitly clear streaming info and pin format (Master Duel originals often use BC7 + .resS).
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
            bundleInst.file.BlockAndDirInfo.DirectoryInfos[0].SetNewData(assetsInst.file);

            tempUncompressed = Path.Combine(Path.GetTempPath(), $"floowan-{Guid.NewGuid():N}.bundle");
            tempPacked = Path.Combine(Path.GetTempPath(), $"floowan-{Guid.NewGuid():N}.lz4");

            using (var writer = new AssetsFileWriter(tempUncompressed))
                bundleInst.file.Write(writer);

            // Close readers before overwriting the live game file.
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

    private BundleSession Open(string bundlePath)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(_classDataPath);
        var bundleInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
        var assetsInst = am.LoadAssetsFileFromBundle(bundleInst, 0, false);
        am.LoadClassDatabaseFromPackage(assetsInst.file.Metadata.UnityVersion);
        var texInfos = assetsInst.file.GetAssetsOfType(AssetClassID.Texture2D);
        if (texInfos.Count == 0)
        {
            am.UnloadAll();
            throw new InvalidOperationException("No Texture2D asset found in bundle.");
        }

        var texInfo = texInfos[0];
        var baseField = am.GetBaseField(assetsInst, texInfo);
        var texture = TextureFile.ReadTextureFile(baseField);
        return new BundleSession(am, bundleInst, assetsInst, texture);
    }

    public void Dispose()
    {
        _sharedPreviewManager?.UnloadAll();
        _sharedPreviewManager = null;
    }

    private sealed class BundleSession : IDisposable
    {
        private readonly AssetsManager _am;
        public AssetsFileInstance Assets { get; }
        public TextureFile Texture { get; }

        public BundleSession(AssetsManager am, BundleFileInstance bundle, AssetsFileInstance assets, TextureFile texture)
        {
            _am = am;
            Assets = assets;
            Texture = texture;
            _ = bundle;
        }

        public void Dispose()
        {
            _am.UnloadAll();
        }
    }
}

public readonly record struct TextureInfo(string Name, int Width, int Height, int Format, int MipCount);

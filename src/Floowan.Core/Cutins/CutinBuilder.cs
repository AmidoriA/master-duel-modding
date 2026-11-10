using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace Floowan.Core.Cutins;

/// <summary>A bundle to write: game file path and its LZ4-packed bytes.</summary>
public sealed record BuiltBundle(string Path, byte[] Data);

/// <summary>
/// Builds a cut-in's bundles (C# port of build_custom / build_borrow in scripts/cutin/monster_cutin.py;
/// the output was checked object for object against the Python tool's).
/// </summary>
public sealed class CutinBuilder
{
    private readonly string _classDataPath;
    private readonly ICutinTextureEncoder _encoder;

    public CutinBuilder(string classDataPath, ICutinTextureEncoder encoder)
    {
        _classDataPath = classDataPath;
        _encoder = encoder;
    }

    /// <summary>Manifest entries a build reads: every template bundle (custom) or the donor's two root prefabs (borrow).</summary>
    public static List<JsonObject> SourceEntries(CutinEntry entry, JsonArray baseInformations)
    {
        if (entry.IsCustom)
        {
            var prefix = CutinNaming.CutinDir(entry.Template ?? CutinNaming.DefaultTemplate);
            return baseInformations.Select(n => n!.AsObject())
                .Where(e => e["assetName"]!.GetValue<string>().StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }
        var names = CutinNaming.ResourceTypes.Select(rt => CutinNaming.CutinRoot(entry.Source!.Value, rt)).ToHashSet();
        return baseInformations.Select(n => n!.AsObject())
            .Where(e => names.Contains(e["assetName"]!.GetValue<string>())).ToList();
    }

    /// <summary>The manifest entries for a cut-in's own bundles.</summary>
    public static List<JsonObject> OwnEntries(CutinEntry entry, JsonArray baseInformations)
    {
        var dst = entry.CardId;
        if (entry.IsCustom)
        {
            string sTok = $"P{entry.Template ?? CutinNaming.DefaultTemplate}", dTok = $"P{dst}";
            return SourceEntries(entry, baseInformations).Select(e => new JsonObject
            {
                ["assetName"] = e["assetName"]!.GetValue<string>().Replace(sTok, dTok),
                ["dependencies"] = new JsonArray(e["dependencies"]!.AsArray().Select(d => (JsonNode?)d!.GetValue<string>().Replace(sTok, dTok)).ToArray()),
            }).ToList();
        }
        var byName = baseInformations.Select(n => n!.AsObject()).ToDictionary(e => e["assetName"]!.GetValue<string>());
        return CutinNaming.ResourceTypes.Select(rt =>
        {
            var src = CutinNaming.CutinRoot(entry.Source!.Value, rt);
            if (!byName.TryGetValue(src, out var se))
                throw new InvalidOperationException($"Card {entry.Source} has no cut-in ({src} is not in the manifest).");
            return new JsonObject
            {
                ["assetName"] = CutinNaming.CutinRoot(dst, rt),
                ["dependencies"] = se["dependencies"]!.DeepClone(),
            };
        }).ToList();
    }

    /// <summary>Clone the template's bundles as P{dst} with the content folder's Spine data and pages.</summary>
    public List<BuiltBundle> BuildCustom(string playerDir, CutinEntry entry, string contentDir, JsonArray baseInformations,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var tpl = entry.Template ?? CutinNaming.DefaultTemplate;
        var dst = entry.CardId;
        var prefix = CutinNaming.CutinDir(tpl);
        var tplEntries = SourceEntries(entry, baseInformations);
        if (tplEntries.Count == 0)
            throw new InvalidOperationException($"Template P{tpl} is not in the manifest (did a game update remove it?).");
        string sTok = $"P{tpl}", dTok = $"P{dst}";
        var skelText = File.ReadAllText(Path.Combine(contentDir, "skeleton.json"));
        var duration = AnimDuration(JsonDocument.Parse(skelText).RootElement.GetProperty("animations").GetProperty("animation"));
        var atlas = new Dictionary<bool, string>
        {
            [true] = File.ReadAllText(Path.Combine(contentDir, "hd.atlas")),
            [false] = File.ReadAllText(Path.Combine(contentDir, "sd.atlas")),
        };
        var textures = new Dictionary<bool, EncodedTexture>();

        var info = new List<(string Old, string New, string Oh, string Nh, string OldCab, string NewCab)>();
        foreach (var e in tplEntries)
        {
            var old = e["assetName"]!.GetValue<string>();
            var nw = old.Replace(sTok, dTok);
            string oh = CutinNaming.HashName(old), nh = CutinNaming.HashName(nw);
            var am0 = new AssetsManager();
            try
            {
                var b0 = am0.LoadBundleFile(CutinNaming.BundleFile(playerDir, old), unpackIfPacked: false);
                var cabs = b0.file.BlockAndDirInfo.DirectoryInfos.Select(d => d.Name).Where(n => !n.EndsWith(".resS", StringComparison.Ordinal)).ToList();
                if (cabs.Count != 1)
                    throw new InvalidOperationException($"Unexpected bundle layout in {old}.");
                info.Add((old, nw, oh, nh, cabs[0], CutinNaming.CabName(CutinNaming.BundleName(nh))));
            }
            finally
            {
                am0.UnloadAll();
            }
        }
        var cabMap = info.ToDictionary(v => v.OldCab, v => v.NewCab);
        var nameMap = info.ToDictionary(v => CutinNaming.BundleName(v.Oh), v => CutinNaming.BundleName(v.Nh));

        var result = new List<BuiltBundle>();
        foreach (var v in info)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = v.Old[prefix.Length..];
            var hd = rel.StartsWith("HighEnd_HD/", StringComparison.Ordinal);
            var am = new AssetsManager();
            try
            {
                am.LoadClassPackage(_classDataPath);
                var bun = am.LoadBundleFile(CutinNaming.BundleFile(playerDir, v.Old), unpackIfPacked: true);
                var dirs = bun.file.BlockAndDirInfo.DirectoryInfos;
                var cabIdx = dirs.FindIndex(d => d.Name == v.OldCab);
                var afi = am.LoadAssetsFileFromBundle(bun, cabIdx, false);
                am.LoadClassDatabaseFromPackage(afi.file.Metadata.UnityVersion);
                foreach (var ext in afi.file.Metadata.Externals)
                    foreach (var (oc, nc) in cabMap)
                        if (ext.PathName.Contains(oc, StringComparison.Ordinal))
                            ext.PathName = ext.PathName.Replace(oc, nc);

                var dropRess = false;
                foreach (var ai in afi.file.AssetInfos)
                {
                    var type = (AssetClassID)ai.TypeId;
                    if (type == AssetClassID.AssetBundle)
                    {
                        var bf = am.GetBaseField(afi, ai);
                        var newName = nameMap[bf["m_AssetBundleName"].AsString];
                        bf["m_Name"].AsString = newName;
                        bf["m_AssetBundleName"].AsString = newName;
                        foreach (var d in bf["m_Dependencies"]["Array"].Children)
                            if (nameMap.TryGetValue(d.AsString, out var nd))
                                d.AsString = nd;
                        foreach (var c in bf["m_Container"]["Array"].Children)
                            c["first"].AsString = c["first"].AsString.Replace(sTok.ToLowerInvariant(), dTok.ToLowerInvariant());
                        ai.SetNewData(bf);
                    }
                    else if (type == AssetClassID.TextAsset && (rel.EndsWith(sTok + "JS", StringComparison.Ordinal) || rel.EndsWith(sTok + ".atlas", StringComparison.Ordinal)))
                    {
                        var bf = am.GetBaseField(afi, ai);
                        bf["m_Script"].AsByteArray = Encoding.UTF8.GetBytes(rel.EndsWith("JS", StringComparison.Ordinal) ? skelText : atlas[hd]);
                        ai.SetNewData(bf);
                    }
                    else if (type == AssetClassID.Texture2D)
                    {
                        if (!textures.TryGetValue(hd, out var t))
                        {
                            progress?.Report($"Encoding {(hd ? "HD" : "SD")} texture (BC7)…");
                            t = _encoder.EncodeBc7(Path.Combine(contentDir, hd ? "hd.png" : "sd.png"), cancellationToken);
                            textures[hd] = t;
                        }
                        var bf = am.GetBaseField(afi, ai);
                        var tf = TextureFile.ReadTextureFile(bf);
                        tf.m_TextureFormat = (int)TextureFormat.BC7;
                        tf.m_Width = t.Width;
                        tf.m_Height = t.Height;
                        tf.m_MipCount = 1;
                        tf.SetPictureData(t.Data, t.Width, t.Height);
                        tf.WriteTo(bf);
                        bf["m_TextureFormat"].AsInt = (int)TextureFormat.BC7;
                        if (!bf["m_MipCount"].IsDummy)
                            bf["m_MipCount"].AsInt = 1;
                        if (!bf["m_CompleteImageSize"].IsDummy)
                            bf["m_CompleteImageSize"].AsInt = t.Data.Length;
                        if (!bf["m_StreamData"].IsDummy)
                        {
                            bf["m_StreamData"]["offset"].AsULong = 0;
                            bf["m_StreamData"]["size"].AsUInt = 0;
                            bf["m_StreamData"]["path"].AsString = "";
                        }
                        ai.SetNewData(bf);
                        dropRess = true;
                    }
                    else if (type == AssetClassID.MonoBehaviour && rel == sTok + "Timeline")
                    {
                        var bf = am.GetBaseField(afi, ai);
                        var clips = bf["m_Clips"];
                        if (!clips.IsDummy && clips["Array"].Children.Count > 0)
                        {
                            foreach (var c in clips["Array"].Children)
                                c["m_Duration"].AsDouble = duration;
                            ai.SetNewData(bf);
                        }
                    }
                }
                dirs[cabIdx].SetNewData(afi.file);
                dirs[cabIdx].Name = v.NewCab;
                foreach (var d in dirs.Where(d => d.Name.EndsWith(".resS", StringComparison.Ordinal)))
                {
                    if (dropRess)
                        d.SetRemoved();
                    else
                        d.Name = d.Name.Replace(v.OldCab, v.NewCab);
                }
                result.Add(new BuiltBundle(CutinNaming.BundleFile(playerDir, v.New), BundleIo.SavePacked(bun)));
            }
            finally
            {
                am.UnloadAll();
            }
        }
        return result;
    }

    /// <summary>Clone the donor's two root prefab bundles as P{dst}; dependencies stay on the donor's shared bundles.</summary>
    public List<BuiltBundle> BuildBorrow(string playerDir, CutinEntry entry, JsonArray baseInformations)
    {
        int src = entry.Source!.Value, dst = entry.CardId;
        var names = baseInformations.Select(n => n!["assetName"]!.GetValue<string>()).ToHashSet();
        var result = new List<BuiltBundle>();
        foreach (var rt in CutinNaming.ResourceTypes)
        {
            string srcPath = CutinNaming.CutinRoot(src, rt), dstPath = CutinNaming.CutinRoot(dst, rt);
            if (!names.Contains(srcPath))
                throw new InvalidOperationException($"Card {src} has no cut-in ({srcPath} is not in the manifest).");
            var newName = CutinNaming.BundleName(CutinNaming.HashName(dstPath));
            var am = new AssetsManager();
            try
            {
                am.LoadClassPackage(_classDataPath);
                var bun = am.LoadBundleFile(CutinNaming.BundleFile(playerDir, srcPath), unpackIfPacked: true);
                var dirs = bun.file.BlockAndDirInfo.DirectoryInfos;
                var cabIdx = dirs.FindIndex(d => !d.Name.EndsWith(".resS", StringComparison.Ordinal));
                var oldCab = dirs[cabIdx].Name;
                var newCab = CutinNaming.CabName(newName);
                var afi = am.LoadAssetsFileFromBundle(bun, cabIdx, false);
                am.LoadClassDatabaseFromPackage(afi.file.Metadata.UnityVersion);
                foreach (var ai in afi.file.GetAssetsOfType(AssetClassID.AssetBundle))
                {
                    var bf = am.GetBaseField(afi, ai);
                    foreach (var c in bf["m_Container"]["Array"].Children)
                        c["first"].AsString = c["first"].AsString
                            .Replace($"tcg/p{src}/", $"tcg/p{dst}/")
                            .Replace($"/p{src}.prefab", $"/p{dst}.prefab");
                    bf["m_Name"].AsString = newName;
                    bf["m_AssetBundleName"].AsString = newName;
                    ai.SetNewData(bf);
                }
                dirs[cabIdx].SetNewData(afi.file);
                dirs[cabIdx].Name = newCab;
                foreach (var d in dirs.Where(d => d.Name.EndsWith(".resS", StringComparison.Ordinal)))
                    d.Name = d.Name.Replace(oldCab, newCab);
                result.Add(new BuiltBundle(CutinNaming.BundleFile(playerDir, dstPath), BundleIo.SavePacked(bun)));
            }
            finally
            {
                am.UnloadAll();
            }
        }
        return result;
    }

    /// <summary>Last key time of the animation named "animation" (the cut-in length).</summary>
    public static double AnimDuration(JsonElement e)
    {
        double m = 0;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                m = Math.Max(m, p.Name == "time" && p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble() : AnimDuration(p.Value));
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in e.EnumerateArray())
                m = Math.Max(m, AnimDuration(v));
        }
        return m;
    }
}

internal static class BundleIo
{
    /// <summary>Write a modified bundle uncompressed, then return it packed with LZ4 (like the game's bundles).</summary>
    public static byte[] SavePacked(BundleFileInstance bundle)
    {
        var unc = Path.Combine(Path.GetTempPath(), $"floowan-cutin-{Guid.NewGuid():N}.bundle");
        try
        {
            using (var w = new AssetsFileWriter(unc))
                bundle.file.Write(w);
            var am2 = new AssetsManager();
            try
            {
                var packInst = am2.LoadBundleFile(unc, unpackIfPacked: false);
                using var ms = new MemoryStream();
                using (var w = new AssetsFileWriter(ms))
                    packInst.file.Pack(w, AssetBundleCompressionType.LZ4);
                return ms.ToArray();
            }
            finally
            {
                am2.UnloadAll();
            }
        }
        finally
        {
            try { File.Delete(unc); } catch { /* best effort */ }
        }
    }
}

using System.Text.Json;
using Floowan.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Backup;

public sealed class BackupService
{
    private static readonly JsonSerializerOptions StageJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _root;

    public BackupService(string? rootDirectory = null)
    {
        _root = rootDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "backups");
    }

    public string RootDirectory => _root;

    public string GetBundleBackupPath(string bundleId) =>
        Path.Combine(_root, "bundles", "cards", bundleId);

    public string GetGateBundleBackupPath(string bundleId) =>
        Path.Combine(_root, "bundles", "gate", bundleId);

    public string GetTextureBackupPath(string cardName) =>
        Path.Combine(_root, "cards", ImagePreparation.Slugify(cardName) + ".png");

    /// <summary>
    /// Original (pre-over-frame) art snapshot used by Auto-create on re-runs.
    /// </summary>
    public string GetOverFrameTextureBackupPath(string cardName) =>
        GetTextureBackupPath(cardName + "-overframe");

    /// <summary>
    /// Last successfully applied Floowan over-frame canvas (704x1024), used to re-apply after
    /// an MD patch replaces live art / of_card_asset.
    /// </summary>
    public string GetAppliedOverFrameBackupPath(string cardName) =>
        GetTextureBackupPath(cardName + "-applied-overframe");

    /// <summary>
    /// Directory for Custom OF editable stage (subject/background PNGs + params JSON).
    /// </summary>
    public string GetCustomOverframeStageDirectory(string cardName) =>
        Path.Combine(_root, "cards", ImagePreparation.Slugify(cardName) + "-custom-of");

    public string GetCustomOverframeStageJsonPath(string cardName) =>
        Path.Combine(GetCustomOverframeStageDirectory(cardName), "stage.json");

    public string GetCustomOverframeStageSubjectPath(string cardName) =>
        Path.Combine(GetCustomOverframeStageDirectory(cardName), "subject.png");

    public string GetCustomOverframeStageSubjectMaskPath(string cardName) =>
        Path.Combine(GetCustomOverframeStageDirectory(cardName), "subject-mask.png");

    public string GetCustomOverframeStageBackgroundPath(string cardName) =>
        Path.Combine(GetCustomOverframeStageDirectory(cardName), "background.png");

    public bool HasBundleBackup(string bundleId) =>
        File.Exists(GetBundleBackupPath(bundleId));

    public bool HasOverFrameTextureBackup(string cardName) =>
        File.Exists(GetOverFrameTextureBackupPath(cardName));

    public bool HasAppliedOverFrameBackup(string cardName) =>
        File.Exists(GetAppliedOverFrameBackupPath(cardName));

    public bool HasCustomOverframeStage(string cardName) =>
        File.Exists(GetCustomOverframeStageJsonPath(cardName));

    /// <summary>
    /// Copies the applied OF PNG into backups (overwrites). Used on successful Apply.
    /// </summary>
    public string SaveAppliedOverFramePng(string cardName, string sourcePngPath)
    {
        var dest = GetAppliedOverFrameBackupPath(cardName);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(sourcePngPath, dest, overwrite: true);
        return dest;
    }

    /// <summary>
    /// Persists Custom OF layers + transforms so the dialog can reopen an already OF card.
    /// </summary>
    public void SaveCustomOverframeStage(
        string cardName,
        CustomOverframeStageState state,
        Image<Rgba32>? subjectSource,
        Image<L8>? subjectMask,
        Image<Rgba32>? background)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dir = GetCustomOverframeStageDirectory(cardName);
        Directory.CreateDirectory(dir);

        state.HasSubject = subjectSource is not null && subjectMask is not null;
        state.HasBackground = background is not null;

        if (state.HasSubject)
        {
            subjectSource!.Save(GetCustomOverframeStageSubjectPath(cardName), new PngEncoder());
            subjectMask!.Save(GetCustomOverframeStageSubjectMaskPath(cardName), new PngEncoder());
        }
        else
        {
            TryDeleteFile(GetCustomOverframeStageSubjectPath(cardName));
            TryDeleteFile(GetCustomOverframeStageSubjectMaskPath(cardName));
        }

        if (state.HasBackground)
        {
            background!.Save(GetCustomOverframeStageBackgroundPath(cardName), new PngEncoder());
        }
        else
        {
            TryDeleteFile(GetCustomOverframeStageBackgroundPath(cardName));
        }

        var json = JsonSerializer.Serialize(state, StageJsonOptions);
        File.WriteAllText(GetCustomOverframeStageJsonPath(cardName), json);
    }

    /// <summary>
    /// Loads a previously saved Custom OF stage. Caller must dispose returned images.
    /// </summary>
    public bool TryLoadCustomOverframeStage(
        string cardName,
        out CustomOverframeStageState state,
        out Image<Rgba32>? subjectSource,
        out Image<L8>? subjectMask,
        out Image<Rgba32>? background)
    {
        state = new CustomOverframeStageState();
        subjectSource = null;
        subjectMask = null;
        background = null;

        var jsonPath = GetCustomOverframeStageJsonPath(cardName);
        if (!File.Exists(jsonPath))
            return false;

        try
        {
            var json = File.ReadAllText(jsonPath);
            var loaded = JsonSerializer.Deserialize<CustomOverframeStageState>(json, StageJsonOptions);
            if (loaded is null)
                return false;

            state = loaded;
            if (state.HasSubject)
            {
                var subjectPath = GetCustomOverframeStageSubjectPath(cardName);
                var maskPath = GetCustomOverframeStageSubjectMaskPath(cardName);
                if (!File.Exists(subjectPath) || !File.Exists(maskPath))
                {
                    DisposeLoaded(ref subjectSource, ref subjectMask, ref background);
                    return false;
                }

                subjectSource = Image.Load<Rgba32>(subjectPath);
                subjectMask = Image.Load<L8>(maskPath);
            }

            if (state.HasBackground)
            {
                var bgPath = GetCustomOverframeStageBackgroundPath(cardName);
                if (!File.Exists(bgPath))
                {
                    DisposeLoaded(ref subjectSource, ref subjectMask, ref background);
                    return false;
                }

                background = Image.Load<Rgba32>(bgPath);
            }

            return state.HasSubject || state.HasBackground;
        }
        catch
        {
            DisposeLoaded(ref subjectSource, ref subjectMask, ref background);
            return false;
        }
    }

    /// <summary>
    /// Drops the applied OF canvas backup (e.g. after Restore backups / intentional OF removal).
    /// </summary>
    public bool TryDeleteAppliedOverFrameBackup(string cardName)
    {
        var path = GetAppliedOverFrameBackupPath(cardName);
        if (!File.Exists(path))
            return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Drops the Custom OF editable stage directory (Restore / remove from gate).
    /// </summary>
    public bool TryDeleteCustomOverframeStage(string cardName)
    {
        var dir = GetCustomOverframeStageDirectory(cardName);
        if (!Directory.Exists(dir))
            return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DisposeLoaded(
        ref Image<Rgba32>? subjectSource,
        ref Image<L8>? subjectMask,
        ref Image<Rgba32>? background)
    {
        subjectSource?.Dispose();
        subjectMask?.Dispose();
        background?.Dispose();
        subjectSource = null;
        subjectMask = null;
        background = null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* best effort */
        }
    }

    /// <summary>
    /// Drops the pre-over-frame PNG snapshot so the next OF run re-reads current live art
    /// (e.g. after Card Art replacement). Bundle backups used for Restore are left intact.
    /// </summary>
    public bool TryInvalidateOverFrameTextureBackup(string cardName)
    {
        var path = GetOverFrameTextureBackupPath(cardName);
        if (!File.Exists(path))
            return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string BackupBundleFile(string sourceBundlePath, string bundleId) =>
        BackupFile(sourceBundlePath, GetBundleBackupPath(bundleId));

    public string BackupGateBundleFile(string sourceBundlePath, string bundleId) =>
        BackupFile(sourceBundlePath, GetGateBundleBackupPath(bundleId));

    public bool TryRestoreBundleFile(string targetBundlePath, string bundleId) =>
        TryRestoreFile(targetBundlePath, GetBundleBackupPath(bundleId));

    public bool TryRestoreGateBundleFile(string targetBundlePath, string bundleId) =>
        TryRestoreFile(targetBundlePath, GetGateBundleBackupPath(bundleId));

    /// <summary>
    /// Copies the bundle only when no backup exists yet. Returns whether a new file was written.
    /// </summary>
    public bool TryCreateBundleBackupIfMissing(string sourceBundlePath, string bundleId, out string backupPath)
    {
        backupPath = GetBundleBackupPath(bundleId);
        if (File.Exists(backupPath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(sourceBundlePath, backupPath);
        return true;
    }

    private static string BackupFile(string sourcePath, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!File.Exists(dest))
            File.Copy(sourcePath, dest);
        return dest;
    }

    private static bool TryRestoreFile(string targetPath, string backupPath)
    {
        if (!File.Exists(backupPath))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        File.Copy(backupPath, targetPath, overwrite: true);
        return true;
    }
}

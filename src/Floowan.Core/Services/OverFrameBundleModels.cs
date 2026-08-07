using System.Text.Json.Serialization;
using Floowan.Core.Backup;

namespace Floowan.Core.Services;

/// <summary>
/// Root manifest for a <c>.overframes</c> pack (zip).
/// </summary>
public sealed class OverFrameBundleManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string ExportedAtUtc { get; set; } = "";

    public string? ExporterVersion { get; set; }

    public List<OverFrameBundleCardEntry> Cards { get; set; } = new();
}

/// <summary>
/// One card in a <c>.overframes</c> pack.
/// </summary>
public sealed class OverFrameBundleCardEntry
{
    public int CardId { get; set; }

    public string Name { get; set; } = "";

    public string? Bundle { get; set; }

    public int? ArtId { get; set; }

    public int? OverframeBaseId { get; set; }

    public string? AppliedAtUtc { get; set; }

    /// <summary>Relative zip path to the 704×1024 applied canvas PNG, when present.</summary>
    public string? AppliedPng { get; set; }

    public bool HasEditLayer { get; set; }

    public OverFrameBundleLayerEntry? EditLayer { get; set; }
}

/// <summary>
/// Editable Custom/Auto OF stage transforms + relative PNG paths inside the zip.
/// </summary>
public sealed class OverFrameBundleLayerEntry
{
    public int Version { get; set; } = CustomOverframeStageState.CurrentVersion;

    public string FrameStyle { get; set; } = nameof(Imaging.CardFrameStyle.Effect);

    public float SubjectScale { get; set; } = 1.5f;
    public int SubjectOffsetX { get; set; }
    public int SubjectOffsetY { get; set; }

    public float BackgroundScale { get; set; } = 1.0f;
    public int BackgroundOffsetX { get; set; }
    public int BackgroundOffsetY { get; set; }

    public bool BackgroundIsCardArt { get; set; }
    public bool SubjectIsFromCardArt { get; set; }

    public string? SubjectPng { get; set; }
    public string? SubjectMaskPng { get; set; }
    public string? BackgroundPng { get; set; }

    public CustomOverframeStageState ToStageState() => new()
    {
        Version = Version <= 0 ? CustomOverframeStageState.CurrentVersion : Version,
        FrameStyle = string.IsNullOrWhiteSpace(FrameStyle)
            ? nameof(Imaging.CardFrameStyle.Effect)
            : FrameStyle,
        SubjectScale = SubjectScale,
        SubjectOffsetX = SubjectOffsetX,
        SubjectOffsetY = SubjectOffsetY,
        BackgroundScale = BackgroundScale,
        BackgroundOffsetX = BackgroundOffsetX,
        BackgroundOffsetY = BackgroundOffsetY,
        BackgroundIsCardArt = BackgroundIsCardArt,
        SubjectIsFromCardArt = SubjectIsFromCardArt,
        HasSubject = !string.IsNullOrWhiteSpace(SubjectPng) && !string.IsNullOrWhiteSpace(SubjectMaskPng),
        HasBackground = !string.IsNullOrWhiteSpace(BackgroundPng)
    };

    public static OverFrameBundleLayerEntry FromStage(
        CustomOverframeStageState state,
        string? subjectPng,
        string? subjectMaskPng,
        string? backgroundPng) =>
        new()
        {
            Version = state.Version,
            FrameStyle = state.FrameStyle,
            SubjectScale = state.SubjectScale,
            SubjectOffsetX = state.SubjectOffsetX,
            SubjectOffsetY = state.SubjectOffsetY,
            BackgroundScale = state.BackgroundScale,
            BackgroundOffsetX = state.BackgroundOffsetX,
            BackgroundOffsetY = state.BackgroundOffsetY,
            BackgroundIsCardArt = state.BackgroundIsCardArt,
            SubjectIsFromCardArt = state.SubjectIsFromCardArt,
            SubjectPng = subjectPng,
            SubjectMaskPng = subjectMaskPng,
            BackgroundPng = backgroundPng
        };
}

public sealed class OverFrameBundleExportItem
{
    public int CardId { get; init; }
    public string DisplayName { get; init; } = "";
    public string Name { get; init; } = "";
    public bool HasEditLayer { get; init; }
    public bool HasAppliedCanvas { get; init; }
}

public sealed class OverFrameBundleExportResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int CardsExported { get; init; }
    public int CardsSkipped { get; init; }
    public string? OutputPath { get; init; }

    public static OverFrameBundleExportResult Ok(string path, int exported, int skipped) => new()
    {
        Success = true,
        Message = skipped > 0
            ? $"Exported {exported} card(s) to {Path.GetFileName(path)} ({skipped} skipped)."
            : $"Exported {exported} card(s) to {Path.GetFileName(path)}.",
        CardsExported = exported,
        CardsSkipped = skipped,
        OutputPath = path
    };

    public static OverFrameBundleExportResult Fail(string message) =>
        new() { Success = false, Message = message };
}

public sealed class OverFrameBundleImportResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int CardsImported { get; init; }
    public int CardsFailed { get; init; }
    public int CardsSkipped { get; init; }

    public static OverFrameBundleImportResult Ok(int imported, int failed, int skipped) => new()
    {
        Success = failed == 0,
        Message = failed > 0
            ? $"Imported {imported} card(s); {failed} failed; {skipped} skipped."
            : skipped > 0
                ? $"Imported {imported} card(s); {skipped} skipped."
                : $"Imported {imported} card(s).",
        CardsImported = imported,
        CardsFailed = failed,
        CardsSkipped = skipped
    };

    public static OverFrameBundleImportResult Fail(string message) =>
        new() { Success = false, Message = message };
}

/// <summary>JSON source-gen friendly options for pack manifests.</summary>
public static class OverFrameBundleJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

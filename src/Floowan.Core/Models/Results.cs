namespace Floowan.Core.Models;

public sealed class ImageValidationResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Warning { get; init; }
    public string? Info { get; init; }

    public static ImageValidationResult Ok(
        int width,
        int height,
        string? warning = null,
        string? info = null) =>
        new() { IsValid = true, Width = width, Height = height, Warning = warning, Info = info };

    public static ImageValidationResult Fail(string error) =>
        new() { IsValid = false, Error = error };
}

public sealed class CardArtReplacementResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? BundlePath { get; init; }
    public string? BackupPath { get; init; }

    public static CardArtReplacementResult Ok(string message, string bundlePath, string? backupPath) =>
        new() { Success = true, Message = message, BundlePath = bundlePath, BackupPath = backupPath };

    public static CardArtReplacementResult Fail(string message) =>
        new() { Success = false, Message = message };
}

public sealed class OverFrameResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? CardBundlePath { get; init; }
    public string? GateBundlePath { get; init; }
    public bool InGate { get; init; }

    public static OverFrameResult Ok(
        string message,
        string? cardBundlePath = null,
        string? gateBundlePath = null,
        bool inGate = false) =>
        new()
        {
            Success = true,
            Message = message,
            CardBundlePath = cardBundlePath,
            GateBundlePath = gateBundlePath,
            InGate = inGate
        };

    public static OverFrameResult Fail(string message) =>
        new() { Success = false, Message = message };
}

public sealed class OverFrameRestoreBatchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int Total { get; init; }
    public int Restored { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static OverFrameRestoreBatchResult Create(
        int total,
        int restored,
        int skipped,
        int failed,
        IReadOnlyList<string> warnings,
        string? gateBundlePath = null)
    {
        var parts = new List<string>
        {
            $"Restore overframes after patch: {restored} restored, {skipped} skipped, {failed} failed (of {total})."
        };
        if (!string.IsNullOrWhiteSpace(gateBundlePath))
            parts.Add("Gate: " + gateBundlePath);
        parts.Add("Official of_card_asset entries were preserved (additive merge). Quit Master Duel fully so LocalData reloads.");

        return new OverFrameRestoreBatchResult
        {
            Success = failed == 0,
            Message = string.Join(" ", parts),
            Total = total,
            Restored = restored,
            Skipped = skipped,
            Failed = failed,
            Warnings = warnings
        };
    }
}

/// <summary>
/// Result of scanning live art bundles for OF-sized textures missing/incomplete in user.db / gate.
/// </summary>
public sealed class OverFrameOrphanRepairBatchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int Scanned { get; init; }
    public int LiveOverframeFound { get; init; }
    public int Fixed { get; init; }
    public int GateUpdated { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static OverFrameOrphanRepairBatchResult Create(
        int scanned,
        int liveOverframeFound,
        int fixedCount,
        int gateUpdated,
        int skipped,
        int failed,
        IReadOnlyList<string> warnings,
        string? gateBundlePath = null)
    {
        var parts = new List<string>
        {
            $"Slower full fix with asset scans: {fixedCount} fixed, {gateUpdated} gate update(s), " +
            $"{skipped} already ok, {failed} failed " +
            $"(scanned {scanned} bundle(s), {liveOverframeFound} live OF texture(s))."
        };
        if (!string.IsNullOrWhiteSpace(gateBundlePath))
            parts.Add("Gate: " + gateBundlePath);
        parts.Add("Official of_card_asset entries were preserved (additive merge). Quit Master Duel fully so LocalData reloads.");

        return new OverFrameOrphanRepairBatchResult
        {
            Success = failed == 0,
            Message = string.Join(" ", parts),
            Scanned = scanned,
            LiveOverframeFound = liveOverframeFound,
            Fixed = fixedCount,
            GateUpdated = gateUpdated,
            Skipped = skipped,
            Failed = failed,
            Warnings = warnings
        };
    }
}

namespace Floowan.Core.Models;

public sealed class ImageValidationResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Warning { get; init; }

    public static ImageValidationResult Ok(int width, int height, string? warning = null) =>
        new() { IsValid = true, Width = width, Height = height, Warning = warning };

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

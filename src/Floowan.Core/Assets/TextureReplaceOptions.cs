namespace Floowan.Core.Assets;

/// <summary>
/// Options for Texture2D replacement. When Width/Height are null, the existing
/// texture dimensions are kept (normal card-art behavior). Over-frame uses 704×1024.
/// </summary>
public sealed record TextureReplaceOptions
{
    public static TextureReplaceOptions Default { get; } = new();

    public static TextureReplaceOptions OverFrame { get; } = new()
    {
        Width = OverFrameConstants.Width,
        Height = OverFrameConstants.Height,
        Compression = "lz4"
    };

    public int? Width { get; init; }
    public int? Height { get; init; }
    public string Compression { get; init; } = "lz4";
}

public static class OverFrameConstants
{
    public const int Width = 704;
    public const int Height = 1024;
}

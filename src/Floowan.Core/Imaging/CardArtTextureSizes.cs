namespace Floowan.Core.Imaging;

/// <summary>
/// Master Duel card-illustration Texture2D sizes used by LocalData illust bundles.
/// Normal monsters / spells / traps use a square canvas (512×512).
/// Pendulum <em>art</em> is <strong>3:4</strong> (typically 512×683). The live MD
/// Texture2D canvas is still often 512×1024; Card Art extract crops the top 3:4 band
/// to 512×683. UV / OF paths use that same top band.
/// </summary>
public static class CardArtTextureSizes
{
    public const int NormalWidth = 512;
    public const int NormalHeight = 512;

    /// <summary>Canonical Pendulum illustration width (3:4).</summary>
    public const int PendulumWidth = 512;

    /// <summary>Canonical Pendulum illustration height — round(512 × 4/3).</summary>
    public const int PendulumHeight = 683;

    /// <summary>Live Master Duel Pendulum Texture2D canvas width.</summary>
    public const int PendulumNativeWidth = 512;

    /// <summary>Live Master Duel Pendulum Texture2D canvas height (taller storage).</summary>
    public const int PendulumNativeHeight = 1024;

    /// <summary>Width / height for a normal illust (1.0).</summary>
    public const double NormalAspect = (double)NormalWidth / NormalHeight;

    /// <summary>Width / height for Pendulum art (3:4 = 0.75).</summary>
    public const double PendulumAspect = 3.0 / 4.0;

    public static bool IsNormal(int width, int height) =>
        width == NormalWidth && height == NormalHeight;

    /// <summary>Exact canonical Pendulum art size (512×683).</summary>
    public static bool IsPendulum(int width, int height) =>
        width == PendulumWidth && height == PendulumHeight;

    /// <summary>Live MD Pendulum Texture2D canvas (512×1024).</summary>
    public static bool IsPendulumNativeCanvas(int width, int height) =>
        width == PendulumNativeWidth && height == PendulumNativeHeight;

    /// <summary>
    /// Tall storage canvas (~1:2) used by live Pendulum Texture2D — art lives in the top 3:4 band.
    /// </summary>
    public static bool IsTallPendulumStorageCanvas(int width, int height)
    {
        if (IsPendulumNativeCanvas(width, height))
            return true;
        if (width <= 0 || height <= 0 || height <= width)
            return false;
        // Exact MD canvas or same 1:2 aspect at the native width (or close).
        var aspect = width / (double)height;
        return Math.Abs(aspect - 0.5) <= 0.02 && width >= PendulumWidth / 2;
    }

    /// <summary>Crop height for the top 3:4 art band of a tall Pendulum canvas.</summary>
    public static int PendulumArtCropHeight(int canvasWidth) =>
        Math.Max(1, (int)Math.Round(canvasWidth * 4.0 / 3.0));

    /// <summary>
    /// PNG export size for Card Art extract: Pendulum → canonical 512×683; otherwise live size.
    /// </summary>
    public static (int Width, int Height) GetCardArtExportSize(int textureWidth, int textureHeight)
    {
        if (Classify(textureWidth, textureHeight) == CardArtSizeKind.Pendulum ||
            IsTallPendulumStorageCanvas(textureWidth, textureHeight))
            return (PendulumWidth, PendulumHeight);
        return (textureWidth, textureHeight);
    }

    /// <summary>
    /// True when the size is an exact MD illust size, the live Pendulum canvas,
    /// or matches the Pendulum 3:4 art aspect (within a small tolerance).
    /// </summary>
    public static bool IsSupportedCardArtSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;
        if (IsNormal(width, height) || IsPendulum(width, height) || IsPendulumNativeCanvas(width, height))
            return true;
        return HasPendulumAspect(width, height);
    }

    public static bool HasPendulumAspect(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;
        var aspect = width / (double)height;
        return Math.Abs(aspect - PendulumAspect) <= 0.02;
    }

    public static bool HasNormalAspect(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;
        var aspect = width / (double)height;
        return Math.Abs(aspect - NormalAspect) <= 0.02;
    }

    public static bool SameAspect(int widthA, int heightA, int widthB, int heightB)
    {
        if (widthA <= 0 || heightA <= 0 || widthB <= 0 || heightB <= 0)
            return false;
        var a = widthA / (double)heightA;
        var b = widthB / (double)heightB;
        return Math.Abs(a - b) <= 0.02;
    }

    public static CardArtSizeKind Classify(int width, int height)
    {
        if (IsPendulum(width, height) ||
            IsPendulumNativeCanvas(width, height) ||
            HasPendulumAspect(width, height))
            return CardArtSizeKind.Pendulum;
        if (IsNormal(width, height) || HasNormalAspect(width, height))
            return CardArtSizeKind.Normal;
        return CardArtSizeKind.Other;
    }

    public static string Describe(int width, int height)
    {
        var kind = Classify(width, height);
        return kind switch
        {
            CardArtSizeKind.Pendulum when IsPendulum(width, height) =>
                $"{width}×{height} (Pendulum 3:4)",
            CardArtSizeKind.Pendulum when IsPendulumNativeCanvas(width, height) =>
                $"{width}×{height} (Pendulum native canvas)",
            CardArtSizeKind.Pendulum =>
                $"{width}×{height} (Pendulum 3:4 aspect)",
            CardArtSizeKind.Normal when IsNormal(width, height) =>
                $"{width}×{height} (normal illust)",
            CardArtSizeKind.Normal =>
                $"{width}×{height} (normal aspect)",
            _ => $"{width}×{height}"
        };
    }
}

public enum CardArtSizeKind
{
    Other = 0,
    Normal = 1,
    Pendulum = 2
}

namespace Floowan.Core.Imaging;

/// <summary>
/// Master Duel card-illustration Texture2D sizes used by LocalData illust bundles.
/// Normal monsters / spells / traps use a square canvas; Pendulum cards use a taller
/// 1:2 texture (community-confirmed as 512×1024).
/// </summary>
public static class CardArtTextureSizes
{
    public const int NormalWidth = 512;
    public const int NormalHeight = 512;

    public const int PendulumWidth = 512;
    public const int PendulumHeight = 1024;

    /// <summary>Width / height for a normal illust (1.0).</summary>
    public const double NormalAspect = (double)NormalWidth / NormalHeight;

    /// <summary>Width / height for a Pendulum illust (0.5).</summary>
    public const double PendulumAspect = (double)PendulumWidth / PendulumHeight;

    public static bool IsNormal(int width, int height) =>
        width == NormalWidth && height == NormalHeight;

    public static bool IsPendulum(int width, int height) =>
        width == PendulumWidth && height == PendulumHeight;

    /// <summary>
    /// True when the size is an exact MD illust size or matches the Pendulum 1:2 aspect
    /// (within a small tolerance for near-exact exports).
    /// </summary>
    public static bool IsSupportedCardArtSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return false;
        if (IsNormal(width, height) || IsPendulum(width, height))
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
        if (IsPendulum(width, height) || HasPendulumAspect(width, height))
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
                $"{width}×{height} (Pendulum)",
            CardArtSizeKind.Pendulum =>
                $"{width}×{height} (Pendulum aspect)",
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

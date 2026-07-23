using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Floowan.Core.Imaging;

/// <summary>
/// Loads bundled 704×1024 card-face templates (frames with a transparent art window).
/// Extracted from Master Duel <c>data.unity3d</c> <c>card_frame*</c> Texture2D assets.
/// </summary>
public static class CardFrameTemplates
{
    private static readonly IReadOnlyDictionary<CardFrameStyle, string> FileNames =
        new Dictionary<CardFrameStyle, string>
        {
            [CardFrameStyle.Effect] = "Effect.png",
            [CardFrameStyle.Normal] = "Normal.png",
            [CardFrameStyle.Fusion] = "Fusion.png",
            [CardFrameStyle.Synchro] = "Synchro.png",
            [CardFrameStyle.Xyz] = "Xyz.png",
            [CardFrameStyle.Ritual] = "Ritual.png",
            [CardFrameStyle.Spell] = "Spell.png",
            [CardFrameStyle.Trap] = "Trap.png",
        };

    public static string GetFileName(CardFrameStyle style) =>
        FileNames.TryGetValue(style, out var name) ? name : FileNames[CardFrameStyle.Effect];

    public static string ResolveTemplatePath(CardFrameStyle style, string? overrideDirectory = null)
    {
        var fileName = GetFileName(style);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            var custom = Path.Combine(overrideDirectory, fileName);
            if (File.Exists(custom))
                return custom;
        }

        foreach (var candidate in GetSearchPaths(fileName))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Card frame template '{fileName}' was not found. Expected it next to the app or under Resources/frames.",
            fileName);
    }

    public static Image<Rgba32> Load(CardFrameStyle style, string? overrideDirectory = null)
    {
        var path = ResolveTemplatePath(style, overrideDirectory);
        return Image.Load<Rgba32>(path);
    }

    /// <summary>
    /// Best-effort style guess from the card type line (e.g. <c>[Dragon/Effect]</c>).
    /// Does not scan effect body text — phrases like "Link Monster" there caused false Link predictions.
    /// </summary>
    public static CardFrameStyle InferStyle(string? name, string? description)
    {
        var text = $"{name}\n{description}".ToLowerInvariant();
        var typeLine = ExtractTypeLine(text);
        if (typeLine is not null)
            return InferFromTypeLine(typeLine);

        // No bracket type line — only accept explicit spell/trap card labels.
        if (ContainsAny(text, "spell card", "[spell"))
            return CardFrameStyle.Spell;
        if (ContainsAny(text, "trap card", "[trap"))
            return CardFrameStyle.Trap;

        return CardFrameStyle.Effect;
    }

    private static string? ExtractTypeLine(string lowerText)
    {
        // Prefer the first [Race/Types…] line; ignore later bracketed reminders in effects.
        var start = lowerText.IndexOf('[');
        while (start >= 0)
        {
            var end = lowerText.IndexOf(']', start + 1);
            if (end < 0)
                break;

            var segment = lowerText[start..(end + 1)];
            if (LooksLikeTypeLine(segment))
                return segment;

            start = lowerText.IndexOf('[', end + 1);
        }

        return null;
    }

    private static bool LooksLikeTypeLine(string bracketed)
    {
        // Real type lines are short and use / separators, e.g. [fiend/effect], [cyberse/link/effect].
        if (bracketed.Length is < 5 or > 80)
            return false;

        return ContainsAny(
            bracketed,
            "/effect",
            "/normal",
            "/fusion",
            "/synchro",
            "/xyz",
            "/link",
            "/ritual",
            "/pendulum",
            "/tuner",
            "/token",
            "spell]",
            "trap]",
            "spell card",
            "trap card");
    }

    private static CardFrameStyle InferFromTypeLine(string typeLine)
    {
        // Order matters: Xyz/Synchro/… before generic /effect.
        // Link monsters are not a supported frame template — use Effect.
        if (typeLine.Contains("/link", StringComparison.Ordinal) || typeLine.StartsWith("[link", StringComparison.Ordinal))
            return CardFrameStyle.Effect;
        if (typeLine.Contains("/xyz", StringComparison.Ordinal) || typeLine.Contains("rank", StringComparison.Ordinal))
            return CardFrameStyle.Xyz;
        if (typeLine.Contains("/synchro", StringComparison.Ordinal))
            return CardFrameStyle.Synchro;
        if (typeLine.Contains("/fusion", StringComparison.Ordinal))
            return CardFrameStyle.Fusion;
        if (typeLine.Contains("/ritual", StringComparison.Ordinal))
            return CardFrameStyle.Ritual;
        if (ContainsAny(typeLine, "spell]", "spell card", "[spell"))
            return CardFrameStyle.Spell;
        if (ContainsAny(typeLine, "trap]", "trap card", "[trap"))
            return CardFrameStyle.Trap;
        if (typeLine.Contains("/normal", StringComparison.Ordinal))
            return CardFrameStyle.Normal;
        if (typeLine.Contains("/effect", StringComparison.Ordinal) || typeLine.Contains("effect]", StringComparison.Ordinal))
            return CardFrameStyle.Effect;

        return CardFrameStyle.Effect;
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.Ordinal));

    private static IEnumerable<string> GetSearchPaths(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "frames", fileName);
        yield return Path.Combine(baseDir, "Resources", "frames", fileName);

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "Resources", "frames", fileName);
            yield return Path.Combine(dir.FullName, "src", "Floowan.Core", "Resources", "frames", fileName);
        }
    }
}

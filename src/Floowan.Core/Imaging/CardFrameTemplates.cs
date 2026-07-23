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
            [CardFrameStyle.Link] = "Link.png",
            [CardFrameStyle.Token] = "Token.png",
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
    /// Best-effort style guess from the card type line (first lore line starting with <c>[</c>).
    /// Returns <c>null</c> when there is no confident type line / leading Spell|Trap label —
    /// never forces Effect from mid-body phrases like "Spell Card".
    /// </summary>
    public static CardFrameStyle? InferStyle(string? name, string? description)
    {
        _ = name; // name is unused for inference; kept for call-site compatibility
        var descriptionText = description ?? string.Empty;
        var lowerDescription = descriptionText.ToLowerInvariant();
        var typeLine = ExtractTypeLine(lowerDescription);
        if (typeLine is not null)
            return InferFromTypeLine(typeLine);

        // Spell/Trap usually lack a [type] line — only accept an explicit leading label.
        var leading = lowerDescription.TrimStart();
        if (leading.StartsWith("spell card", StringComparison.Ordinal))
            return CardFrameStyle.Spell;
        if (leading.StartsWith("trap card", StringComparison.Ordinal))
            return CardFrameStyle.Trap;

        return null;
    }

    private static string? ExtractTypeLine(string lowerText)
    {
        // Prefer the first lore line that starts with [Race/Types…] — ignore mid-body brackets.
        foreach (var rawLine in lowerText.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
        {
            var line = rawLine.TrimStart();
            if (line.Length == 0)
                continue;
            if (!line.StartsWith('['))
                continue;

            var end = line.IndexOf(']');
            if (end < 0)
                return null;

            var segment = line[..(end + 1)];
            return LooksLikeTypeLine(segment) ? segment : null;
        }

        return null;
    }

    private static bool LooksLikeTypeLine(string bracketed)
    {
        // Real type lines are short, e.g. [fiend/effect], [cyberse/link/effect], [dragon], [trap].
        if (bracketed.Length is < 3 or > 80)
            return false;

        if (ContainsAny(
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
                "trap card"))
        {
            return true;
        }

        // Bare race / short label without known markers → still a type line (→ Normal).
        var inner = bracketed[1..^1].Trim();
        if (inner.Length is < 1 or > 40)
            return false;
        if (ContainsAny(inner, "once per", "you can", "this card", "special summon"))
            return false;
        foreach (var c in inner)
        {
            if (!(char.IsLetter(c) || c is ' ' or '/' or '-'))
                return false;
        }

        return true;
    }

    private static CardFrameStyle InferFromTypeLine(string typeLine)
    {
        // Special frames first (Link/Xyz/Token/...) -- even when the line also contains /effect.
        if (typeLine.Contains("/link", StringComparison.Ordinal) || typeLine.StartsWith("[link", StringComparison.Ordinal))
            return CardFrameStyle.Link;
        if (typeLine.Contains("/xyz", StringComparison.Ordinal) || typeLine.Contains("rank", StringComparison.Ordinal))
            return CardFrameStyle.Xyz;
        if (typeLine.Contains("/synchro", StringComparison.Ordinal))
            return CardFrameStyle.Synchro;
        if (typeLine.Contains("/fusion", StringComparison.Ordinal))
            return CardFrameStyle.Fusion;
        if (typeLine.Contains("/ritual", StringComparison.Ordinal))
            return CardFrameStyle.Ritual;
        if (typeLine.Contains("/token", StringComparison.Ordinal) || typeLine.Contains("token]", StringComparison.Ordinal))
            return CardFrameStyle.Token;
        if (ContainsAny(typeLine, "spell]", "spell card", "[spell"))
            return CardFrameStyle.Spell;
        if (ContainsAny(typeLine, "trap]", "trap card", "[trap"))
            return CardFrameStyle.Trap;
        if (typeLine.Contains("/effect", StringComparison.Ordinal) || typeLine.Contains("effect]", StringComparison.Ordinal))
            return CardFrameStyle.Effect;
        if (typeLine.Contains("/normal", StringComparison.Ordinal))
            return CardFrameStyle.Normal;

        // Usable [type] line without special/effect markers → Normal (e.g. [Dragon]).
        return CardFrameStyle.Normal;
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

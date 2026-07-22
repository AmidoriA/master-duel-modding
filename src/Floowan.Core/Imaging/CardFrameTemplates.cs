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
            [CardFrameStyle.EffectExt] = "EffectExt.png",
            [CardFrameStyle.Effect] = "Effect.png",
            [CardFrameStyle.Normal] = "Normal.png",
            [CardFrameStyle.Fusion] = "Fusion.png",
            [CardFrameStyle.Synchro] = "Synchro.png",
            [CardFrameStyle.Xyz] = "Xyz.png",
            [CardFrameStyle.Ritual] = "Ritual.png",
            [CardFrameStyle.Spell] = "Spell.png",
            [CardFrameStyle.Trap] = "Trap.png",
            [CardFrameStyle.Link] = "Link.png",
        };

    public static string GetFileName(CardFrameStyle style) =>
        FileNames.TryGetValue(style, out var name) ? name : FileNames[CardFrameStyle.EffectExt];

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
    /// Best-effort style guess from Floowandereeze card text (type line / keywords).
    /// </summary>
    public static CardFrameStyle InferStyle(string? name, string? description)
    {
        var text = $"{name}\n{description}".ToLowerInvariant();

        if (ContainsAny(text, "[spell", "spell card", "/spell]", "continuous spell", "quick-play"))
            return CardFrameStyle.Spell;
        if (ContainsAny(text, "[trap", "trap card", "/trap]", "counter trap", "continuous trap"))
            return CardFrameStyle.Trap;
        if (ContainsAny(text, "link monster", "/link]", "link-"))
            return CardFrameStyle.Link;
        if (ContainsAny(text, "xyz monster", "/xyz]", "rank "))
            return CardFrameStyle.Xyz;
        if (ContainsAny(text, "synchro monster", "/synchro]"))
            return CardFrameStyle.Synchro;
        if (ContainsAny(text, "fusion monster", "/fusion]", "fusion summon"))
            return CardFrameStyle.Fusion;
        if (ContainsAny(text, "ritual monster", "/ritual]", "ritual summon"))
            return CardFrameStyle.Ritual;
        if (ContainsAny(text, "/normal]", "normal monster"))
            return CardFrameStyle.Normal;
        if (ContainsAny(text, "/effect]", "effect monster"))
            return CardFrameStyle.EffectExt;

        // Default to the game's over-frame extension face (Effect chrome).
        return CardFrameStyle.EffectExt;
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

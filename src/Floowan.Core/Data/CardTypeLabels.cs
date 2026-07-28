using Floowan.Core.Imaging;

namespace Floowan.Core.Data;

/// <summary>
/// Human-readable card type labels stored in <c>card.card_type</c>.
/// Derived from CARD_Prop via <see cref="CardPropTypeDecoder"/> or description type lines via
/// <see cref="CardFrameTemplates.InferStyle"/> (same heuristics Floowan uses for over-frame selection).
/// </summary>
public static class CardTypeLabels
{
    public static string? InferFromCardText(string? name, string? description)
    {
        var style = CardFrameTemplates.InferStyle(name, description);
        return style is null ? null : ToLabel(style.Value);
    }

    public static string ToLabel(CardFrameStyle style) => style switch
    {
        CardFrameStyle.Effect => "Effect",
        CardFrameStyle.Normal => "Normal",
        CardFrameStyle.Fusion => "Fusion",
        CardFrameStyle.Synchro => "Synchro",
        CardFrameStyle.Xyz => "Xyz",
        CardFrameStyle.Ritual => "Ritual",
        CardFrameStyle.Spell => "Spell",
        CardFrameStyle.Trap => "Trap",
        CardFrameStyle.Link => "Link",
        CardFrameStyle.Token => "Token",
        CardFrameStyle.PendulumNormal => "Normal Pendulum",
        CardFrameStyle.PendulumEffect => "Effect Pendulum",
        CardFrameStyle.PendulumFusion => "Fusion Pendulum",
        CardFrameStyle.PendulumSynchro => "Synchro Pendulum",
        CardFrameStyle.PendulumXyz => "Xyz Pendulum",
        CardFrameStyle.PendulumRitual => "Ritual Pendulum",
        CardFrameStyle.PendulumToken => "Token Pendulum",
        CardFrameStyle.OfGradientEffect => "OF Gradient Effect",
        CardFrameStyle.OfGradientNormal => "OF Gradient Normal",
        CardFrameStyle.OfGradientFusion => "OF Gradient Fusion",
        CardFrameStyle.OfGradientSynchro => "OF Gradient Synchro",
        CardFrameStyle.OfGradientXyz => "OF Gradient Xyz",
        CardFrameStyle.OfGradientRitual => "OF Gradient Ritual",
        CardFrameStyle.OfGradientSpell => "OF Gradient Spell",
        CardFrameStyle.OfGradientTrap => "OF Gradient Trap",
        CardFrameStyle.OfGradientLink => "OF Gradient Link",
        CardFrameStyle.OfGradientToken => "OF Gradient Token",
        CardFrameStyle.OfGradientPendulumNormal => "OF Gradient Normal Pendulum",
        CardFrameStyle.OfGradientPendulumEffect => "OF Gradient Effect Pendulum",
        CardFrameStyle.OfGradientPendulumFusion => "OF Gradient Fusion Pendulum",
        CardFrameStyle.OfGradientPendulumSynchro => "OF Gradient Synchro Pendulum",
        CardFrameStyle.OfGradientPendulumXyz => "OF Gradient Xyz Pendulum",
        CardFrameStyle.OfGradientPendulumRitual => "OF Gradient Ritual Pendulum",
        CardFrameStyle.OfGradientPendulumToken => "OF Gradient Token Pendulum",
        _ => style.ToString()
    };

    /// <summary>
    /// Maps a stored <c>card_type</c> label back to <see cref="CardFrameStyle"/>.
    /// Accepts <see cref="ToLabel"/> strings (case-insensitive) and enum names.
    /// </summary>
    public static bool TryParseStyle(string? label, out CardFrameStyle style)
    {
        style = default;
        if (string.IsNullOrWhiteSpace(label))
            return false;

        var trimmed = label.Trim();
        foreach (CardFrameStyle candidate in Enum.GetValues<CardFrameStyle>())
        {
            if (string.Equals(ToLabel(candidate), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                style = candidate;
                return true;
            }
        }

        // Compact / enum-name forms: "PendulumEffect", "pendulum effect", etc.
        var compact = trimmed.Replace(" ", "", StringComparison.Ordinal);
        if (Enum.TryParse(compact, ignoreCase: true, out style))
            return true;

        // Word-order aliases: "Pendulum Normal" ↔ "Normal Pendulum".
        if (TryParsePendulumAlias(trimmed, out style))
            return true;

        return false;
    }

    /// <summary>
    /// Prefers DB <paramref name="cardType"/> when present; otherwise description-text InferStyle.
    /// </summary>
    public static CardFrameStyle? InferStyle(string? cardType, string? name, string? description)
    {
        if (TryParseStyle(cardType, out var fromType))
            return fromType;
        return CardFrameTemplates.InferStyle(name, description);
    }

    private static bool TryParsePendulumAlias(string label, out CardFrameStyle style)
    {
        style = default;
        var lower = label.Trim().ToLowerInvariant();
        switch (lower)
        {
            case "pendulum normal":
                style = CardFrameStyle.PendulumNormal;
                return true;
            case "pendulum effect":
                style = CardFrameStyle.PendulumEffect;
                return true;
            case "pendulum fusion":
                style = CardFrameStyle.PendulumFusion;
                return true;
            case "pendulum synchro":
                style = CardFrameStyle.PendulumSynchro;
                return true;
            case "pendulum xyz":
                style = CardFrameStyle.PendulumXyz;
                return true;
            case "pendulum ritual":
                style = CardFrameStyle.PendulumRitual;
                return true;
            case "pendulum token":
                style = CardFrameStyle.PendulumToken;
                return true;
            default:
                return false;
        }
    }
}

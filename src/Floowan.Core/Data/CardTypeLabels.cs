using Floowan.Core.Imaging;

namespace Floowan.Core.Data;

/// <summary>
/// Human-readable card type labels stored in <c>card.card_type</c>.
/// Derived from description type lines via <see cref="CardFrameTemplates.InferStyle"/>
/// (same heuristics Floowan already uses for over-frame frame selection).
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
        _ => style.ToString()
    };
}

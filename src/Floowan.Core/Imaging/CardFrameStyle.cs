namespace Floowan.Core.Imaging;

/// <summary>
/// Master Duel card-face styles used when baking a frame-with-hole into over-frame art.
/// Texture templates are the game's <c>card_frame*</c> 704×1024 assets (art window already A=0).
/// <see cref="OfGradientEffect"/> and siblings are Floowan-derived OF premium presets
/// (brighter gradient outer rim) — additive, never returned by type-line inference.
/// </summary>
public enum CardFrameStyle
{
    Effect,
    Normal,
    Fusion,
    Synchro,
    Xyz,
    /// <summary>Master Duel <c>card_frame02</c> — Ritual face (plain blue; not Link).</summary>
    Ritual,
    Spell,
    Trap,
    /// <summary>Master Duel <c>card_frame18</c> — Link face (hex pattern + arrow markers).</summary>
    Link,
    /// <summary>Master Duel <c>card_frame09</c> — Token face.</summary>
    Token,

    /// <summary>Master Duel <c>card_frame13</c> — Normal Pendulum.</summary>
    PendulumNormal,
    /// <summary>Master Duel <c>card_frame14</c> — Effect Pendulum.</summary>
    PendulumEffect,
    /// <summary>Master Duel <c>card_frame17</c> — Fusion Pendulum.</summary>
    PendulumFusion,
    /// <summary>Master Duel <c>card_frame16</c> — Synchro Pendulum.</summary>
    PendulumSynchro,
    /// <summary>Master Duel <c>card_frame15</c> — Xyz Pendulum.</summary>
    PendulumXyz,
    /// <summary>Master Duel <c>card_frame19</c> — Ritual Pendulum (Ritual-blue chrome + pendulum scales).</summary>
    PendulumRitual,
    /// <summary>
    /// Legacy alias for <see cref="PendulumRitual"/> (same <c>card_frame19</c> asset).
    /// MD has no separate Token Pendulum face; kept so existing Tag/tests keep working.
    /// </summary>
    PendulumToken,

    // --- OF Gradient (premium outer border) presets — derived from solid frames above ---

    /// <summary>OF premium outer rim on Effect chrome (pale blue / white light shafts).</summary>
    OfGradientEffect,
    /// <summary>OF premium outer rim on Normal chrome (warm gold glow).</summary>
    OfGradientNormal,
    /// <summary>OF premium outer rim on Fusion chrome (violet light leak).</summary>
    OfGradientFusion,
    /// <summary>OF premium outer rim on Synchro chrome (silver / white glow).</summary>
    OfGradientSynchro,
    /// <summary>OF premium outer rim on Xyz chrome (dark + cyan shafts).</summary>
    OfGradientXyz,
    /// <summary>OF premium outer rim on Ritual chrome (cyan / teal corner glow).</summary>
    OfGradientRitual,
    /// <summary>OF premium outer rim on Spell chrome (teal iridescent + holo grid).</summary>
    OfGradientSpell,
    /// <summary>OF premium outer rim on Trap chrome (magenta light leak).</summary>
    OfGradientTrap,
    /// <summary>OF premium outer rim on Link chrome (cyan tech glow + holo grid).</summary>
    OfGradientLink,
    /// <summary>OF premium outer rim on Token chrome.</summary>
    OfGradientToken,

    OfGradientPendulumNormal,
    OfGradientPendulumEffect,
    OfGradientPendulumFusion,
    OfGradientPendulumSynchro,
    OfGradientPendulumXyz,
    OfGradientPendulumRitual,
    OfGradientPendulumToken,
}

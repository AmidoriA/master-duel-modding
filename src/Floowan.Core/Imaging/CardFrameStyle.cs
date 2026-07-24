namespace Floowan.Core.Imaging;

/// <summary>
/// Master Duel card-face styles used when baking a frame-with-hole into over-frame art.
/// Texture templates are the game's <c>card_frame*</c> 704×1024 assets (art window already A=0).
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
    PendulumToken
}

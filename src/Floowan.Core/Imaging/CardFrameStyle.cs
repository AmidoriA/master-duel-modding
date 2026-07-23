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
    Ritual,
    Spell,
    Trap,
    Link,
    /// <summary>Master Duel <c>card_frame14</c> — Effect Pendulum face (taller-illust / shorter hole).</summary>
    Pendulum
}

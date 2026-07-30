namespace Floowan.Core.Services;

/// <summary>
/// Pure classification for cards the app treats as over-frame (live 704×1024)
/// but whose user.db / of_card_asset records are missing or incomplete.
/// </summary>
public static class OverFrameOrphanDetector
{
    public enum FixKind
    {
        None,
        /// <summary>Live OF already in gate; only sync <c>is_overframe</c> (official or unsynced).</summary>
        DbFlagFromGate,
        /// <summary>Live OF already gated; backfill Floowan apply memory only.</summary>
        FloowanMemoryOnly,
        /// <summary>Live OF missing from gate (and usually DB); persist like EnableGateOnly / Apply.</summary>
        GateAndFloowanDb,
        /// <summary>Live OF in gate but incomplete Floowan record — set Floowan flags without gate rewrite.</summary>
        FloowanDbInGate
    }

    /// <summary>
    /// True when the UI would treat the live texture as an over-frame canvas
    /// (704×1024), independent of the user.db flag.
    /// </summary>
    public static bool IsRenderedAsOverframe(int liveWidth, int liveHeight) =>
        liveWidth == Assets.OverFrameConstants.Width &&
        liveHeight == Assets.OverFrameConstants.Height;

    /// <summary>
    /// Decide whether a live OF-sized card needs user.db and/or gate repair.
    /// </summary>
    /// <param name="liveIsOverframeSize">Live Texture2D is 704×1024.</param>
    /// <param name="dbIsOverframe"><c>user.card_state.is_overframe</c>.</param>
    /// <param name="dbFloowanOverframe"><c>user.card_state.floowan_overframe</c>.</param>
    /// <param name="inGate">Art id present in live <c>of_card_asset</c>.</param>
    /// <param name="hasFloowanEvidence">
    /// Applied OF backup, editable <c>of_edit_layer</c>, or similar Floowan-only signal.
    /// Used so official Konami OF rows are not marked <c>floowan_overframe</c>.
    /// </param>
    public static FixKind Classify(
        bool liveIsOverframeSize,
        bool dbIsOverframe,
        bool dbFloowanOverframe,
        bool inGate,
        bool hasFloowanEvidence)
    {
        if (!liveIsOverframeSize)
            return FixKind.None;

        if (!inGate)
            return FixKind.GateAndFloowanDb;

        if (!dbIsOverframe)
        {
            return hasFloowanEvidence
                ? FixKind.FloowanDbInGate
                : FixKind.DbFlagFromGate;
        }

        if (!dbFloowanOverframe && hasFloowanEvidence)
            return FixKind.FloowanMemoryOnly;

        return FixKind.None;
    }
}

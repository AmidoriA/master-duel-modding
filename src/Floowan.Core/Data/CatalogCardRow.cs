namespace Floowan.Core.Data;

/// <summary>
/// One master-catalog row produced from Master Duel game data.
/// </summary>
public sealed class CatalogCardRow
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Bundle { get; init; }
    public required int DataIndex { get; init; }
    /// <summary>Optional type label (Effect, Spell, Link, …).</summary>
    public string? CardType { get; init; }
    /// <summary>
    /// UTC creation timestamp of the card illustration AssetBundle file used for this row
    /// (LocalData/0000/… or StreamingAssets/AssetBundle/…). Stored as ISO-8601.
    /// </summary>
    public string? CreatedAt { get; init; }
    /// <summary>
    /// Link arrow bitmask from CARD_Prop (null for non-Link / unknown). Stored as INTEGER 0–255.
    /// </summary>
    public LinkMarkerMask? LinkMarkers { get; init; }
}

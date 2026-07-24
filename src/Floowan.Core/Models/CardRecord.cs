namespace Floowan.Core.Models;

public sealed class CardRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Bundle { get; init; } = "";
    public string? ModdedName { get; init; }
    public string? ModdedDescription { get; init; }
    public int DataIndex { get; init; }
    /// <summary>Catalog card type label (Effect, Spell, Link, …), when known.</summary>
    public string? CardType { get; init; }
    /// <summary>
    /// ISO-8601 UTC creation time of the illustration AssetBundle used for this catalog row.
    /// </summary>
    public string? CreatedAt { get; init; }
    /// <summary>Human-readable local display of <see cref="CreatedAt"/> for UI grids/details.</summary>
    public string CreatedAtDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CreatedAt))
                return "";
            if (!DateTimeOffset.TryParse(CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
                return CreatedAt;
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
    public bool Favorite { get; init; }
    public bool HasBackup { get; init; }
    public bool IsOverframe { get; init; }
    public int? OverframeBaseId { get; init; }
    /// <summary>Master Duel art id (Texture2D m_Name), when known.</summary>
    public int? ArtId { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(ModdedName) ? Name : ModdedName!;
}

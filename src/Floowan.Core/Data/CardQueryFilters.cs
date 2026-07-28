namespace Floowan.Core.Data;

/// <summary>
/// Parameterized filters for the Database tab browser.
/// Queries are always limited; do not load the full card table.
/// </summary>
public sealed class CardQueryFilters
{
    public int? CardId { get; init; }
    public string? NameContains { get; init; }
    public string? DescriptionContains { get; init; }
    public bool? Favorite { get; init; }
    public bool? HasBackup { get; init; }
    public bool? HasModdedName { get; init; }
    public bool? HasModdedDescription { get; init; }
    public int Limit { get; init; } = 100;
    public int Offset { get; init; } = 0;

    /// <summary>Column used for SQL ORDER BY before LIMIT/OFFSET (full filtered set, not page-only).</summary>
    public CardSortColumn SortBy { get; init; } = CardSortColumn.Id;

    /// <summary>When true, sort descending; otherwise ascending.</summary>
    public bool SortDescending { get; init; }
}

namespace Floowan.Core.Data;

/// <summary>
/// Columns that <see cref="CardDatabase.QueryCards"/> can ORDER BY across the full filtered set
/// before LIMIT/OFFSET pagination.
/// </summary>
public enum CardSortColumn
{
    Id = 0,
    Name,
    Description,
    Bundle,
    DataIndex,
    CardType,
    CreatedAt,
    ModdedName,
    ModdedDescription,
    Favorite,
    HasBackup
}

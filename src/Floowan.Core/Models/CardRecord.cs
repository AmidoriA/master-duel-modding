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
    public bool Favorite { get; init; }
    public bool HasBackup { get; init; }
    public bool IsOverframe { get; init; }
    public int? OverframeBaseId { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(ModdedName) ? Name : ModdedName!;
}

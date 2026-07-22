namespace Floowan.Core.Assets;

/// <summary>
/// Master Duel card-art identifiers are the Texture2D <c>m_Name</c> values
/// (numeric strings such as "22811"). These are what <c>of_card_asset</c> stores —
/// not Floowandereeze's autoincrement <c>card.id</c>.
/// </summary>
public static class CardArtId
{
    public static bool TryParse(string? textureName, out int artId)
    {
        artId = 0;
        if (string.IsNullOrWhiteSpace(textureName))
            return false;
        if (!int.TryParse(textureName.Trim(), out artId))
            return false;
        return artId is > 0 and <= ushort.MaxValue;
    }

    public static int ParseRequired(string? textureName)
    {
        if (!TryParse(textureName, out var artId))
        {
            throw new InvalidOperationException(
                $"Card texture name '{textureName}' is not a Master Duel art id (expected a positive 16-bit integer).");
        }

        return artId;
    }
}

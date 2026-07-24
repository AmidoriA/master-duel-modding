namespace Floowan.Core.Data;

/// <summary>
/// High-level “refresh master catalog from game” orchestration used by the Tools UI.
/// Updates the opened <c>database.db</c> in place; does not touch <c>user.db</c> card_state.
/// </summary>
public sealed class CardCatalogUpdater
{
    private readonly CardCatalogExtractor _extractor;

    public CardCatalogUpdater(string? classDataPath = null)
    {
        _extractor = new CardCatalogExtractor(classDataPath);
    }

    public CardCatalogUpdater(CardCatalogExtractor extractor)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    /// <summary>
    /// Scans <paramref name="playerDataPath"/> and replaces master <c>card</c> rows in
    /// <paramref name="database"/>. Ensures <c>card_type</c> / <c>created_at</c> columns exist.
    /// </summary>
    public CardCatalogUpdateResult UpdateFromGame(
        CardDatabase database,
        string playerDataPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        var extract = _extractor.Extract(playerDataPath, progress, cancellationToken);
        if (!extract.Success)
            return CardCatalogUpdateResult.Fail(extract.Message);

        progress?.Report($"Writing {extract.Rows.Count} cards to master catalog…");
        var written = database.ReplaceMasterCatalog(extract.Rows);
        progress?.Report($"Catalog updated: {written} cards.");
        return CardCatalogUpdateResult.Ok(written, extract.CryptoKey, extract.IllustCount, extract.BundlesScanned);
    }
}

public sealed class CardCatalogUpdateResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int CardsWritten { get; init; }
    public int? CryptoKey { get; init; }
    public int IllustCount { get; init; }
    public int BundlesScanned { get; init; }

    public static CardCatalogUpdateResult Ok(int cardsWritten, int? cryptoKey, int illustCount, int bundlesScanned) =>
        new()
        {
            Success = true,
            Message = $"Updated master catalog with {cardsWritten} cards.",
            CardsWritten = cardsWritten,
            CryptoKey = cryptoKey,
            IllustCount = illustCount,
            BundlesScanned = bundlesScanned
        };

    public static CardCatalogUpdateResult Fail(string message) =>
        new() { Success = false, Message = message };
}

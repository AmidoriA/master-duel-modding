namespace Floowan.Core.Data;

/// <summary>
/// High-level "refresh master catalog from game" orchestration used by the Tools UI.
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
    /// Full rebuild: scans all AssetBundles and replaces master <c>card</c> rows in
    /// <paramref name="database"/>. Ensures <c>card_type</c> / <c>created_at</c> /
    /// <c>link_markers</c> columns exist.
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
        return CardCatalogUpdateResult.Ok(
            written,
            extract.CryptoKey,
            extract.IllustCount,
            extract.BundlesScanned,
            incremental: false);
    }

    /// <summary>
    /// Incremental update: skips every AssetBundle whose
    /// <see cref="File.GetCreationTimeUtc"/> is strictly after
    /// <see cref="CardDatabase.GetLatestCreatedAtUtc"/> (DB <c>MAX(created_at)</c>)
    /// before any AssetsTools open. Upserts matching rows; does not delete existing catalog cards.
    /// </summary>
    public CardCatalogUpdateResult UpdateNewFilesOnly(
        CardDatabase database,
        string playerDataPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var latest = database.GetLatestCreatedAtUtc();
        if (latest is null)
        {
            return CardCatalogUpdateResult.Fail(
                "No created_at values in the catalog yet. Run ""Update entire DB"" once first.");
        }

        progress?.Report(
            $"Cutoff: illustration File.GetCreationTimeUtc after DB MAX(created_at) = {latest:o}");

        var extract = _extractor.Extract(
            playerDataPath,
            progress,
            cancellationToken,
            illustCreatedAfterUtc: latest);
        if (!extract.Success)
            return CardCatalogUpdateResult.Fail(extract.Message);

        if (extract.Rows.Count == 0)
        {
            progress?.Report("No new cards to upsert.");
            return CardCatalogUpdateResult.Ok(
                0,
                extract.CryptoKey,
                extract.IllustCount,
                extract.BundlesScanned,
                incremental: true,
                message: "No new illustration files after the latest created_at; catalog unchanged.");
        }

        progress?.Report($"Upserting {extract.Rows.Count} new/updated cards…");
        var written = database.UpsertMasterCatalog(extract.Rows);
        progress?.Report($"Catalog upserted: {written} cards.");
        return CardCatalogUpdateResult.Ok(
            written,
            extract.CryptoKey,
            extract.IllustCount,
            extract.BundlesScanned,
            incremental: true);
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
    public bool Incremental { get; init; }

    public static CardCatalogUpdateResult Ok(
        int cardsWritten,
        int? cryptoKey,
        int illustCount,
        int bundlesScanned,
        bool incremental = false,
        string? message = null) =>
        new()
        {
            Success = true,
            Message = message ?? (incremental
                ? $"Upserted {cardsWritten} new/updated cards."
                : $"Updated master catalog with {cardsWritten} cards."),
            CardsWritten = cardsWritten,
            CryptoKey = cryptoKey,
            IllustCount = illustCount,
            BundlesScanned = bundlesScanned,
            Incremental = incremental
        };

    public static CardCatalogUpdateResult Fail(string message) =>
        new() { Success = false, Message = message };
}
using Floowan.Core.Data;
using Floowan.Core.Imaging;
using Floowan.Core.Models;

namespace Floowan.Core.Services;

/// <summary>
/// Resolves over-frame <see cref="CardFrameStyle"/> preferring DB <c>card_type</c>,
/// with optional single-card CARD_Prop backfill when the column is empty.
/// </summary>
public sealed class CardFrameStyleResolver
{
    private readonly Func<string, IProgress<string>?, CancellationToken, IReadOnlyDictionary<int, string?>> _loadPropTypes;
    private readonly object _cacheGate = new();
    private string? _cachedPath;
    private IReadOnlyDictionary<int, string?>? _cachedPropTypes;

    public CardFrameStyleResolver(string? classDataPath = null)
        : this(CreateDefaultLoader(classDataPath))
    {
    }

    public CardFrameStyleResolver(CardCatalogExtractor extractor)
        : this(CreateLoader(extractor ?? throw new ArgumentNullException(nameof(extractor))))
    {
    }

    /// <summary>Test seam: inject a CARD_Prop id→label map loader.</summary>
    public CardFrameStyleResolver(
        Func<string, IProgress<string>?, CancellationToken, IReadOnlyDictionary<int, string?>> loadPropTypes)
    {
        _loadPropTypes = loadPropTypes ?? throw new ArgumentNullException(nameof(loadPropTypes));
    }

    /// <summary>
    /// Prefer stored <see cref="CardRecord.CardType"/>; when missing, read CARD_Prop for
    /// <paramref name="card"/>.Id (cached per LocalData path), upsert <c>card_type</c>, then map.
    /// Falls back to description-text <see cref="CardFrameTemplates.InferStyle"/> when game data
    /// is unavailable or yields no label. Old DBs without a <c>card_type</c> column skip upsert.
    /// </summary>
    public CardFrameStyle? Resolve(
        CardRecord card,
        CardDatabase? database = null,
        string? playerDataPath = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (CardTypeLabels.TryParseStyle(card.CardType, out var fromDb))
            return fromDb;

        // Missing or unrecognised card_type → try CARD_Prop backfill when a game path is available.
        if (!string.IsNullOrWhiteSpace(playerDataPath))
        {
            var label = TryResolveFromGame(
                playerDataPath!,
                card.Id,
                card.Name,
                card.Description,
                progress,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(label))
            {
                TryPersistCardType(database, card.Id, label);
                if (CardTypeLabels.TryParseStyle(label, out var fromGame))
                    return fromGame;
            }
        }

        return CardFrameTemplates.InferStyle(card.Name, card.Description);
    }

    /// <summary>
    /// Clears the in-memory CARD_Prop type map (e.g. after a Tools catalog rebuild).
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cachedPath = null;
            _cachedPropTypes = null;
        }
    }

    private string? TryResolveFromGame(
        string playerDataPath,
        int cardId,
        string? name,
        string? description,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var map = EnsurePropTypeMap(playerDataPath, progress, cancellationToken);
            if (map.TryGetValue(cardId, out var fromProp) && !string.IsNullOrWhiteSpace(fromProp))
                return fromProp;

            return CardTypeLabels.InferFromCardText(name, description);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Game path / AssetsTools failures → caller falls back to description InferStyle.
            return CardTypeLabels.InferFromCardText(name, description);
        }
    }

    private IReadOnlyDictionary<int, string?> EnsurePropTypeMap(
        string playerDataPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        lock (_cacheGate)
        {
            if (_cachedPropTypes is not null
                && string.Equals(_cachedPath, playerDataPath, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedPropTypes;
            }
        }

        var loaded = _loadPropTypes(playerDataPath, progress, cancellationToken);

        lock (_cacheGate)
        {
            _cachedPath = playerDataPath;
            _cachedPropTypes = loaded;
            return _cachedPropTypes;
        }
    }

    private static void TryPersistCardType(CardDatabase? database, int cardId, string label)
    {
        if (database is null)
            return;

        try
        {
            database.UpdateCardType(cardId, label);
        }
        catch
        {
            // Read-only DB or missing column — prediction still proceeds from the resolved label.
        }
    }

    private static Func<string, IProgress<string>?, CancellationToken, IReadOnlyDictionary<int, string?>>
        CreateDefaultLoader(string? classDataPath) =>
        CreateLoader(new CardCatalogExtractor(classDataPath));

    private static Func<string, IProgress<string>?, CancellationToken, IReadOnlyDictionary<int, string?>>
        CreateLoader(CardCatalogExtractor extractor) =>
        (path, progress, ct) => extractor.LoadCardPropTypeMap(path, progress, ct);
}

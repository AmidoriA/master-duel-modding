using System.Text;

namespace Floowan.Core.Data;

/// <summary>
/// Parses decrypted Master Duel CARD_Indx / CARD_Name / CARD_Desc / CARD_Prop binaries
/// (same layout as the Floowandereeze ETL decode scripts).
/// </summary>
public static class CardDataFilesParser
{
    /// <summary>
    /// CARD_Prop records are 8 bytes; card id is the little-endian UInt16 at offset 0 of each record.
    /// Parsing starts at byte 8 (ETL skips the first record / header).
    /// </summary>
    public static IReadOnlyList<int> ParseCardIds(ReadOnlySpan<byte> decryptedProp)
    {
        var ids = new List<int>();
        for (var i = 8; i + 1 < decryptedProp.Length; i += 8)
        {
            var id = decryptedProp[i] | (decryptedProp[i + 1] << 8);
            ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// Splits CARD_Name (indexStart=0) or CARD_Desc (indexStart=4) using CARD_Indx offsets.
    /// </summary>
    public static IReadOnlyList<string> SplitIndexedStrings(
        ReadOnlySpan<byte> decryptedIndx,
        ReadOnlySpan<byte> decryptedPayload,
        int indexStart)
    {
        if (indexStart is not (0 or 4))
            throw new ArgumentOutOfRangeException(nameof(indexStart), "indexStart must be 0 (names) or 4 (descriptions).");

        var offsets = new List<int>();
        for (var i = indexStart; i + 3 < decryptedIndx.Length; i += 8)
        {
            var offset =
                decryptedIndx[i]
                | (decryptedIndx[i + 1] << 8)
                | (decryptedIndx[i + 2] << 16)
                | (decryptedIndx[i + 3] << 24);
            offsets.Add(offset);
        }

        // ETL drops the first index entry.
        if (offsets.Count > 0)
            offsets.RemoveAt(0);

        var results = new List<string>(Math.Max(0, offsets.Count - 1));
        for (var i = 0; i < offsets.Count - 1; i++)
        {
            var start = offsets[i];
            var end = offsets[i + 1];
            if (start < 0 || end < start || end > decryptedPayload.Length)
            {
                results.Add("");
                continue;
            }

            var slice = decryptedPayload[start..end];
            var text = Encoding.UTF8.GetString(slice);
            results.Add(TrimNulls(text));
        }

        return results;
    }

    /// <summary>
    /// Builds card-id → (description, name, data_index) the same way the ETL zips CARD_Prop IDs
    /// with decrypted names/descriptions. Drops the 30000–30099 duplicate id band.
    /// </summary>
    public static IReadOnlyDictionary<int, CardIdentity> BuildIdentityMap(
        IReadOnlyList<int> cardIds,
        IReadOnlyList<string> names,
        IReadOnlyList<string> descriptions)
    {
        var count = Math.Min(cardIds.Count, Math.Min(names.Count, descriptions.Count));
        var map = new Dictionary<int, CardIdentity>(count);
        for (var i = 0; i < count; i++)
        {
            var id = cardIds[i];
            if (id is >= 30000 and < 30100)
                continue;
            map[id] = new CardIdentity(descriptions[i], names[i], i);
        }

        return map;
    }

    /// <summary>
    /// Appends <c>(alt N)</c> suffixes to duplicate names (ETL <c>add_suffix</c>),
    /// processing from last to first so the chronologically first keep the bare name.
    /// </summary>
    public static IReadOnlyList<string> AddAltSuffixes(IReadOnlyList<string> names)
    {
        var reversed = names.Reverse().ToList();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var updated = new List<string>(reversed.Count);
        foreach (var name in reversed)
        {
            if (counts.TryGetValue(name, out var count))
            {
                counts[name] = count + 1;
                updated.Add($"{name} (alt {count})");
            }
            else
            {
                counts[name] = 1;
                updated.Add(name);
            }
        }

        updated.Reverse();
        return updated;
    }

    /// <summary>
    /// Strips a trailing <c> (alt N)</c> suffix from a catalog name, if present.
    /// Does not alter distinct titles that merely contain the same words
    /// (e.g. <c>Aleister the Invoker of Madness</c>).
    /// </summary>
    public static string StripAltArtSuffix(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var trimmed = name.Trim();
        var open = trimmed.LastIndexOf(" (alt ", StringComparison.OrdinalIgnoreCase);
        if (open < 0 || !trimmed.EndsWith(')'))
            return trimmed;

        var inner = trimmed[(open + " (alt ".Length)..^1].Trim();
        if (inner.Length == 0 || !inner.All(char.IsDigit))
            return trimmed;

        return trimmed[..open].Trim();
    }

    private static string TrimNulls(string s)
    {
        var end = s.Length;
        while (end > 0 && s[end - 1] == '\0')
            end--;
        return end == s.Length ? s : s[..end];
    }

    public readonly record struct CardIdentity(string Description, string Name, int DataIndex);
}

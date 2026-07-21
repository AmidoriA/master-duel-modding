namespace Floowan.Core.Assets;

/// <summary>
/// Parses and edits Master Duel <c>of_card_asset</c> gate payloads:
/// little-endian ushort pairs <c>(triggerId, baseArtId)</c>.
/// Supports raw payload-only bytes and a simple UABEA-style 4-byte length prefix.
/// </summary>
public sealed class OfCardAssetGate
{
    public enum PayloadFormat
    {
        /// <summary>Raw sequence of LE ushort pairs (length % 4 == 0).</summary>
        RawPairs,
        /// <summary>4-byte LE pair count, then pairs (common dump wrapper).</summary>
        CountPrefixed,
        /// <summary>4-byte LE byte-length, then pairs.</summary>
        ByteLengthPrefixed
    }

    private readonly List<(ushort TriggerId, ushort BaseArtId)> _entries = new();

    public PayloadFormat Format { get; private set; } = PayloadFormat.RawPairs;

    public IReadOnlyList<(ushort TriggerId, ushort BaseArtId)> Entries => _entries;

    public static OfCardAssetGate Parse(ReadOnlySpan<byte> data)
    {
        var gate = new OfCardAssetGate();
        if (data.IsEmpty)
        {
            gate.Format = PayloadFormat.RawPairs;
            return gate;
        }

        // Prefer UABEA-style wrappers when the header math is consistent, then raw pairs.
        if (data.Length >= 4)
        {
            var prefix = BitConverter.ToUInt32(data);
            var body = data[4..];

            if (prefix <= int.MaxValue / 4
                && body.Length == (int)prefix * 4
                && body.Length % 4 == 0
                && TryParseRaw(body, gate._entries))
            {
                gate.Format = PayloadFormat.CountPrefixed;
                return gate;
            }

            gate._entries.Clear();

            if (prefix == (uint)body.Length
                && body.Length % 4 == 0
                && TryParseRaw(body, gate._entries))
            {
                gate.Format = PayloadFormat.ByteLengthPrefixed;
                return gate;
            }

            gate._entries.Clear();
        }

        if (TryParseRaw(data, gate._entries))
        {
            gate.Format = PayloadFormat.RawPairs;
            return gate;
        }

        throw new InvalidDataException(
            "of_card_asset payload is not a valid LE ushort pair list (raw or UABEA-wrapped).");
    }

    public static OfCardAssetGate FromEntries(
        IEnumerable<(ushort TriggerId, ushort BaseArtId)> entries,
        PayloadFormat format = PayloadFormat.RawPairs)
    {
        var gate = new OfCardAssetGate { Format = format };
        gate._entries.AddRange(entries);
        return gate;
    }

    public bool Contains(ushort triggerId) =>
        _entries.Any(e => e.TriggerId == triggerId);

    public bool Contains(int triggerId) =>
        triggerId is >= 0 and <= ushort.MaxValue && Contains((ushort)triggerId);

    public void Add(ushort triggerId, ushort baseArtId)
    {
        var idx = _entries.FindIndex(e => e.TriggerId == triggerId);
        if (idx >= 0)
            _entries[idx] = (triggerId, baseArtId);
        else
            _entries.Add((triggerId, baseArtId));
    }

    public void Add(int triggerId, int baseArtId)
    {
        if (triggerId is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(triggerId));
        if (baseArtId is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(baseArtId));
        Add((ushort)triggerId, (ushort)baseArtId);
    }

    public bool Remove(ushort triggerId)
    {
        var removed = _entries.RemoveAll(e => e.TriggerId == triggerId);
        return removed > 0;
    }

    public bool Remove(int triggerId) =>
        triggerId is >= 0 and <= ushort.MaxValue && Remove((ushort)triggerId);

    public IReadOnlyList<(ushort TriggerId, ushort BaseArtId)> ListEntries() =>
        _entries.ToList();

    public byte[] ToBytes()
    {
        var pairs = new byte[_entries.Count * 4];
        for (var i = 0; i < _entries.Count; i++)
        {
            var (trigger, baseArt) = _entries[i];
            BitConverter.TryWriteBytes(pairs.AsSpan(i * 4, 2), trigger);
            BitConverter.TryWriteBytes(pairs.AsSpan(i * 4 + 2, 2), baseArt);
        }

        return Format switch
        {
            PayloadFormat.RawPairs => pairs,
            PayloadFormat.CountPrefixed => Prefixed((uint)_entries.Count, pairs),
            PayloadFormat.ByteLengthPrefixed => Prefixed((uint)pairs.Length, pairs),
            _ => pairs
        };
    }

    private static byte[] Prefixed(uint prefix, byte[] body)
    {
        var result = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0, 4), prefix);
        body.CopyTo(result, 4);
        return result;
    }

    private static bool TryParseRaw(ReadOnlySpan<byte> data, List<(ushort, ushort)> into)
    {
        into.Clear();
        if (data.Length % 4 != 0)
            return false;

        for (var i = 0; i < data.Length; i += 4)
        {
            var trigger = BitConverter.ToUInt16(data[i..]);
            var baseArt = BitConverter.ToUInt16(data[(i + 2)..]);
            into.Add((trigger, baseArt));
        }

        return true;
    }
}

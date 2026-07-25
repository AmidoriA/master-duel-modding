using Floowan.Core.Assets;

namespace Floowan.Core.Tests;

public class OfCardAssetGateTests
{
    [Fact]
    public void Parse_RawPair_Example20570()
    {
        // id 20570 = 0x505A → LE bytes 5A 50; pair (20570,20570) => 5A 50 5A 50
        byte[] raw = [0x5A, 0x50, 0x5A, 0x50];
        var gate = OfCardAssetGate.Parse(raw);

        Assert.Equal(OfCardAssetGate.PayloadFormat.RawPairs, gate.Format);
        Assert.True(gate.Contains(20570));
        Assert.Single(gate.Entries);
        Assert.Equal((ushort)20570, gate.Entries[0].TriggerId);
        Assert.Equal((ushort)20570, gate.Entries[0].BaseArtId);
        Assert.Equal(raw, gate.ToBytes());
    }

    [Fact]
    public void Add_And_Remove_RoundTrip()
    {
        var gate = OfCardAssetGate.Parse(ReadOnlySpan<byte>.Empty);
        Assert.False(gate.Contains(100));

        gate.Add(100, 100);
        gate.Add(200, 150);
        Assert.True(gate.Contains(100));
        Assert.True(gate.Contains(200));
        Assert.Equal(2, gate.ListEntries().Count);

        // overwrite same trigger
        gate.Add(100, 99);
        Assert.Equal((ushort)99, gate.Entries.First(e => e.TriggerId == 100).BaseArtId);

        Assert.True(gate.Remove(200));
        Assert.False(gate.Contains(200));
        Assert.True(gate.Remove(100));
        Assert.Empty(gate.ListEntries());
        Assert.Empty(gate.ToBytes());
    }

    [Fact]
    public void Parse_PrefersRawPairs_OverAmbiguousCountPrefix()
    {
        // Bytes that are BOTH valid raw pairs [(1,0),(18472,18472)] AND count-prefixed [1×(18472,18472)].
        // Live Master Duel uses raw pairs — must keep both entries.
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 2), (ushort)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(2, 2), (ushort)0);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 2), (ushort)18472);
        BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), (ushort)18472);

        var gate = OfCardAssetGate.Parse(bytes);
        Assert.Equal(OfCardAssetGate.PayloadFormat.RawPairs, gate.Format);
        Assert.Equal(2, gate.Entries.Count);
        Assert.True(gate.Contains(1));
        Assert.True(gate.Contains(18472));
    }

    [Fact]
    public void FromEntries_CountPrefixed_RoundTrip()
    {
        var gate = OfCardAssetGate.FromEntries(
            [(20570, 20570)],
            OfCardAssetGate.PayloadFormat.CountPrefixed);
        var bytes = gate.ToBytes();
        Assert.Equal(8, bytes.Length);
        Assert.Equal(1u, BitConverter.ToUInt32(bytes));

        // Auto-parse prefers raw (ambiguous); explicit format is preserved on ToBytes only.
        var asRaw = OfCardAssetGate.Parse(bytes);
        Assert.Equal(OfCardAssetGate.PayloadFormat.RawPairs, asRaw.Format);
        Assert.Equal(2, asRaw.Entries.Count);
    }

    [Fact]
    public void FromEntries_ByteLengthPrefixed_RoundTrip()
    {
        byte[] twoPairs =
        [
            0x5A, 0x50, 0x5A, 0x50,
            0x01, 0x00, 0x01, 0x00
        ];
        var gate = OfCardAssetGate.FromEntries(
            [(20570, 20570), (1, 1)],
            OfCardAssetGate.PayloadFormat.ByteLengthPrefixed);
        var bytes = gate.ToBytes();
        Assert.Equal(4 + twoPairs.Length, bytes.Length);
        Assert.Equal((uint)twoPairs.Length, BitConverter.ToUInt32(bytes));
        Assert.Equal(twoPairs, bytes.AsSpan(4).ToArray());
    }

    [Fact]
    public void Parse_Rejects_InvalidLength()
    {
        Assert.Throws<InvalidDataException>(() => OfCardAssetGate.Parse(new byte[] { 0x01, 0x02 }));
    }

    [Fact]
    public void Add_Merges_WithoutDroppingExistingOfficialEntries()
    {
        // Simulate post-patch gate that only has official OF rows, then Floowan appends.
        var gate = OfCardAssetGate.FromEntries([(100, 100), (200, 200)]);
        gate.Add(300, 300);
        gate.Add(100, 100); // idempotent official refresh

        Assert.Equal(3, gate.Entries.Count);
        Assert.True(gate.Contains(100));
        Assert.True(gate.Contains(200));
        Assert.True(gate.Contains(300));
        Assert.Equal((ushort)100, gate.Entries.First(e => e.TriggerId == 100).BaseArtId);
    }
}

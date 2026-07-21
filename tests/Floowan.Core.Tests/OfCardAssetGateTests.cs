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
    public void Parse_CountPrefixed_And_ByteLengthPrefixed()
    {
        byte[] pairs = [0x5A, 0x50, 0x5A, 0x50];

        var countPrefixed = new byte[8];
        BitConverter.TryWriteBytes(countPrefixed.AsSpan(0, 4), 1u);
        pairs.CopyTo(countPrefixed, 4);
        var g1 = OfCardAssetGate.Parse(countPrefixed);
        Assert.Equal(OfCardAssetGate.PayloadFormat.CountPrefixed, g1.Format);
        Assert.True(g1.Contains(20570));
        Assert.Equal(countPrefixed, g1.ToBytes());

        var lenPrefixed = new byte[8];
        BitConverter.TryWriteBytes(lenPrefixed.AsSpan(0, 4), 4u);
        pairs.CopyTo(lenPrefixed, 4);
        // Ambiguous with count=4 when body isn't 16 bytes — use two pairs so count vs length differ.
        byte[] twoPairs =
        [
            0x5A, 0x50, 0x5A, 0x50,
            0x01, 0x00, 0x01, 0x00
        ];
        var lenPrefixed2 = new byte[4 + twoPairs.Length];
        BitConverter.TryWriteBytes(lenPrefixed2.AsSpan(0, 4), (uint)twoPairs.Length);
        twoPairs.CopyTo(lenPrefixed2, 4);
        var g2 = OfCardAssetGate.Parse(lenPrefixed2);
        Assert.Equal(OfCardAssetGate.PayloadFormat.ByteLengthPrefixed, g2.Format);
        Assert.Equal(2, g2.Entries.Count);
        Assert.Equal(lenPrefixed2, g2.ToBytes());
    }

    [Fact]
    public void Parse_Rejects_InvalidLength()
    {
        Assert.Throws<InvalidDataException>(() => OfCardAssetGate.Parse(new byte[] { 0x01, 0x02 }));
    }
}

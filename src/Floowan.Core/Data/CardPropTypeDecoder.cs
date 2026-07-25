namespace Floowan.Core.Data;

/// <summary>
/// Derives Floowan card-type labels from Master Duel <c>CARD_Prop</c> records.
/// Each prop record is 8 bytes; bytes 0–1 are the art/card id (LE). Byte 2 is a packed
/// monster/spell face field (empirical MD encoding, not YGOPro <c>TYPE_*</c> bitflags).
/// Byte 3 is a secondary field (spell/trap marker <c>0x02</c>, otherwise level/rank-ish data).
/// <para>
/// Special summon faces (Link / Xyz / Synchro / Fusion / Ritual and pendulum variants) are
/// matched by bit masks <b>before</b> the generic Effect fallback — exact-byte lists alone
/// miss most Synchro/Xyz/Ritual variants (e.g. Synchro <c>0x52</c>/<c>0xD2</c>/<c>0x53</c>).
/// </para>
/// </summary>
public static class CardPropTypeDecoder
{
    /// <summary>
    /// Reads id + type bytes from a decrypted CARD_Prop blob (same stride as the ETL).
    /// </summary>
    public static IReadOnlyList<CardPropEntry> ParseEntries(ReadOnlySpan<byte> decryptedProp)
    {
        var list = new List<CardPropEntry>();
        for (var i = 8; i + 7 < decryptedProp.Length; i += 8)
        {
            var id = decryptedProp[i] | (decryptedProp[i + 1] << 8);
            list.Add(new CardPropEntry(id, decryptedProp[i + 2], decryptedProp[i + 3]));
        }

        return list;
    }

    public static string? InferLabel(byte typeByte, byte typeByte2)
    {
        // Spells / traps use a stable secondary byte of 0x02.
        if (typeByte2 == 0x02)
        {
            if (typeByte == 0x0D)
                return "Spell";
            if (typeByte == 0x4E)
                return "Trap";
        }

        // Tokens (Sheep Token 0x4A, Kuriboh Token 0x8A, …).
        if (typeByte is 0x4A or 0x8A)
            return "Token";

        var low = (byte)(typeByte & 0x0F);

        // True Links set bit 0x20 with low nibble A/B (Accesscode 0xAB, Link Spider 0x6B, …).
        // Other bit-0x20 faces are Extra Deck pendulums (Synchro/Xyz/Ritual/…) — not Link.
        if ((typeByte & 0x20) != 0 && low is 0x0A or 0x0B)
            return "Link";

        // Extra Deck pendulum frames also set bit 0x20 with other low nibbles.
        if ((typeByte & 0x20) != 0)
        {
            var pendulumExtra = low switch
            {
                0x04 => "Synchro Pendulum", // Nirvana High Paladin 0xA4, Clear Wing Fast Dragon
                0x02 => "Xyz Pendulum",     // Odd-Eyes Rebellion 0xA2
                0x06 => "Ritual Pendulum",  // Shinobaron 0xA6
                0x09 => "Fusion Pendulum",  // Supreme King Z-ARC 0xA9
                0x01 or 0x08 => "Effect Pendulum",
                _ => null
            };
            if (pendulumExtra is not null)
                return pendulumExtra;
        }

        // Xyz: bit 0x10 + low nibble 6 (Normal Xyz) or 7 (Effect Xyz).
        // Covers 0x57/0x97 and previously missed 0x17/0xD7/0x56/0xD6.
        if ((typeByte & 0x10) != 0 && (typeByte & 0x07) is 0x06 or 0x07)
            return "Xyz";

        // Synchro: bit 0x10, not main-deck pendulum bit 0x08, low nibble 1/2/3.
        // Covers 0x92 and previously missed 0x52/0xD2/0x53/0x93/0x13/0xD3/0x51.
        // Note: 0x12 is plain Synchro here (Black Rose / Nitro Warrior); pendulum Synchros
        // that only use 0x12 may still need description fallback for the Pendulum label.
        if ((typeByte & 0x10) != 0 && (typeByte & 0x08) == 0 && (typeByte & 0x07) is 0x01 or 0x02 or 0x03)
            return "Synchro";

        // Fusion (0x42/0x43 classic; 0x83 fusion pendulum).
        if (typeByte is 0x42 or 0x43)
            return "Fusion";
        if (typeByte == 0x83)
            return "Fusion Pendulum";

        // Ritual: low nibble 4/5 without Extra-Deck bits 0x10/0x20.
        // Covers Relinquished 0x85 and previously missed 0x45/0xC5/0x05/0x44/0x84/….
        if ((typeByte & 0x30) == 0 && (typeByte & 0x07) is 0x04 or 0x05)
            return "Ritual";

        // Main-deck pendulum normals / effects.
        if (typeByte == 0x9A)
            return "Effect Pendulum";
        if (typeByte is 0x59 or 0xD9)
            return "Normal Pendulum";

        // Explicit normal monster face (Blue-Eyes 0x40).
        if (typeByte == 0x40)
            return "Normal";

        // Remaining monster faces → Effect (Dark Magician, Ash, Cyber Dragon, …).
        if (typeByte2 != 0x02)
            return "Effect";

        return null;
    }

    public readonly record struct CardPropEntry(int Id, byte TypeByte, byte TypeByte2);
}

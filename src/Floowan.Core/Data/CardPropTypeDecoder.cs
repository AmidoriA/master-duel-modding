namespace Floowan.Core.Data;

/// <summary>
/// Derives Floowan card-type labels from Master Duel <c>CARD_Prop</c> records.
/// Each prop record is 8 bytes; bytes 0–1 are the art/card id (LE). Bytes 2–3 encode
/// a packed type field observed across current MD builds (empirical; not YGOPro bitflags).
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

        // Link monsters set bit 0x20 in the type byte (Accesscode, Linkuriboh, …).
        if ((typeByte & 0x20) != 0)
            return "Link";

        // Tokens (Sheep Token 0x4A, Kuriboh Token 0x8A, …).
        if (typeByte is 0x4A or 0x8A)
            return "Token";

        // Synchro (incl. pendulum synchro variants like 0x12).
        if (typeByte == 0x92 || typeByte == 0x12)
            return typeByte == 0x12 ? "Synchro Pendulum" : "Synchro";

        // Xyz (0x57 / 0x97 common).
        if (typeByte is 0x57 or 0x97)
            return "Xyz";

        // Fusion (0x42/0x43 classic; 0x83 fusion pendulum / fusion effect hybrids).
        if (typeByte is 0x42 or 0x43)
            return "Fusion";
        if (typeByte == 0x83)
            return "Fusion Pendulum";

        // Ritual (Relinquished 0x85).
        if (typeByte == 0x85)
            return "Ritual";

        // Pendulum normals / effects (Odd-Eyes / Performapal / Qliphort Scout patterns).
        if (typeByte == 0x9A)
            return "Effect Pendulum";
        if (typeByte == 0x59)
            return "Normal Pendulum";
        if (typeByte == 0xD9)
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

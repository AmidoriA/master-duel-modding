namespace Floowan.Core.Data;

/// <summary>
/// Derives Floowan card-type labels from Master Duel <c>CARD_Prop</c> records.
/// Each prop record is 8 bytes; bytes 0–1 are the art/card id (LE). Byte 2 is a packed
/// monster/spell face field (empirical MD encoding, not YGOPro <c>TYPE_*</c> bitflags).
/// Byte 3 is a secondary field (spell/trap marker <see cref="SpellTrapSecondary"/>, otherwise level/rank-ish data).
/// <para>
/// Special summon faces (Link / Xyz / Synchro / Fusion / Ritual and pendulum variants) are
/// matched by bit masks <b>before</b> the generic Effect fallback — exact-byte lists alone
/// miss most Synchro/Xyz/Ritual variants (e.g. Synchro faces that share <see cref="SynchroXyzBit"/>).
/// </para>
/// </summary>
public static class CardPropTypeDecoder
{
    // --- Byte 3 (typeByte2) ---
    /// <summary>Secondary byte value that marks Spell / Trap faces.</summary>
    public const byte SpellTrapSecondary = 0x02;

    // --- Byte 2 bit masks ---
    /// <summary>Low nibble of the face byte.</summary>
    public const byte LowNibbleMask = 0x0F;
    /// <summary>Lowest three bits (family within Synchro / Xyz / Ritual).</summary>
    public const byte FamilyNibbleMask = 0x07;
    /// <summary>Shared Synchro / Xyz marker bit.</summary>
    public const byte SynchroXyzBit = 0x10;
    /// <summary>Main-deck pendulum marker (also used to exclude Synchro when set with SynchroXyzBit).</summary>
    public const byte MainDeckPendulumBit = 0x08;
    /// <summary>Link marker, and Extra Deck pendulum frame bit when low nibble is not a Link face.</summary>
    public const byte LinkOrExtraPendulumBit = 0x20;
    /// <summary>Combined Extra Deck bits (<see cref="SynchroXyzBit"/> | <see cref="LinkOrExtraPendulumBit"/>).</summary>
    public const byte ExtraDeckBitsMask = SynchroXyzBit | LinkOrExtraPendulumBit;

    // --- Spell / Trap exact faces ---
    public const byte SpellFace = 0x0D;
    public const byte TrapFace = 0x4E;

    // --- Token exact faces ---
    public const byte SheepTokenFace = 0x4A;
    public const byte KuribohTokenFace = 0x8A;

    // --- Link low nibbles (with LinkOrExtraPendulumBit) ---
    public const byte LinkNibbleA = 0x0A;
    public const byte LinkNibbleB = 0x0B;

    // --- Extra Deck pendulum low nibbles (with LinkOrExtraPendulumBit) ---
    public const byte SynchroPendulumNibble = 0x04;
    public const byte XyzPendulumNibble = 0x02;
    public const byte RitualPendulumNibble = 0x06;
    public const byte FusionPendulumNibble = 0x09;
    public const byte EffectPendulumNibbleA = 0x01;
    public const byte EffectPendulumNibbleB = 0x08;

    // --- Synchro / Xyz / Ritual family (lowest three bits) ---
    public const byte SynchroFamilyNormal = 0x01;
    public const byte SynchroFamilyEffect = 0x02;
    public const byte SynchroFamilyTuner = 0x03;
    public const byte XyzFamilyNormal = 0x06;
    public const byte XyzFamilyEffect = 0x07;
    public const byte RitualFamilyNormal = 0x04;
    public const byte RitualFamilyEffect = 0x05;

    // --- Fusion / pendulum / normal exact faces ---
    public const byte FusionFaceA = 0x42;
    public const byte FusionFaceB = 0x43;
    public const byte FusionPendulumFace = 0x83;
    public const byte EffectPendulumFace = 0x9A;
    public const byte NormalPendulumFaceA = 0x59;
    public const byte NormalPendulumFaceB = 0xD9;
    public const byte NormalMonsterFace = 0x40;

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
        // Spells / traps use a stable secondary byte.
        if (typeByte2 == SpellTrapSecondary)
        {
            if (typeByte == SpellFace)
                return "Spell";
            if (typeByte == TrapFace)
                return "Trap";
        }

        // Tokens (Sheep Token, Kuriboh Token, …).
        if (typeByte is SheepTokenFace or KuribohTokenFace)
            return "Token";

        var low = (byte)(typeByte & LowNibbleMask);

        // True Links set LinkOrExtraPendulumBit with low nibble A/B (Accesscode, Link Spider, …).
        // Other LinkOrExtraPendulumBit faces are Extra Deck pendulums (Synchro/Xyz/Ritual/…) — not Link.
        if ((typeByte & LinkOrExtraPendulumBit) != 0 && low is LinkNibbleA or LinkNibbleB)
            return "Link";

        // Extra Deck pendulum frames also set LinkOrExtraPendulumBit with other low nibbles.
        if ((typeByte & LinkOrExtraPendulumBit) != 0)
        {
            var pendulumExtra = low switch
            {
                SynchroPendulumNibble => "Synchro Pendulum", // Nirvana High Paladin, Clear Wing Fast Dragon
                XyzPendulumNibble => "Xyz Pendulum",         // Odd-Eyes Rebellion
                RitualPendulumNibble => "Ritual Pendulum",   // Shinobaron
                FusionPendulumNibble => "Fusion Pendulum",   // Supreme King Z-ARC
                EffectPendulumNibbleA or EffectPendulumNibbleB => "Effect Pendulum",
                _ => null
            };
            if (pendulumExtra is not null)
                return pendulumExtra;
        }

        // Xyz: SynchroXyzBit + family nibble Normal or Effect.
        if ((typeByte & SynchroXyzBit) != 0 && (typeByte & FamilyNibbleMask) is XyzFamilyNormal or XyzFamilyEffect)
            return "Xyz";

        // Synchro: SynchroXyzBit, not MainDeckPendulumBit, family nibble Normal/Effect/Tuner.
        // Note: face 0x12 is plain Synchro here (Black Rose / Nitro Warrior); pendulum Synchros
        // that only use that face may still need description fallback for the Pendulum label.
        if ((typeByte & SynchroXyzBit) != 0
            && (typeByte & MainDeckPendulumBit) == 0
            && (typeByte & FamilyNibbleMask) is SynchroFamilyNormal or SynchroFamilyEffect or SynchroFamilyTuner)
            return "Synchro";

        // Fusion (classic faces; separate Fusion Pendulum face).
        if (typeByte is FusionFaceA or FusionFaceB)
            return "Fusion";
        if (typeByte == FusionPendulumFace)
            return "Fusion Pendulum";

        // Ritual: family nibble Normal/Effect without Extra Deck bits.
        if ((typeByte & ExtraDeckBitsMask) == 0
            && (typeByte & FamilyNibbleMask) is RitualFamilyNormal or RitualFamilyEffect)
            return "Ritual";

        // Main-deck pendulum normals / effects.
        if (typeByte == EffectPendulumFace)
            return "Effect Pendulum";
        if (typeByte is NormalPendulumFaceA or NormalPendulumFaceB)
            return "Normal Pendulum";

        // Explicit normal monster face (Blue-Eyes).
        if (typeByte == NormalMonsterFace)
            return "Normal";

        // Remaining monster faces → Effect (Dark Magician, Ash, Cyber Dragon, …).
        if (typeByte2 != SpellTrapSecondary)
            return "Effect";

        return null;
    }

    public readonly record struct CardPropEntry(int Id, byte TypeByte, byte TypeByte2);
}

namespace Floowan.Core.Data;

/// <summary>
/// Derives Floowan card-type labels from Master Duel <c>CARD_Prop</c> records.
/// Each prop record is 8 bytes; bytes 0–1 are the art/card id (LE). Byte 2 is a packed
/// monster/spell face field (empirical MD encoding, not YGOPro <c>TYPE_*</c> bitflags).
/// Byte 3 is a secondary field (spell/trap marker <see cref="SpellTrapSecondary"/>, otherwise level/rank/link-rating-ish data).
/// Bytes 4–7 are a LE <c>uint32</c>: bits 0–8 = ATK/10, bits 9–17 = DEF/10 — or, for Link
/// faces, an 8-bit <see cref="LinkMarkerMask"/> in the DEF slot (see <see cref="DecodeLinkMarkers"/>).
/// <para>
/// Special summon faces (Link / Xyz / Synchro / Fusion / Ritual and pendulum variants) are
/// matched by bit masks <b>before</b> the generic Effect fallback — exact-byte lists alone
/// miss most Synchro/Xyz/Ritual/Token/Fusion variants.
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

    // --- Token faces (low nibble <see cref="TokenNibble"/>, no Link/Synchro bits) ---
    /// <summary>Token low nibble shared by Sheep / Kuriboh / Slime / Mirage / … (not Link).</summary>
    public const byte TokenNibble = 0x0A;
    public const byte MirageTokenFace = 0x0A;
    public const byte SheepTokenFace = 0x4A;
    public const byte KuribohTokenFace = 0x8A;
    public const byte SlimeTokenFace = 0xCA;

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

    // --- Fusion low nibbles (no LinkOrExtraPendulumBit, no SynchroXyzBit) ---
    /// <summary>Normal-looking Fusion face nibble (Blue-Eyes Ultimate, Flame Swordsman, …).</summary>
    public const byte FusionNibbleNormal = 0x02;
    /// <summary>Effect Fusion face nibble (Flame Wingman, Exceed, Neo Blue-Eyes, …).</summary>
    public const byte FusionNibbleEffect = 0x03;
    public const byte FusionFaceA = 0x42;
    public const byte FusionFaceB = 0x43;
    /// <summary>
    /// Legacy name: face <c>0x83</c> is a common Effect Fusion (Exceed, Flame Wingman),
    /// <b>not</b> Fusion Pendulum. True Fusion Pendulums use <see cref="FusionPendulumNibble"/>
    /// with <see cref="LinkOrExtraPendulumBit"/> (e.g. Z-ARC <c>0xA9</c>).
    /// </summary>
    public const byte FusionFaceC = 0x83;
    /// <summary>Blue-Eyes Toon Ultimate Dragon — Fusion that also sets Extra-Deck pendulum bits.</summary>
    public const byte FusionFaceToonUltimate = 0x78;

    // --- Main-deck pendulum / normal exact faces ---
    public const byte EffectPendulumFace = 0x9A;
    public const byte NormalPendulumFaceA = 0x59;
    public const byte NormalPendulumFaceB = 0xD9;
    public const byte NormalMonsterFace = 0x40;

    /// <summary>
    /// ATK and DEF are packed as 9-bit values in units of 10 into the LE uint32 at
    /// record bytes 4–7: bits 0–8 = ATK/10, bits 9–17 = DEF/10 (or Link markers).
    /// </summary>
    public const int AtkDefBitWidth = 9;
    public const int AtkDefUnit = 10;
    public const int AtkDefRawMask = (1 << AtkDefBitWidth) - 1; // 0x1FF

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

    /// <summary>
    /// Reads full 8-byte records including the packed ATK / DEF-or-link-marker field.
    /// </summary>
    public static IReadOnlyList<CardPropRecord> ParseRecords(ReadOnlySpan<byte> decryptedProp)
    {
        var list = new List<CardPropRecord>();
        for (var i = 8; i + 7 < decryptedProp.Length; i += 8)
        {
            var id = decryptedProp[i] | (decryptedProp[i + 1] << 8);
            var typeByte = decryptedProp[i + 2];
            var typeByte2 = decryptedProp[i + 3];
            var packed = (uint)(
                decryptedProp[i + 4]
                | (decryptedProp[i + 5] << 8)
                | (decryptedProp[i + 6] << 16)
                | (decryptedProp[i + 7] << 24));
            list.Add(new CardPropRecord(id, typeByte, typeByte2, packed));
        }

        return list;
    }

    /// <summary>ATK points (multiple of 10) from the packed field, or null when undecodable.</summary>
    public static int DecodeAtk(uint packedAtkDef) =>
        (int)(packedAtkDef & AtkDefRawMask) * AtkDefUnit;

    /// <summary>
    /// DEF points (multiple of 10) for non-Link faces. For Links this raw field is a
    /// <see cref="LinkMarkerMask"/> — use <see cref="DecodeLinkMarkers"/> instead.
    /// </summary>
    public static int DecodeDef(uint packedAtkDef) =>
        (int)((packedAtkDef >> AtkDefBitWidth) & AtkDefRawMask) * AtkDefUnit;

    /// <summary>
    /// Link arrow mask from the DEF slot (low 8 bits of the 9-bit DEF field).
    /// Only meaningful when <see cref="InferLabel"/> returns <c>Link</c>.
    /// </summary>
    public static LinkMarkerMask DecodeLinkMarkers(uint packedAtkDef) =>
        (LinkMarkerMask)(byte)((packedAtkDef >> AtkDefBitWidth) & 0xFF);

    /// <summary>
    /// Builds card-id → link-marker map for every record whose type bytes decode as Link.
    /// </summary>
    public static IReadOnlyDictionary<int, LinkMarkerMask> ParseLinkMarkerMap(
        ReadOnlySpan<byte> decryptedProp)
    {
        var map = new Dictionary<int, LinkMarkerMask>();
        foreach (var rec in ParseRecords(decryptedProp))
        {
            if (InferLabel(rec.TypeByte, rec.TypeByte2) != "Link")
                continue;
            map[rec.Id] = DecodeLinkMarkers(rec.PackedAtkDef);
        }

        return map;
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

        var low = (byte)(typeByte & LowNibbleMask);

        // Tokens: low nibble A without Link or Synchro/Xyz bits (0x0A / 0x4A / 0x8A / 0xCA).
        // Effect Pendulum 0x9A shares nibble A but sets SynchroXyzBit — must not match here.
        if (low == TokenNibble
            && (typeByte & LinkOrExtraPendulumBit) == 0
            && (typeByte & SynchroXyzBit) == 0)
            return "Token";

        // True Links set LinkOrExtraPendulumBit with low nibble A/B (Accesscode, Link Spider, …).
        // Other LinkOrExtraPendulumBit faces are Extra Deck pendulums (Synchro/Xyz/Ritual/…) — not Link.
        if ((typeByte & LinkOrExtraPendulumBit) != 0 && low is LinkNibbleA or LinkNibbleB)
            return "Link";

        // Odd Fusion that also sets LinkOrExtraPendulumBit (Blue-Eyes Toon Ultimate Dragon).
        // Must win over the Extra Deck Effect-Pendulum nibble path below.
        if (typeByte == FusionFaceToonUltimate)
            return "Fusion";

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

        // Fusion: low nibble 2/3 without Link or Synchro/Xyz bits (0x02/42/82/C2, 0x03/43/83/C3, …).
        // Face 0x83 is Effect Fusion (Exceed, Flame Wingman) — not Fusion Pendulum.
        if ((typeByte & ExtraDeckBitsMask) == 0 && low is FusionNibbleNormal or FusionNibbleEffect)
            return "Fusion";

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

    /// <param name="PackedAtkDef">LE uint32 from record bytes 4–7 (ATK/10 + DEF/10 or markers).</param>
    public readonly record struct CardPropRecord(int Id, byte TypeByte, byte TypeByte2, uint PackedAtkDef);
}

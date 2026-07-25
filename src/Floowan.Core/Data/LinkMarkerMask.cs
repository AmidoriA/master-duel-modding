namespace Floowan.Core.Data;

/// <summary>
/// Master Duel link-arrow directions as an 8-bit mask.
/// <para>
/// <b>Storage:</b> MD does <em>not</em> put arrows in a separate gameplay bitfield on the
/// card face alone. Directions live in decrypted <c>CARD_Prop</c> (8-byte records): the
/// little-endian <c>uint32</c> at bytes 4–7 packs ATK/10 in bits 0–8 and DEF/10 in bits
/// 9–17. For Link monsters the DEF slot holds this mask instead of defense points.
/// (Community <c>CARD_Link</c> TextAssets are related-id tables, not arrow bits.)
/// </para>
/// <para>
/// Bit order matches the MD / card-frame keypad layout (top row left→right, then mid,
/// then bottom) — <b>not</b> raw YGOPro <c>LINK_MARKER_*</c> values (those skip 0x10 and
/// order BL/B/BR/L/R/TL/T/TR). Convert with <see cref="FromYgoProMask"/> /
/// <see cref="ToYgoProMask"/> when comparing to YGOProDeck / EDOPro data.
/// </para>
/// </summary>
[Flags]
public enum LinkMarkerMask : byte
{
    None = 0,
    /// <summary>Top-left diagonal (UL).</summary>
    UpLeft = 1 << 0,
    /// <summary>Straight up.</summary>
    Up = 1 << 1,
    /// <summary>Top-right diagonal (UR).</summary>
    UpRight = 1 << 2,
    /// <summary>Straight left.</summary>
    Left = 1 << 3,
    /// <summary>Straight right.</summary>
    Right = 1 << 4,
    /// <summary>Bottom-left diagonal (DL).</summary>
    DownLeft = 1 << 5,
    /// <summary>Straight down.</summary>
    Down = 1 << 6,
    /// <summary>Bottom-right diagonal (DR).</summary>
    DownRight = 1 << 7,
}

/// <summary>Helpers for <see cref="LinkMarkerMask"/> ↔ YGOPro-style masks.</summary>
public static class LinkMarkerMaskConvert
{
    // YGOPro / EDOPro LINK_MARKER_* (numeric-keypad layout; 0x10 unused).
    public const int YgoProBottomLeft = 0x001;
    public const int YgoProBottom = 0x002;
    public const int YgoProBottomRight = 0x004;
    public const int YgoProLeft = 0x008;
    public const int YgoProRight = 0x020;
    public const int YgoProTopLeft = 0x040;
    public const int YgoProTop = 0x080;
    public const int YgoProTopRight = 0x100;

    public static LinkMarkerMask FromYgoProMask(int ygoProMask)
    {
        LinkMarkerMask m = LinkMarkerMask.None;
        if ((ygoProMask & YgoProTopLeft) != 0) m |= LinkMarkerMask.UpLeft;
        if ((ygoProMask & YgoProTop) != 0) m |= LinkMarkerMask.Up;
        if ((ygoProMask & YgoProTopRight) != 0) m |= LinkMarkerMask.UpRight;
        if ((ygoProMask & YgoProLeft) != 0) m |= LinkMarkerMask.Left;
        if ((ygoProMask & YgoProRight) != 0) m |= LinkMarkerMask.Right;
        if ((ygoProMask & YgoProBottomLeft) != 0) m |= LinkMarkerMask.DownLeft;
        if ((ygoProMask & YgoProBottom) != 0) m |= LinkMarkerMask.Down;
        if ((ygoProMask & YgoProBottomRight) != 0) m |= LinkMarkerMask.DownRight;
        return m;
    }

    public static int ToYgoProMask(LinkMarkerMask mask)
    {
        var ygo = 0;
        if ((mask & LinkMarkerMask.UpLeft) != 0) ygo |= YgoProTopLeft;
        if ((mask & LinkMarkerMask.Up) != 0) ygo |= YgoProTop;
        if ((mask & LinkMarkerMask.UpRight) != 0) ygo |= YgoProTopRight;
        if ((mask & LinkMarkerMask.Left) != 0) ygo |= YgoProLeft;
        if ((mask & LinkMarkerMask.Right) != 0) ygo |= YgoProRight;
        if ((mask & LinkMarkerMask.DownLeft) != 0) ygo |= YgoProBottomLeft;
        if ((mask & LinkMarkerMask.Down) != 0) ygo |= YgoProBottom;
        if ((mask & LinkMarkerMask.DownRight) != 0) ygo |= YgoProBottomRight;
        return ygo;
    }

    public static int Count(LinkMarkerMask mask) =>
        System.Numerics.BitOperations.PopCount((uint)(byte)mask);
}

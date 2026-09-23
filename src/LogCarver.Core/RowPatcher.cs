namespace LogCarver.Core;

/// <summary>
/// Applies an UPDATE log record's before/after diff (RowLog Contents 0/1
/// at a given [Offset in Row]) to a known row image.
///
/// The diff is a splice from the offset to the row's end, not an equal-
/// length overwrite: the two directions can have different lengths when a
/// variable-length column grows or shrinks (verified against real
/// records, e.g. an 11-byte before-image expanding to a 188-byte
/// after-image - see 研究紀錄 第八節). Applying it as a fixed-width
/// overwrite silently corrupts the row with no error - this is the single
/// most important thing to get right here.
/// </summary>
public static class RowPatcher
{
    /// <summary>
    /// rowBytes[0..offset-1] + newSpan + rowBytes[offset+oldSpanLength..end].
    /// </summary>
    public static byte[] Splice(ReadOnlySpan<byte> rowBytes, int offset, int oldSpanLength, ReadOnlySpan<byte> newSpan)
    {
        int tailStart = offset + oldSpanLength;
        int tailLength = rowBytes.Length - tailStart;
        var result = new byte[offset + newSpan.Length + tailLength];

        rowBytes[..offset].CopyTo(result);
        newSpan.CopyTo(result.AsSpan(offset));
        rowBytes[tailStart..].CopyTo(result.AsSpan(offset + newSpan.Length));

        return result;
    }

    /// <summary>Given a known "before" row, returns the "after" row.</summary>
    public static byte[] ApplyForward(ReadOnlySpan<byte> beforeRow, int offsetInRow, ReadOnlySpan<byte> rowLogContents0, ReadOnlySpan<byte> rowLogContents1) =>
        Splice(beforeRow, offsetInRow, rowLogContents0.Length, rowLogContents1);

    /// <summary>Given a known "after" row, returns the "before" row.</summary>
    public static byte[] ApplyBackward(ReadOnlySpan<byte> afterRow, int offsetInRow, ReadOnlySpan<byte> rowLogContents0, ReadOnlySpan<byte> rowLogContents1) =>
        Splice(afterRow, offsetInRow, rowLogContents1.Length, rowLogContents0);
}

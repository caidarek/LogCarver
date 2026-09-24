namespace LogCarver.Core;

/// <summary>
/// Thrown when a row's bytes don't match the layout RowDecoder implements
/// (研究紀錄 第八節/LOB 小節) - most commonly an off-row LOB value, where the
/// in-row bytes are a pointer structure rather than length-prefixed data
/// and any offset arithmetic against them lands out of bounds. Also
/// covers compressed-row layouts (research 確認 completely different TagA
/// and encoding) if one ever reaches the decoder despite the schema-level
/// compression check in the Cli.
///
/// The point of this type existing is that a caller should never see a
/// raw ArgumentOutOfRangeException/IndexOutOfRangeException from this
/// decoder and have to guess whether that means "bug in RowDecoder" or
/// "this row is a known-unsupported format" - it should always be the latter.
/// </summary>
public sealed class UnsupportedRowFormatException(string reason, Exception? innerException = null)
    : Exception(reason, innerException);

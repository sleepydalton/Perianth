namespace Perianth.Formats;

/// <summary>
/// A half-open byte range within a source resource.
/// </summary>
/// <remarks>
/// Specification section 16 asks every decoded record to carry the range it came
/// from, so a future writer can put it back and a diagnostic can point at it
/// rather than describing it.
/// </remarks>
/// <param name="Offset">Byte offset from the start of the resource.</param>
/// <param name="Length">Length in bytes.</param>
public readonly record struct ByteRange(int Offset, int Length)
{
    /// <summary>The offset one past the last byte.</summary>
    public int End => Offset + Length;
}

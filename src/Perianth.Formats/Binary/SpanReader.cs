using System;
using System.Buffers.Binary;

namespace Perianth.Formats.Binary;

/// <summary>
/// A bounded cursor over a byte buffer. All interpreted numeric fields in every
/// source format are little-endian (porting specification section 4), so that
/// is not a parameter here — it is the only thing this type does.
/// </summary>
/// <remarks>
/// <para>
/// Failure is reported as <c>false</c>, not as a <see cref="Diagnostics.Refusal"/>.
/// The reader knows that a read ran off the end; it does not know that this was
/// "the descriptor count exceeds the section", and only the grammar can say so.
/// Grammars turn a false into a refusal whose message is worth reading.
/// </para>
/// <para>
/// A failed read leaves <see cref="Position"/> untouched. There is no
/// shifted-cursor retry anywhere in this codebase, so the cursor never needs to
/// be restored — but a half-advanced cursor after a failure would make writing
/// one look reasonable.
/// </para>
/// </remarks>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    /// <summary>Creates a reader positioned at the start of <paramref name="data"/>.</summary>
    public SpanReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>The buffer's total size in bytes.</summary>
    public readonly int Length => _data.Length;

    /// <summary>The cursor's byte offset from the start of the buffer.</summary>
    public readonly int Position => _position;

    /// <summary>Bytes between the cursor and the end of the buffer.</summary>
    public readonly int Remaining => _data.Length - _position;

    /// <summary>
    /// Moves the cursor to an absolute offset. The end of the buffer is a valid
    /// destination; one byte past it is not.
    /// </summary>
    public bool TrySeek(long offset)
    {
        if (!BoundedRange.TryResolve(_data.Length, offset, count: 0, stride: 1, out int start, out _))
        {
            return false;
        }

        _position = start;
        return true;
    }

    /// <summary>Moves the cursor forward. Skipping backwards is not a thing this reader does.</summary>
    public bool TrySkip(long count)
    {
        if (count < 0 || count > Remaining)
        {
            return false;
        }

        _position += (int)count;
        return true;
    }

    /// <summary>Reads one unsigned 8-bit integer.</summary>
    public bool TryReadByte(out byte value)
    {
        if (!TryTake(1, out ReadOnlySpan<byte> bytes))
        {
            value = 0;
            return false;
        }

        value = bytes[0];
        return true;
    }

    /// <summary>Reads one little-endian unsigned 16-bit integer.</summary>
    public bool TryReadUInt16(out ushort value)
    {
        if (!TryTake(sizeof(ushort), out ReadOnlySpan<byte> bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    /// <summary>Reads one little-endian signed 16-bit integer, as used by the packed fields.</summary>
    public bool TryReadInt16(out short value)
    {
        if (!TryTake(sizeof(short), out ReadOnlySpan<byte> bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt16LittleEndian(bytes);
        return true;
    }

    /// <summary>Reads one little-endian unsigned 32-bit integer.</summary>
    public bool TryReadUInt32(out uint value)
    {
        if (!TryTake(sizeof(uint), out ReadOnlySpan<byte> bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    /// <summary>
    /// Reads one IEEE-754 binary32, by explicit bit conversion so that every
    /// stored pattern survives the read exactly as written.
    /// </summary>
    /// <remarks>
    /// Non-finite values are returned, not refused. Section 4 refuses non-finite
    /// <em>positions, constants, transforms, times and material values</em> —
    /// which of those a float is, is grammar knowledge, and an unknown field
    /// preserved for a future writer must survive verbatim regardless.
    /// </remarks>
    public bool TryReadSingle(out float value)
    {
        if (!TryTake(sizeof(float), out ReadOnlySpan<byte> bytes))
        {
            value = 0f;
            return false;
        }

        value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
        return true;
    }

    /// <summary>Reads <paramref name="count"/> bytes and advances past them.</summary>
    public bool TryReadBytes(long count, out ReadOnlySpan<byte> bytes)
    {
        if (!BoundedRange.TryResolve(_data.Length, _position, count, stride: 1, out int start, out int byteCount))
        {
            bytes = default;
            return false;
        }

        bytes = _data.Slice(start, byteCount);
        _position = start + byteCount;
        return true;
    }

    /// <summary>
    /// Takes a window at an absolute offset without moving the cursor, for the
    /// pools and tables the formats address by offset rather than in sequence.
    /// </summary>
    public readonly bool TrySlice(long offset, long count, long stride, out ReadOnlySpan<byte> bytes)
    {
        if (!BoundedRange.TryResolve(_data.Length, offset, count, stride, out int start, out int byteCount))
        {
            bytes = default;
            return false;
        }

        bytes = _data.Slice(start, byteCount);
        return true;
    }

    private bool TryTake(int width, out ReadOnlySpan<byte> bytes)
    {
        if (!BoundedRange.TryResolve(_data.Length, _position, count: 1, stride: width, out int start, out int byteCount))
        {
            bytes = default;
            return false;
        }

        bytes = _data.Slice(start, byteCount);
        _position = start + byteCount;
        return true;
    }
}

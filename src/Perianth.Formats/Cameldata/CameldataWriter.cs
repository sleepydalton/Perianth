using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Cameldata;

/// <summary>
/// Writes a <see cref="CameldataFile"/> back to bytes.
/// </summary>
/// <remarks>
/// <para>
/// The third stage of import (Roadmap §6.3), and the reason
/// <see cref="CameldataReader"/> keeps the Bezier block, each constant's
/// uninterpreted data indices and optional tail, the raw header word and any
/// trailing bytes: a writer needs the fields nothing reads as much as the fields
/// everything does.
/// </para>
/// <para>
/// <strong>The correctness bar here is byte equality, not plausibility.</strong>
/// A wrong read refuses; a wrong write produces a file that loads and
/// misbehaves, which the game renders without complaint. So this writes only what
/// a read proved, emits no default it was not given, and reorders nothing. Read a
/// real file, write it back, require the bytes to match — that is the oracle, and
/// it runs over the corpus in <c>CameldataCorpusTests</c>.
/// </para>
/// <para>
/// It follows that this writer has no opinions. It will not renumber a base
/// index, drop an array it thinks is unused, pad a short tail, or rebuild the
/// header word from the mode and flags it was given. Every one of those would be
/// an improvement that breaks the only check capable of telling us the writer
/// works at all.
/// </para>
/// </remarks>
public static class CameldataWriter
{
    private const int DataIndicesLength = 16;
    private const int OptionalTailLength = 8;

    /// <summary>
    /// Serializes <paramref name="file"/>, or refuses if it cannot be spelled.
    /// </summary>
    /// <remarks>
    /// The refusals are all one shape: a file holding something the grammar has
    /// no way to express — a preserved block whose length is not the one the
    /// stride reserves for it, or a Bezier count that disagrees with the bytes
    /// kept for it. None can arise from a file the reader decoded. They arise
    /// from a file assembled in code, which is what a geometry edit does.
    /// </remarks>
    public static Result<byte[]> Write(CameldataFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        // The header word is written whole rather than rebuilt from Mode and
        // Flags. Those are conveniences the reader derived; treating them as the
        // source would silently discard any bit the reader read and did not name.
        if ((int)(file.HeaderWord & 3) != file.Mode)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The cameldata header word declares mode {file.HeaderWord & 3} while the file says mode {file.Mode}."));
        }

        if (file.BezierBytes.Length != file.BezierWordCount * sizeof(uint))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The cameldata declares {file.BezierWordCount} Bezier words but carries {file.BezierBytes.Length} bytes for them."));
        }

        return file switch
        {
            Mode3Cameldata mode3 => WriteMode3(mode3),
            Mode2Cameldata mode2 => WriteMode2(mode2),

            // Unreachable through the reader, which produces only the two. A
            // third subclass added later must extend this rather than fall
            // through to a plausible default.
            _ => Refusal.Unsupported("The cameldata is of a kind this writer does not implement."),
        };
    }

    private static Result<byte[]> WriteMode2(Mode2Cameldata file)
    {
        Builder bytes = new(Estimate(file, 136, 3 * sizeof(float), file.Positions.Length));
        WriteHeader(bytes, file, (uint)file.Constants.Length);

        for (int i = 0; i < file.Constants.Length; i++)
        {
            Mode2Constant constant = file.Constants[i];
            if (Preserved(constant.DataIndices, constant.OptionalTail, file.Flags, i) is { } refusal)
            {
                return refusal;
            }

            WriteVector4(bytes, constant.SurfaceOrigin);
            WriteVector4(bytes, constant.SurfaceU);
            WriteVector4(bytes, constant.SurfaceV);
            bytes.AddBytes(constant.DataIndices.Span);
            WriteMatrix(bytes, constant.InverseLocal);
            bytes.AddSingle(constant.PositionXScale);
            bytes.AddSingle(constant.InverseUnitScale);
            bytes.AddBytes(constant.OptionalTail.Span);
        }

        bytes.AddUInt32((uint)file.Positions.Length);
        foreach (Vector3 position in file.Positions)
        {
            bytes.AddSingle(position.X);
            bytes.AddSingle(position.Y);
            bytes.AddSingle(position.Z);
        }

        bytes.AddBytes(file.TrailingBytes.Span);
        return Result.Ok(bytes.ToArray());
    }

    private static Result<byte[]> WriteMode3(Mode3Cameldata file)
    {
        Builder bytes = new(Estimate(file, 152, 0, 0) +
            (file.Xy.Length * 2 * sizeof(float)) + (file.Z.Length * sizeof(float)) +
            ((file.Uv0.Length + file.PackedZ.Length) * sizeof(uint)) + (4 * sizeof(uint)));

        WriteHeader(bytes, file, (uint)file.Constants.Length);

        for (int i = 0; i < file.Constants.Length; i++)
        {
            Mode3Constant constant = file.Constants[i];
            if (Preserved(constant.DataIndices, constant.OptionalTail, file.Flags, i) is { } refusal)
            {
                return refusal;
            }

            WriteVector4(bytes, constant.SurfaceOrigin);
            WriteVector4(bytes, constant.SurfaceU);
            WriteVector4(bytes, constant.SurfaceV);
            bytes.AddBytes(constant.DataIndices.Span);
            bytes.AddUInt32(constant.XyBase);
            bytes.AddUInt32(constant.ZBase);
            bytes.AddUInt32(constant.Uv0Base);

            // Written whole for the same reason as the header word: UsesUnifiedUv0,
            // Uv0ScaleIndex and ZBitWidth are views over bits 0 to 7, and bits 8
            // upward have no name here. Rebuilding from the named parts would drop
            // whatever the rest carry.
            bytes.AddUInt32(constant.PackedFlags);

            WriteMatrix(bytes, constant.InverseLocal);
            bytes.AddSingle(constant.PositionXScale);
            bytes.AddSingle(constant.InverseUnitScale);
            bytes.AddBytes(constant.OptionalTail.Span);
        }

        bytes.AddUInt32((uint)file.Xy.Length);
        foreach (Vector2 value in file.Xy)
        {
            bytes.AddSingle(value.X);
            bytes.AddSingle(value.Y);
        }

        bytes.AddUInt32((uint)file.Z.Length);
        foreach (float value in file.Z)
        {
            bytes.AddSingle(value);
        }

        WriteWords(bytes, file.Uv0);
        WriteWords(bytes, file.PackedZ);

        bytes.AddBytes(file.TrailingBytes.Span);
        return Result.Ok(bytes.ToArray());
    }

    private static void WriteHeader(Builder bytes, CameldataFile file, uint constantCount)
    {
        bytes.AddUInt32(file.HeaderWord);
        bytes.AddUInt32(constantCount);
        bytes.AddUInt32((uint)file.BezierWordCount);
        bytes.AddBytes(file.BezierBytes.Span);
    }

    private static void WriteWords(Builder bytes, ImmutableArray<uint> values)
    {
        bytes.AddUInt32((uint)values.Length);
        foreach (uint value in values)
        {
            bytes.AddUInt32(value);
        }
    }

    private static void WriteVector4(Builder bytes, Vector4 value)
    {
        bytes.AddSingle(value.X);
        bytes.AddSingle(value.Y);
        bytes.AddSingle(value.Z);
        bytes.AddSingle(value.W);
    }

    private static void WriteMatrix(Builder bytes, SerializedMatrix matrix)
    {
        WriteVector4(bytes, matrix.Group0);
        WriteVector4(bytes, matrix.Group1);
        WriteVector4(bytes, matrix.Group2);
        WriteVector4(bytes, matrix.Group3);
    }

    /// <summary>
    /// Checks the two blocks the reader kept without interpreting, whose lengths
    /// the stride fixes.
    /// </summary>
    /// <remarks>
    /// A short block would shift every byte after it and a long one would
    /// overwrite the next field, and either produces a file that parses. This is
    /// the check that cannot be recovered from the values themselves, because the
    /// bytes mean nothing here.
    /// </remarks>
    private static Refusal? Preserved(
        ReadOnlyMemory<byte> dataIndices, ReadOnlyMemory<byte> optionalTail, int flags, int ordinal)
    {
        if (dataIndices.Length != DataIndicesLength)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Cameldata constant {ordinal} carries {dataIndices.Length} data-index bytes, and the stride reserves {DataIndicesLength}."));
        }

        int expected = flags != 0 ? OptionalTailLength : 0;
        return optionalTail.Length == expected
            ? null
            : Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Cameldata constant {ordinal} carries {optionalTail.Length} tail bytes, and this file's flag reserves {expected}."));
    }

    private static int Estimate(CameldataFile file, int stride, int itemBytes, int itemCount) =>
        (3 * sizeof(uint)) + file.BezierBytes.Length +
        (stride + (file.Flags != 0 ? OptionalTailLength : 0)) *
            (file is Mode3Cameldata mode3 ? mode3.Constants.Length : ((Mode2Cameldata)file).Constants.Length) +
        sizeof(uint) + (itemBytes * itemCount) + file.TrailingBytes.Length;

    /// <summary>
    /// A growable byte buffer that appends little-endian primitives.
    /// </summary>
    /// <remarks>
    /// A local type rather than <see cref="System.IO.MemoryStream"/> or a
    /// <c>List&lt;byte&gt;</c> per field: the arrays here reach hundreds of
    /// thousands of entries, and this writes each primitive straight into the
    /// buffer without an intermediate allocation.
    /// </remarks>
    private sealed class Builder(int capacity)
    {
        private byte[] _buffer = new byte[Math.Max(capacity, 16)];
        private int _length;

        public void AddUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Reserve(sizeof(uint)), value);
        }

        /// <summary>
        /// Writes the float's exact bits.
        /// </summary>
        /// <remarks>
        /// No arithmetic touches the value on the way through, so a bit pattern
        /// the reader accepted comes back out unchanged — negative zero included,
        /// which comparing against zero would erase.
        /// </remarks>
        public void AddSingle(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(Reserve(sizeof(float)), value);
        }

        public void AddBytes(ReadOnlySpan<byte> value)
        {
            value.CopyTo(Reserve(value.Length));
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

        private Span<byte> Reserve(int count)
        {
            if (_length + count > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
            }

            Span<byte> span = _buffer.AsSpan(_length, count);
            _length += count;
            return span;
        }
    }
}

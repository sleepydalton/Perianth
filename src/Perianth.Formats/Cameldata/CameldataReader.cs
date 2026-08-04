using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Cameldata;

/// <summary>
/// Decodes a cameldata file: the shared header, then whichever mode it declared.
/// </summary>
public static class CameldataReader
{
    private const uint ReservedHeaderBits = 0x7FFC;
    private const int DataIndicesLength = 16;
    private const int OptionalTailLength = 8;
    private const int Mode2Stride = 136;
    private const int Mode3Stride = 152;

    /// <summary>Decodes <paramref name="file"/>.</summary>
    public static Result<CameldataFile> Read(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        ReadOnlyMemory<byte> memory = file.Memory;
        SpanReader reader = new(memory.Span);

        if (!reader.TryReadUInt32(out uint headerWord) ||
            !reader.TryReadUInt32(out uint constantCount) ||
            !reader.TryReadUInt32(out uint bezierWordCount))
        {
            return Refusal.Malformed("The cameldata header is truncated.");
        }

        if ((headerWord & ReservedHeaderBits) != 0)
        {
            return Refusal.Malformed("The cameldata header sets reserved bits 2 to 14.");
        }

        uint flags = headerWord >> 15;
        if (flags > 1)
        {
            return Refusal.Malformed("The cameldata header carries a flag field other than 0 or 1.");
        }

        int mode = (int)(headerWord & 3);
        if (mode is not (2 or 3))
        {
            // A coherent header naming a mode nobody has implemented. The file
            // is not broken, so this is unsupported rather than malformed.
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The cameldata declares mode {mode}, and only modes 2 and 3 are implemented."));
        }

        int bezierStart = reader.Position;
        if (!reader.TrySkip((long)bezierWordCount * sizeof(uint)))
        {
            return Refusal.Malformed("The cameldata Bezier block runs past the end of the file.");
        }

        ReadOnlyMemory<byte> bezierBytes = memory[bezierStart..reader.Position];

        return mode == 2
            ? ReadMode2(file, memory, ref reader, headerWord, (int)flags, (int)bezierWordCount, bezierBytes, constantCount)
            : ReadMode3(file, memory, ref reader, headerWord, (int)flags, (int)bezierWordCount, bezierBytes, constantCount);
    }

    private static Result<CameldataFile> ReadMode2(
        SourceFile file,
        ReadOnlyMemory<byte> memory,
        ref SpanReader reader,
        uint headerWord,
        int flags,
        int bezierWordCount,
        ReadOnlyMemory<byte> bezierBytes,
        uint constantCount)
    {
        if (!Fits(reader, constantCount, Mode2Stride + (flags != 0 ? OptionalTailLength : 0)))
        {
            return Refusal.Malformed("The cameldata mode-2 constants run past the end of the file.");
        }

        ImmutableArray<Mode2Constant>.Builder constants =
            ImmutableArray.CreateBuilder<Mode2Constant>((int)constantCount);

        for (uint i = 0; i < constantCount; i++)
        {
            if (!TryReadSurface(ref reader, memory, out Vector4 origin, out Vector4 u, out Vector4 v,
                    out ReadOnlyMemory<byte> dataIndices) ||
                !TryReadMatrix(ref reader, out SerializedMatrix matrix) ||
                !TryReadFinite(ref reader, out float positionXScale) ||
                !TryReadFinite(ref reader, out float inverseUnitScale) ||
                !TryReadTail(ref reader, memory, flags, out ReadOnlyMemory<byte> tail))
            {
                return MalformedConstant(i);
            }

            constants.Add(new Mode2Constant(
                origin, u, v, dataIndices, matrix, positionXScale, inverseUnitScale, tail));
        }

        if (!reader.TryReadUInt32(out uint positionCount))
        {
            return Refusal.Malformed("The cameldata mode-2 position count is missing.");
        }

        const int PositionStride = 3 * sizeof(float);
        if (!Fits(reader, positionCount, PositionStride))
        {
            return Refusal.Malformed("The cameldata mode-2 position pool runs past the end of the file.");
        }

        ImmutableArray<Vector3>.Builder positions = ImmutableArray.CreateBuilder<Vector3>((int)positionCount);
        for (uint i = 0; i < positionCount; i++)
        {
            if (!TryReadFinite(ref reader, out float x) ||
                !TryReadFinite(ref reader, out float y) ||
                !TryReadFinite(ref reader, out float z))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Cameldata position {i} is missing or not finite."));
            }

            positions.Add(new Vector3(x, y, z));
        }

        return Result.Ok<CameldataFile>(new Mode2Cameldata(
            file.Path, headerWord, flags, bezierWordCount, bezierBytes,
            constants.MoveToImmutable(), positions.MoveToImmutable(), memory[reader.Position..]));
    }

    private static Result<CameldataFile> ReadMode3(
        SourceFile file,
        ReadOnlyMemory<byte> memory,
        ref SpanReader reader,
        uint headerWord,
        int flags,
        int bezierWordCount,
        ReadOnlyMemory<byte> bezierBytes,
        uint constantCount)
    {
        if (constantCount == 0)
        {
            return Refusal.Malformed("The cameldata declares mode 3 with no constants.");
        }

        if (!Fits(reader, constantCount, Mode3Stride + (flags != 0 ? OptionalTailLength : 0)))
        {
            return Refusal.Malformed("The cameldata mode-3 constants run past the end of the file.");
        }

        ImmutableArray<Mode3Constant>.Builder constants =
            ImmutableArray.CreateBuilder<Mode3Constant>((int)constantCount);

        for (uint i = 0; i < constantCount; i++)
        {
            if (!TryReadSurface(ref reader, memory, out Vector4 origin, out Vector4 u, out Vector4 v,
                    out ReadOnlyMemory<byte> dataIndices) ||
                !reader.TryReadUInt32(out uint xyBase) ||
                !reader.TryReadUInt32(out uint zBase) ||
                !reader.TryReadUInt32(out uint uv0Base) ||
                !reader.TryReadUInt32(out uint packedFlags) ||
                !TryReadMatrix(ref reader, out SerializedMatrix matrix) ||
                !TryReadFinite(ref reader, out float positionXScale) ||
                !TryReadFinite(ref reader, out float inverseUnitScale) ||
                !TryReadTail(ref reader, memory, flags, out ReadOnlyMemory<byte> tail))
            {
                return MalformedConstant(i);
            }

            constants.Add(new Mode3Constant(
                origin, u, v, dataIndices, xyBase, zBase, uv0Base, packedFlags,
                matrix, positionXScale, inverseUnitScale, tail));
        }

        Result<ImmutableArray<Vector2>> xy = ReadXyArray(ref reader);
        if (!xy.IsSuccess)
        {
            return xy.Refusal;
        }

        Result<ImmutableArray<float>> z = ReadFloatArray(ref reader);
        if (!z.IsSuccess)
        {
            return z.Refusal;
        }

        Result<ImmutableArray<uint>> uv0 = ReadWordArray(ref reader, "UV0");
        if (!uv0.IsSuccess)
        {
            return uv0.Refusal;
        }

        Result<ImmutableArray<uint>> packedZ = ReadWordArray(ref reader, "packed Z-index");
        if (!packedZ.IsSuccess)
        {
            return packedZ.Refusal;
        }

        return Result.Ok<CameldataFile>(new Mode3Cameldata(
            file.Path, headerWord, flags, bezierWordCount, bezierBytes,
            constants.MoveToImmutable(), xy.Value, z.Value, uv0.Value, packedZ.Value,
            memory[reader.Position..]));
    }

    private static Result<ImmutableArray<Vector2>> ReadXyArray(ref SpanReader reader)
    {
        if (!reader.TryReadUInt32(out uint count) || !Fits(reader, count, 2 * sizeof(float)))
        {
            return Refusal.Malformed("The cameldata XY array is missing or runs past the end of the file.");
        }

        ImmutableArray<Vector2>.Builder values = ImmutableArray.CreateBuilder<Vector2>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (!TryReadFinite(ref reader, out float x) || !TryReadFinite(ref reader, out float y))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"Cameldata XY entry {i} is not finite."));
            }

            values.Add(new Vector2(x, y));
        }

        return Result.Ok(values.MoveToImmutable());
    }

    private static Result<ImmutableArray<float>> ReadFloatArray(ref SpanReader reader)
    {
        if (!reader.TryReadUInt32(out uint count) || !Fits(reader, count, sizeof(float)))
        {
            return Refusal.Malformed("The cameldata Z array is missing or runs past the end of the file.");
        }

        ImmutableArray<float>.Builder values = ImmutableArray.CreateBuilder<float>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (!TryReadFinite(ref reader, out float value))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"Cameldata Z entry {i} is not finite."));
            }

            values.Add(value);
        }

        return Result.Ok(values.MoveToImmutable());
    }

    private static Result<ImmutableArray<uint>> ReadWordArray(ref SpanReader reader, string name)
    {
        if (!reader.TryReadUInt32(out uint count) || !Fits(reader, count, sizeof(uint)))
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The cameldata {name} array is missing or runs past the end of the file."));
        }

        ImmutableArray<uint>.Builder values = ImmutableArray.CreateBuilder<uint>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (!reader.TryReadUInt32(out uint value))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"The cameldata {name} array is truncated."));
            }

            values.Add(value);
        }

        return Result.Ok(values.MoveToImmutable());
    }

    /// <summary>
    /// Whether <paramref name="count"/> elements of <paramref name="stride"/>
    /// bytes remain, checked before any of them is allocated.
    /// </summary>
    private static bool Fits(SpanReader reader, uint count, int stride) =>
        BoundedRange.TryResolve(reader.Length, reader.Position, count, stride, out _, out _);

    private static bool TryReadSurface(
        ref SpanReader reader,
        ReadOnlyMemory<byte> memory,
        out Vector4 origin,
        out Vector4 u,
        out Vector4 v,
        out ReadOnlyMemory<byte> dataIndices)
    {
        origin = default;
        u = default;
        v = default;
        dataIndices = default;

        if (!TryReadVector4(ref reader, out origin) ||
            !TryReadVector4(ref reader, out u) ||
            !TryReadVector4(ref reader, out v))
        {
            return false;
        }

        int start = reader.Position;
        if (!reader.TrySkip(DataIndicesLength))
        {
            return false;
        }

        dataIndices = memory.Slice(start, DataIndicesLength);
        return true;
    }

    private static bool TryReadMatrix(ref SpanReader reader, out SerializedMatrix matrix)
    {
        matrix = default;
        if (!TryReadVector4(ref reader, out Vector4 group0) ||
            !TryReadVector4(ref reader, out Vector4 group1) ||
            !TryReadVector4(ref reader, out Vector4 group2) ||
            !TryReadVector4(ref reader, out Vector4 group3))
        {
            return false;
        }

        matrix = new SerializedMatrix(group0, group1, group2, group3);
        return true;
    }

    private static bool TryReadTail(
        ref SpanReader reader,
        ReadOnlyMemory<byte> memory,
        int flags,
        out ReadOnlyMemory<byte> tail)
    {
        tail = default;
        if (flags == 0)
        {
            return true;
        }

        int start = reader.Position;
        if (!reader.TrySkip(OptionalTailLength))
        {
            return false;
        }

        tail = memory.Slice(start, OptionalTailLength);
        return true;
    }

    private static bool TryReadVector4(ref SpanReader reader, out Vector4 value)
    {
        value = default;
        if (!TryReadFinite(ref reader, out float x) ||
            !TryReadFinite(ref reader, out float y) ||
            !TryReadFinite(ref reader, out float z) ||
            !TryReadFinite(ref reader, out float w))
        {
            return false;
        }

        value = new Vector4(x, y, z, w);
        return true;
    }

    /// <summary>
    /// Reads one float and requires it to be finite, which section 4 demands of
    /// every constant and transform.
    /// </summary>
    private static bool TryReadFinite(ref SpanReader reader, out float value) =>
        reader.TryReadSingle(out value) && float.IsFinite(value);

    private static Refusal MalformedConstant(uint ordinal) =>
        Refusal.Malformed(string.Create(
            CultureInfo.InvariantCulture,
            $"Cameldata constant {ordinal} is truncated or carries a value that is not finite."));
}

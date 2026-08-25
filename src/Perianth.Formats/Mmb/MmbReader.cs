using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Mmb;

/// <summary>
/// Reads an MMB model file: its node table, and the model parts that follow.
/// </summary>
/// <remarks>
/// <para>
/// <b>This reads the container, and it used to scan for it.</b> Specification
/// §5.1 said the enclosing structure had never been derived and located records
/// by matching a signature. Roadmap §10.47 and §10.53 derived it from the
/// loader: a magic and version byte, a node table, a part count, and a versioned
/// part grammar, read front to back.
/// </para>
/// <para>
/// The signature turned out to be this grammar with two counts pinned to
/// constants — §10.54. Its unexplained seven-byte suffix was a zero matrix
/// count, a LOD count of one, and a flags word; its ten opaque descriptor words
/// were one version-11 LOD entry. So the scan was a special case, and it was
/// measured to be a very good one: of 441,865 real parts, 441,864 matched. The
/// one that did not carries matrices, and the scan did not lose it quietly — it
/// reported the whole file as holding no records at all. Absence and "cannot
/// represent this" were indistinguishable, which is the failure this reader
/// exists to end.
/// </para>
/// <para>
/// <b>Every unreadable thing is a refusal.</b> There is no position at which
/// this gives up and moves on, because there is no searching left to do: a
/// field that does not parse is a file that is not what it claims to be.
/// </para>
/// </remarks>
public static class MmbReader
{
    /// <summary>The plain container's magic, before the version byte.</summary>
    private static ReadOnlySpan<byte> Magic => "MMB"u8;

    /// <summary>
    /// Two container variants this reader does not decode, kept named so they
    /// refuse for what they are.
    /// </summary>
    /// <remarks>
    /// <c>MUCM</c> is an alternative envelope and <c>MCMP</c> a compressed one
    /// carrying a codec id and both lengths. Two of the 2,285 files measured are
    /// <c>MUCM</c>; neither had been noticed while the reader scanned, because a
    /// scan never has to know what container it is inside.
    /// </remarks>
    private static ReadOnlySpan<byte> AlternativeMagic => "MUCM"u8;

    private static ReadOnlySpan<byte> CompressedMagic => "MCMP"u8;

    /// <summary>Below this version the loader reads no part body at all.</summary>
    private const int FirstVersionWithParts = 6;

    private const int VersionMask = 0x3F;
    private const int ValueByteCount = 48;
    private const int ValueCount = 12;
    private const int MatrixByteCount = 64;
    private const int NodeTrailerBytes = 2;
    private const int DeclarationStride = 4;
    private const int DescriptorWordCount = 10;

    /// <summary>Reads every node and model part in <paramref name="file"/>.</summary>
    public static Result<MmbModel> Read(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ReadOnlyMemory<byte> memory = file.Memory;
        ReadOnlySpan<byte> data = memory.Span;
        SpanReader reader = new(data);

        Result<MmbHeader> header = ReadHeader(ref reader);
        if (!header.IsSuccess)
        {
            return header.Refusal;
        }

        int version = header.Value.Version;

        if (!reader.TryReadUInt32(out uint nodeCount))
        {
            return Refusal.Malformed("The file ends where its node count should be.");
        }

        ImmutableArray<MmbNode>.Builder nodes =
            ImmutableArray.CreateBuilder<MmbNode>((int)Math.Min(nodeCount, 1u << 16));

        for (uint node = 0; node < nodeCount; node++)
        {
            // A node is a name, a 4x4 matrix and a trailing short. Nothing in
            // export reads them -- the posed hierarchy comes from the setup
            // ANIM -- but they are kept rather than walked past, because a
            // writer must put them back and a skipped field is invisible until
            // one tries.
            if (!reader.TryReadUInt16(out ushort nameLength) ||
                !reader.TryReadBytes(nameLength, out _))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The node table ends early, at node {node} of {nodeCount}."));
            }

            int nameAt = reader.Position - nameLength;
            if (!reader.TryReadBytes(MatrixByteCount, out _) ||
                !reader.TryReadUInt16(out ushort trailer))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The node table ends early, at node {node} of {nodeCount}."));
            }

            nodes.Add(new MmbNode(
                memory.Slice(nameAt, nameLength),
                memory.Slice(nameAt + nameLength, MatrixByteCount),
                trailer));
        }

        if (!reader.TryReadUInt32(out uint partCount))
        {
            return Refusal.Malformed("The file ends where its model-part count should be.");
        }

        ImmutableArray<MmbModelPart>.Builder parts =
            ImmutableArray.CreateBuilder<MmbModelPart>((int)Math.Min(partCount, 1u << 16));

        for (uint ordinal = 0; ordinal < partCount; ordinal++)
        {
            Result<MmbModelPart?> part = ReadPart(memory, ref reader, version, parts.Count);
            if (!part.IsSuccess)
            {
                return part.Refusal;
            }

            // A part with an empty name carries no body, and the loader reads
            // nothing further for it. It is a hole in the table rather than
            // geometry, so it takes no ordinal.
            if (part.Value is MmbModelPart present)
            {
                parts.Add(present);
            }
        }

        if (parts.Count == 0)
        {
            return Refusal.Malformed(
                "The file declares no model parts that draw anything.");
        }

        return Result.Ok(new MmbModel(
            file.Path, version, header.Value.Flags, header.Value.DeclaredLength,
            nodes.ToImmutable(), parts.ToImmutable()));
    }

    /// <summary>What the four-byte magic and the word after it said.</summary>
    private readonly record struct MmbHeader(int Version, int Flags, uint DeclaredLength);

    /// <summary>The magic and version byte, and the declared length after it.</summary>
    private static Result<MmbHeader> ReadHeader(ref SpanReader reader)
    {
        if (!reader.TryReadBytes(4, out ReadOnlySpan<byte> magic))
        {
            return Refusal.Malformed("The file is too short to be a model.");
        }

        if (magic.SequenceEqual(AlternativeMagic) || magic.SequenceEqual(CompressedMagic))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This model is in the '{Encoding.ASCII.GetString(magic)}' container, which this build does not decode. Only the plain 'MMB' container is read."));
        }

        if (!magic[..3].SequenceEqual(Magic))
        {
            return Refusal.Malformed("This is not a model file: it does not begin with 'MMB'.");
        }

        int version = magic[3] & VersionMask;
        if (version < FirstVersionWithParts)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This model declares version {version}, which stores no part geometry."));
        }

        // The word after the magic is the file's own length on all 2,283 files
        // measured. It is kept rather than trusted: nothing seeks by it, and a
        // writer sets it from the length it actually produced.
        if (!reader.TryReadUInt32(out uint declaredLength))
        {
            return Refusal.Malformed("The file ends inside its header.");
        }

        return Result.Ok(new MmbHeader(version, magic[3] >> 6, declaredLength));
    }

    /// <summary>
    /// One model part, or null where the table holds an empty entry.
    /// </summary>
    private static Result<MmbModelPart?> ReadPart(
        ReadOnlyMemory<byte> memory, ref SpanReader reader, int version, int ordinal)
    {
        ReadOnlySpan<byte> data = memory.Span;
        int start = reader.Position;

        if (!reader.TryReadUInt16(out ushort nameLength) ||
            !reader.TryReadBytes(nameLength, out ReadOnlySpan<byte> name))
        {
            return Malformed(ordinal, "has a name that runs past the end of the file");
        }

        // The loader has a branch for these and no file exercises it: zero of
        // 441,865 entries measured. Skipping one would drop it from a file a
        // writer then rebuilt without it, so it refuses instead.
        if (nameLength == 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Model part {ordinal} has no name, which this build has never seen and cannot write back."));
        }

        int nameOffset = reader.Position - nameLength;

        ImmutableArray<float>.Builder values = ImmutableArray.CreateBuilder<float>(ValueCount);
        for (int i = 0; i < ValueCount; i++)
        {
            if (!reader.TryReadSingle(out float value))
            {
                return Malformed(ordinal, "ends inside its transform block");
            }

            values.Add(value);
        }

        // Two bytes the older versions do not carry. Flags to the loader and
        // unread here, but they must be consumed or every later field is off by
        // their width -- the shifted cursor this project treats as the
        // characteristic grammar fault -- and kept, or a writer cannot restore
        // them.
        int flagsAt = reader.Position;
        int flagCount = (version >= 8 ? 1 : 0) + (version > 9 ? 1 : 0);
        if (!reader.TrySkip(flagCount))
        {
            return Malformed(ordinal, "ends where its flag bytes should be");
        }

        if (!reader.TryReadUInt16(out ushort declarationCount) ||
            !reader.TryReadBytes((long)declarationCount * DeclarationStride, out _))
        {
            return Malformed(ordinal, "has a declaration block that runs past the end of the file");
        }

        int declarationOffset = reader.Position - (declarationCount * DeclarationStride);

        if (!reader.TryReadUInt16(out ushort matrixCount))
        {
            return Malformed(ordinal, "ends where its matrix count should be");
        }

        int matrixOffset = reader.Position;
        if (!reader.TrySkip((long)matrixCount * (MatrixByteCount + NodeTrailerBytes)))
        {
            return Malformed(ordinal, "has a matrix block that runs past the end of the file");
        }

        int matrixBytes = reader.Position - matrixOffset;

        if (!reader.TryReadByte(out byte lodCount))
        {
            return Malformed(ordinal, "ends where its level-of-detail count should be");
        }

        uint lodFlags = 0;
        if (version >= 7 && !reader.TryReadUInt32(out lodFlags))
        {
            return Malformed(ordinal, "ends where its level-of-detail flags should be");
        }

        // A descriptor is one LOD entry. Every one of 441,865 parts measured
        // declares exactly one, so more than one is refused rather than
        // silently reduced to the first: a model whose levels of detail were
        // dropped would export and draw, at whichever detail happened to come
        // first.
        if (lodCount != 1)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Model part {ordinal} declares {lodCount} levels of detail, and this build reads models with one."));
        }

        Result<MmbGeometryDescriptor> descriptor = ReadDescriptor(ref reader, version, ordinal);
        if (!descriptor.IsSuccess)
        {
            return descriptor.Refusal;
        }

        int tailAt = reader.Position;
        if (!SkipTail(ref reader, version))
        {
            return Malformed(ordinal, "ends inside the fields after its descriptor");
        }

        int end = reader.Position;

        Result<MmbModelPart> built = BuildPart(
            memory, data, descriptor.Value, ordinal,
            new ByteRange(start, end - start),
            nameOffset, nameLength,
            values.MoveToImmutable(),
            declarationCount, declarationOffset,
            matrixCount, matrixOffset, matrixBytes,
            memory.Slice(flagsAt, flagCount), lodFlags,
            memory.Slice(tailAt, end - tailAt));

        return built.IsSuccess ? Result.Ok<MmbModelPart?>(built.Value) : built.Refusal;
    }

    /// <summary>
    /// The descriptor: one level-of-detail entry, whose width the version sets.
    /// </summary>
    /// <remarks>
    /// Version 11 writes all ten words. Version 9 omits the last, and below 9
    /// two more; below 8, one word is a copy of another rather than written.
    /// The omitted words are left zero, which is what the loader leaves them.
    /// </remarks>
    private static Result<MmbGeometryDescriptor> ReadDescriptor(
        ref SpanReader reader, int version, int ordinal)
    {
        Span<uint> words = stackalloc uint[DescriptorWordCount];

        for (int word = 0; word < 5; word++)
        {
            if (!reader.TryReadUInt32(out words[word]))
            {
                return Malformed(ordinal, "has a truncated descriptor");
            }
        }

        if (version >= 8)
        {
            if (!reader.TryReadUInt32(out words[5]))
            {
                return Malformed(ordinal, "has a truncated descriptor");
            }
        }
        else
        {
            // The loader copies word 4 into word 5 rather than reading one.
            words[5] = words[4];
        }

        if (!reader.TryReadUInt32(out words[6]))
        {
            return Malformed(ordinal, "has a truncated descriptor");
        }

        if (version > 8)
        {
            if (!reader.TryReadUInt32(out words[7]) || !reader.TryReadUInt32(out words[8]))
            {
                return Malformed(ordinal, "has a truncated descriptor");
            }
        }

        if (version > 10 && !reader.TryReadUInt32(out words[9]))
        {
            return Malformed(ordinal, "has a truncated descriptor");
        }

        return Result.Ok(new MmbGeometryDescriptor(
            words[0], words[1], words[2], words[3], words[4],
            words[5], words[6], words[7], words[8], words[9]));
    }

    /// <summary>The fields between the descriptor and the next part.</summary>
    private static bool SkipTail(ref SpanReader reader, int version)
    {
        if (!reader.TrySkip(sizeof(uint) * 2) || !reader.TryReadByte(out byte extra))
        {
            return false;
        }

        if (!reader.TrySkip((long)extra * sizeof(uint)) || !reader.TrySkip(sizeof(ushort)))
        {
            return false;
        }

        if (version >= 8 && !reader.TrySkip(sizeof(ushort)))
        {
            return false;
        }

        // Four words: a flag, and the three the first pass keeps. The last is
        // the payload length below version 9, where payloads are concatenated
        // after the table and the loader tracks a running cursor. From version 9
        // the descriptor carries its own absolute offset and length, which is
        // what this build reads.
        return reader.TrySkip(sizeof(uint) * 4);
    }

    private static Result<MmbModelPart> BuildPart(
        ReadOnlyMemory<byte> memory,
        ReadOnlySpan<byte> data,
        MmbGeometryDescriptor descriptor,
        int ordinal,
        ByteRange envelope,
        int nameOffset,
        int nameLength,
        ImmutableArray<float> values,
        int declarationCount,
        int declarationOffset,
        int matrixCount,
        int matrixOffset,
        int matrixBytes,
        ReadOnlyMemory<byte> flagBytes,
        uint lodFlags,
        ReadOnlyMemory<byte> tailBytes)
    {
        if (descriptor.VertexCount == 0)
        {
            return Malformed(ordinal, "declares no vertices");
        }

        if (!BoundedRange.TryResolve(
                data.Length, descriptor.PayloadOffset, descriptor.PayloadLength, 1,
                out int payloadStart, out int payloadLength))
        {
            return Malformed(ordinal, "has a payload that does not lie inside the file");
        }

        Result<ImmutableArray<int>> indices = ReadStoredIndices(
            data, descriptor, payloadStart, payloadLength, ordinal);
        if (!indices.IsSuccess)
        {
            return indices.Refusal;
        }

        return Result.Ok(new MmbModelPart(
            ordinal,
            envelope,
            Encoding.ASCII.GetString(data.Slice(nameOffset, nameLength)),
            memory.Slice(nameOffset, nameLength),
            values,
            declarationCount,
            memory.Slice(declarationOffset, declarationCount * DeclarationStride),
            descriptor,
            memory.Slice(payloadStart, payloadLength),
            indices.Value,
            matrixCount,
            memory.Slice(matrixOffset, matrixBytes),
            flagBytes,
            lodFlags,
            tailBytes));
    }

    private static Result<ImmutableArray<int>> ReadStoredIndices(
        ReadOnlySpan<byte> data,
        MmbGeometryDescriptor descriptor,
        int payloadStart,
        int payloadLength,
        int ordinal)
    {
        if (!descriptor.IsIndexed)
        {
            // A direct record stores nothing to read. Its topology is
            // 0..vertexCount-1 and belongs with the vertices, not here.
            if (descriptor.BaseBias != 0)
            {
                return Malformed(ordinal, "is direct but declares a nonzero index bias");
            }

            if (descriptor.VertexCount % 3 != 0)
            {
                return Malformed(ordinal, "is direct but its vertex count is not a whole number of triangles");
            }

            return Result.Ok(ImmutableArray<int>.Empty);
        }

        if (descriptor.IndexCount % 3 != 0)
        {
            return Malformed(ordinal, "declares an index count that is not a whole number of triangles");
        }

        if (!BoundedRange.TryResolve(
                payloadLength, descriptor.IndexOffset, descriptor.IndexCount, sizeof(ushort),
                out int indexStart, out int indexBytes))
        {
            return Malformed(ordinal, "has an index buffer that does not lie inside its payload");
        }

        int count = (int)descriptor.IndexCount;
        SpanReader reader = new(data.Slice(payloadStart + indexStart, indexBytes));
        ImmutableArray<int>.Builder indices = ImmutableArray.CreateBuilder<int>(count);

        for (int i = 0; i < count; i++)
        {
            if (!reader.TryReadUInt16(out ushort stored))
            {
                return Malformed(ordinal, "has a truncated index buffer");
            }

            if (stored < descriptor.BaseBias)
            {
                return Malformed(ordinal, "stores an index below its own base bias");
            }

            long local = stored - (long)descriptor.BaseBias;
            if (local >= descriptor.VertexCount)
            {
                return Malformed(ordinal, "stores an index beyond its own vertex array");
            }

            indices.Add((int)local);
        }

        for (int triangle = 0; triangle + 2 < count; triangle += 3)
        {
            int a = indices[triangle];
            int b = indices[triangle + 1];
            int c = indices[triangle + 2];
            if (a == b || b == c || a == c)
            {
                return Malformed(ordinal, "stores a triangle that uses the same vertex twice");
            }
        }

        return Result.Ok(indices.MoveToImmutable());
    }

    private static Refusal Malformed(int ordinal, string problem) =>
        Refusal.Malformed(string.Create(
            CultureInfo.InvariantCulture, $"Model part {ordinal} {problem}."));
}

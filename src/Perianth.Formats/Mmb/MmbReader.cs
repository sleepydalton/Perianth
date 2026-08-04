using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Mmb;

/// <summary>
/// Finds and decodes the model-part records in an MMB file.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a signature scan, not a container grammar.</b> Specification
/// section 5.1 is explicit that the enclosing MMB structure was never derived:
/// what is proven, over the corpus and through the exporter, is that records
/// matching this exact envelope shape are the model parts and that nothing else
/// matches it. The scan is kept behind this type on purpose, so that no caller
/// can come to depend on searching bytes.
/// </para>
/// <para>
/// Two kinds of failure are possible while scanning and they are not the same.
/// A byte offset that does not match the envelope shape is simply not a record,
/// and scanning moves on — the file is a container this reader does not claim to
/// parse, so most offsets are not records. But once an envelope has matched, its
/// descriptor describes a record that genuinely exists, and an incoherent one is
/// a refusal rather than something to skip past. Silently dropping a located
/// record would lose geometry with no diagnostic, which is the guessing this
/// project refuses to do.
/// </para>
/// </remarks>
public static class MmbReader
{
    private const int MinimumLabelLength = 1;
    private const int MaximumLabelLength = 240;
    private const int ValueCount = 12;
    private const float ValueMagnitudeLimit = 1e6f;
    private const int DeclarationStride = 4;
    private const int DescriptorWordCount = 10;
    private const byte FirstPrintableAscii = 0x20;
    private const byte LastPrintableAscii = 0x7E;

    /// <summary>The exact bytes that separate the declarations from the descriptor.</summary>
    private static ReadOnlySpan<byte> DeclarationSuffix => [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xF0];

    /// <summary>Decodes every model-part record in <paramref name="file"/>.</summary>
    public static Result<MmbModel> Read(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ReadOnlyMemory<byte> memory = file.Memory;
        ReadOnlySpan<byte> data = memory.Span;

        // Scanning ascending means a later candidate always has the greater
        // start, so overwriting on a shared descriptor is exactly section 5.1's
        // "retain the latest record start". That rule is corpus-derived and its
        // reasoning is not reconstructible from the format; it is ported as
        // written, and it is what keeps a printable coincidence nested inside a
        // real record from being reported as a second record.
        Dictionary<int, EnvelopeCandidate> byDescriptor = [];
        for (int start = 0; start < data.Length; start++)
        {
            if (TryMatchEnvelope(data, start, out EnvelopeCandidate candidate))
            {
                byDescriptor[candidate.DescriptorOffset] = candidate;
            }
        }

        if (byDescriptor.Count == 0)
        {
            return Refusal.Malformed(
                "No model-part records were found. The file does not contain the envelope this reader recognizes.");
        }

        List<EnvelopeCandidate> candidates = [.. byDescriptor.Values];
        candidates.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        ImmutableArray<MmbModelPart>.Builder parts = ImmutableArray.CreateBuilder<MmbModelPart>(candidates.Count);
        for (int ordinal = 0; ordinal < candidates.Count; ordinal++)
        {
            Result<MmbModelPart> part = BuildPart(memory, candidates[ordinal], ordinal);
            if (!part.IsSuccess)
            {
                return part.Refusal;
            }

            parts.Add(part.Value);
        }

        return Result.Ok(new MmbModel(file.Path, parts.MoveToImmutable()));
    }

    /// <summary>
    /// Whether an envelope begins at <paramref name="start"/>. A false answer
    /// means "not a record here", never "this file is broken".
    /// </summary>
    private static bool TryMatchEnvelope(ReadOnlySpan<byte> data, int start, out EnvelopeCandidate candidate)
    {
        candidate = default;

        SpanReader reader = new(data);
        if (!reader.TrySeek(start) || !reader.TryReadUInt16(out ushort labelLength))
        {
            return false;
        }

        if (labelLength is < MinimumLabelLength or > MaximumLabelLength)
        {
            return false;
        }

        int labelOffset = reader.Position;
        if (!reader.TryReadBytes(labelLength, out ReadOnlySpan<byte> label))
        {
            return false;
        }

        foreach (byte character in label)
        {
            if (character is < FirstPrintableAscii or > LastPrintableAscii)
            {
                return false;
            }
        }

        ImmutableArray<float>.Builder values = ImmutableArray.CreateBuilder<float>(ValueCount);
        for (int i = 0; i < ValueCount; i++)
        {
            if (!reader.TryReadSingle(out float value) ||
                !float.IsFinite(value) ||
                Math.Abs(value) >= ValueMagnitudeLimit)
            {
                return false;
            }

            values.Add(value);
        }

        if (!reader.TryReadUInt16(out ushort zeroPrefix) || zeroPrefix != 0)
        {
            return false;
        }

        if (!reader.TryReadUInt16(out ushort declarationCount))
        {
            return false;
        }

        int declarationOffset = reader.Position;
        if (!reader.TrySkip((long)declarationCount * DeclarationStride))
        {
            return false;
        }

        if (!reader.TryReadBytes(DeclarationSuffix.Length, out ReadOnlySpan<byte> suffix) ||
            !suffix.SequenceEqual(DeclarationSuffix))
        {
            return false;
        }

        int descriptorOffset = reader.Position;
        if (!reader.TrySkip((long)DescriptorWordCount * sizeof(uint)))
        {
            return false;
        }

        candidate = new EnvelopeCandidate(
            start,
            labelOffset,
            labelLength,
            values.MoveToImmutable(),
            declarationCount,
            declarationOffset,
            descriptorOffset,
            reader.Position);
        return true;
    }

    private static Result<MmbModelPart> BuildPart(ReadOnlyMemory<byte> memory, EnvelopeCandidate candidate, int ordinal)
    {
        ReadOnlySpan<byte> data = memory.Span;
        SpanReader reader = new(data);
        if (!reader.TrySeek(candidate.DescriptorOffset))
        {
            return Malformed(ordinal, "has a descriptor outside the file");
        }

        Span<uint> words = stackalloc uint[DescriptorWordCount];
        for (int word = 0; word < DescriptorWordCount; word++)
        {
            if (!reader.TryReadUInt32(out uint value))
            {
                return Malformed(ordinal, "has a truncated descriptor");
            }

            words[word] = value;
        }

        MmbGeometryDescriptor descriptor = new(
            words[0], words[1], words[2], words[3], words[4],
            words[5], words[6], words[7], words[8], words[9]);

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
            new ByteRange(candidate.Start, candidate.End - candidate.Start),
            Encoding.ASCII.GetString(data.Slice(candidate.LabelOffset, candidate.LabelLength)),
            memory.Slice(candidate.LabelOffset, candidate.LabelLength),
            candidate.Values,
            candidate.DeclarationCount,
            memory.Slice(candidate.DeclarationOffset, candidate.DeclarationCount * DeclarationStride),
            descriptor,
            memory.Slice(payloadStart, payloadLength),
            indices.Value));
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

    private readonly record struct EnvelopeCandidate(
        int Start,
        int LabelOffset,
        int LabelLength,
        ImmutableArray<float> Values,
        int DeclarationCount,
        int DeclarationOffset,
        int DescriptorOffset,
        int End);
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Mmb;

/// <summary>
/// Serializes a model part's geometry payload, and copies a file that keeps the
/// one it has.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This does not write an MMB</strong>; <see cref="MmbContainerWriter"/>
/// does. What lives here is the contents of one payload — the identifiers a
/// record's vertices name, and for an indexed record the corners its triangles
/// draw — with no knowledge of what surrounds it.
/// </para>
/// <para>
/// Both payload kinds are written whole rather than patched, so both may change
/// length; the container writer recomputes every offset as it lays the file out.
/// That is a lifted restriction rather than an original design. It used to say
/// that a payload's length could never change, because a payload's position is
/// an absolute file offset "in a file nothing can re-index" — true while the
/// container was found by a signature scan, and untrue since it was derived.
/// </para>
/// <para>
/// <see cref="WithPayloads"/> is the remaining in-place path and writes no
/// payload at all. It exists so that an edit which changed nothing produces the
/// bytes it read, rather than a re-serialization that merely ought to match.
/// </para>
/// </remarks>
public static class MmbWriter
{
    /// <summary>
    /// Returns <paramref name="original"/> with the named records' payloads
    /// replaced.
    /// </summary>
    /// <param name="original">The file the payloads were read from.</param>
    /// <param name="model">Its decoded records, which say where each payload sits.</param>
    /// <param name="payloads">Replacement bytes, keyed by source ordinal.</param>
    public static Result<byte[]> WithPayloads(
        SourceFile original, MmbModel model, IReadOnlyDictionary<int, byte[]> payloads)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(payloads);

        byte[] bytes = original.Memory.ToArray();

        foreach ((int ordinal, byte[] payload) in payloads)
        {
            if (ordinal < 0 || ordinal >= model.Parts.Length)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The model has {model.Parts.Length} parts, so there is no part {ordinal} to write."));
            }

            MmbModelPart part = model.Parts[ordinal];
            MmbGeometryDescriptor descriptor = part.Descriptor;

            if (payload.Length != descriptor.PayloadLength)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Part {ordinal} holds {descriptor.PayloadLength} bytes and the replacement is {payload.Length}. A payload cannot change length: its position in the file is recorded as an absolute offset, so growing or shrinking one would move every part after it."));
            }

            long start = descriptor.PayloadOffset;
            if (start < 0 || start + payload.Length > bytes.Length)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Part {ordinal} names a payload outside the file."));
            }

            payload.CopyTo(bytes.AsSpan((int)start));
        }

        return Result.Ok(bytes);
    }

    /// <summary>
    /// An indexed record's payload built afresh, at whatever size its contents
    /// need.
    /// </summary>
    /// <param name="localIds">One pool identifier per vertex, in vertex order.</param>
    /// <param name="indices">One vertex number per corner, in triangle order.</param>
    /// <param name="baseBias">The bias the record's stored indices carry.</param>
    /// <remarks>
    /// <para>
    /// Two arrays rather than one, which is the whole difference from
    /// <see cref="DirectPayload"/>: identifiers first, index buffer immediately
    /// after, nothing else. So the index offset is the identifiers' length, and
    /// the caller writes that into the descriptor along with the two counts.
    /// </para>
    /// <para>
    /// There is one identifier per <em>vertex</em>, not per distinct point. A
    /// corner names a vertex, so the array is indexed by vertex number, and two
    /// vertices sitting at the same place share a pool slot while still holding
    /// an entry each. The distinct count is the record's slice of the pool and a
    /// different number.
    /// </para>
    /// <para>
    /// Writing the payload rather than overwriting it is what lets an indexed
    /// part be resized, and what it needs in exchange is that the payload hold no
    /// byte nothing has decoded — <see
    /// cref="MmbGeometryDescriptor.AccountsForEveryByte"/> is that check, and
    /// making it is the caller's. An earlier version preserved the payload by
    /// overwriting the two arrays inside it, which kept every other byte and
    /// therefore could not change length. It was removed rather than kept as a
    /// second path: the population it served turned out to be empty, since every
    /// one of the 1,595 editable indexed records accounts for every byte
    /// (Roadmap §10.58).
    /// </para>
    /// <para>
    /// An index is stored biased: the reader subtracts
    /// <see cref="MmbGeometryDescriptor.BaseBias"/> from every one, so this adds
    /// it back. Writing the unbiased number would produce a file that loads and
    /// draws the wrong triangles wherever the bias is not zero. It is zero on all
    /// 6,139 indexed records measured, and still read off the record rather than
    /// assumed, because a writer that normalises a field it was given is a writer
    /// with an opinion.
    /// </para>
    /// </remarks>
    public static Result<byte[]> RebuiltIndexedPayload(
        IReadOnlyList<int> localIds, IReadOnlyList<int> indices, uint baseBias)
    {
        ArgumentNullException.ThrowIfNull(localIds);
        ArgumentNullException.ThrowIfNull(indices);

        if (indices.Count % 3 != 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"An index buffer of {indices.Count} corners is not a whole number of triangles."));
        }

        byte[] bytes = new byte[(localIds.Count + indices.Count) * sizeof(ushort)];
        for (int i = 0; i < localIds.Count; i++)
        {
            if (localIds[i] < 0 || localIds[i] > ushort.MaxValue)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Vertex {i} names pool slot {localIds[i]}, and a slot is stored as a u16."));
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(i * sizeof(ushort)), (ushort)localIds[i]);
        }

        int start = localIds.Count * sizeof(ushort);
        for (int i = 0; i < indices.Count; i++)
        {
            if (indices[i] < 0 || indices[i] >= localIds.Count)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Corner {i} names vertex {indices[i]}, and the record has {localIds.Count}."));
            }

            long stored = indices[i] + (long)baseBias;
            if (stored > ushort.MaxValue)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Corner {i} would be stored as {stored}, and an index is a u16."));
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(start + (i * sizeof(ushort))), (ushort)stored);
        }

        return Result.Ok(bytes);
    }

    /// <summary>
    /// Serializes a direct record's per-vertex pool identifiers.
    /// </summary>
    /// <remarks>
    /// One little-endian <c>u16</c> per vertex, in vertex order, which is what
    /// <c>ReadMode3LocalIds</c> reads back. A direct record stores nothing else,
    /// so this is its whole payload.
    /// </remarks>
    public static Result<byte[]> DirectPayload(IReadOnlyList<int> localIds)
    {
        ArgumentNullException.ThrowIfNull(localIds);

        byte[] bytes = new byte[localIds.Count * sizeof(ushort)];
        for (int i = 0; i < localIds.Count; i++)
        {
            if (localIds[i] < 0 || localIds[i] > ushort.MaxValue)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Vertex {i} names pool slot {localIds[i]}, and a slot is stored as a u16."));
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(i * sizeof(ushort)), (ushort)localIds[i]);
        }

        return Result.Ok(bytes);
    }
}

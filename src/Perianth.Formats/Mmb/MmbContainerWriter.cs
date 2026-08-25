using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Mmb;

/// <summary>
/// Writes a whole MMB file back from what <see cref="MmbReader"/> read.
/// </summary>
/// <remarks>
/// <para>
/// <b>A writer must have no opinions.</b> It normalises no name, sorts nothing,
/// supplies no absent field and upgrades no version. Every one of those is an
/// improvement that would break the only test capable of showing the writer
/// works — read a real file, write it back, require the bytes to match. The
/// asymmetry behind the rule: a wrong read refuses, while a wrong write produces
/// a file that loads and misbehaves, which the game renders without complaint.
/// </para>
/// <para>
/// The layout is fully determined, measured over 2,283 files: payloads begin
/// exactly where the part table ends, follow part order, and are gapless,
/// non-overlapping and exhaustive to the last byte of the file. So this
/// reconstructs the file rather than patching one, and there is no unmodelled
/// remainder to carry along.
/// </para>
/// <para>
/// The one field not written as read is the header's declared length, which is
/// set from the length actually produced. On every file measured it is the
/// file's own size; a file that disagreed with itself would be corrected rather
/// than reproduced, and the round-trip oracle would say so rather than this
/// hiding it.
/// </para>
/// </remarks>
public static class MmbContainerWriter
{
    private const int VersionMask = 0x3F;
    private const int MatrixByteCount = 64;
    private const int DeclarationStride = 4;

    /// <summary>Serializes <paramref name="model"/> as an MMB file.</summary>
    public static Result<byte[]> Write(MmbModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Version is < 6 or > VersionMask)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This model declares version {model.Version}, which this build does not write."));
        }

        List<byte> bytes = [];
        bytes.Add((byte)'M');
        bytes.Add((byte)'M');
        bytes.Add((byte)'B');
        bytes.Add((byte)(model.Version | (model.HeaderFlags << 6)));

        int declaredLengthAt = bytes.Count;
        AddUInt32(bytes, 0);                        // filled in once the length is known

        AddUInt32(bytes, (uint)model.Nodes.Length);
        foreach (MmbNode node in model.Nodes)
        {
            if (node.MatrixBytes.Length != MatrixByteCount)
            {
                return Refusal.Malformed(
                    "A node carries a matrix that is not sixty-four bytes.");
            }

            AddUInt16(bytes, checked((ushort)node.NameBytes.Length));
            bytes.AddRange(node.NameBytes.Span);
            bytes.AddRange(node.MatrixBytes.Span);
            AddUInt16(bytes, node.Trailer);
        }

        AddUInt32(bytes, (uint)model.Parts.Length);

        // Two passes over the parts. The payload offset a descriptor carries is
        // absolute, so it cannot be written until the table's length is known,
        // and the table's length cannot be known until every part is laid out.
        // The first pass writes the records with the offsets left as read; the
        // second corrects them.
        List<int> payloadOffsetPositions = [];
        foreach (MmbModelPart part in model.Parts)
        {
            Result<int> written = WritePart(bytes, part, model.Version);
            if (!written.IsSuccess)
            {
                return written.Refusal;
            }

            payloadOffsetPositions.Add(written.Value);
        }

        int payloadStart = bytes.Count;
        int cursor = payloadStart;
        for (int i = 0; i < model.Parts.Length; i++)
        {
            int position = payloadOffsetPositions[i];
            if (position >= 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[position..],
                    (uint)cursor);
            }

            bytes.AddRange(model.Parts[i].Payload.Span);
            cursor += model.Parts[i].Payload.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[declaredLengthAt..],
            (uint)bytes.Count);

        return Result.Ok(bytes.ToArray());
    }

    /// <summary>
    /// Writes one part, returning where its payload offset word landed, or -1
    /// where the version stores no such word.
    /// </summary>
    private static Result<int> WritePart(List<byte> bytes, MmbModelPart part, int version)
    {
        AddUInt16(bytes, checked((ushort)part.LabelBytes.Length));
        bytes.AddRange(part.LabelBytes.Span);

        foreach (float value in part.Values)
        {
            AddUInt32(bytes, (uint)BitConverter.SingleToInt32Bits(value));
        }

        int expectedFlags = (version >= 8 ? 1 : 0) + (version > 9 ? 1 : 0);
        if (part.FlagBytes.Length != expectedFlags)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Model part {part.SourceOrdinal} carries {part.FlagBytes.Length} flag bytes where version {version} writes {expectedFlags}."));
        }

        bytes.AddRange(part.FlagBytes.Span);

        if (part.DeclarationBytes.Length != part.DeclarationCount * DeclarationStride)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Model part {part.SourceOrdinal} declares {part.DeclarationCount} declarations and carries {part.DeclarationBytes.Length} bytes of them."));
        }

        AddUInt16(bytes, checked((ushort)part.DeclarationCount));
        bytes.AddRange(part.DeclarationBytes.Span);

        AddUInt16(bytes, checked((ushort)part.MatrixCount));
        bytes.AddRange(part.MatrixBytes.Span);

        // One level of detail. The reader refuses anything else, so this is the
        // count it must write rather than a choice made here.
        bytes.Add(1);
        if (version >= 7)
        {
            AddUInt32(bytes, part.LodFlags);
        }

        int payloadOffsetPosition = WriteDescriptor(bytes, part.Descriptor, version);
        bytes.AddRange(part.TailBytes.Span);
        return Result.Ok(payloadOffsetPosition);
    }

    /// <summary>
    /// Writes the descriptor at the width the version carries, returning where
    /// the payload offset landed.
    /// </summary>
    /// <remarks>
    /// Word 5 is not written below version 8, where the loader copies word 4
    /// into it, and word 9 not below version 11. Writing the omitted words would
    /// shift every later field and produce a file the game reads as something
    /// else entirely.
    /// </remarks>
    private static int WriteDescriptor(List<byte> bytes, MmbGeometryDescriptor descriptor, int version)
    {
        AddUInt32(bytes, descriptor.VertexCount);
        AddUInt32(bytes, descriptor.BaseBias);
        AddUInt32(bytes, descriptor.IndexCount);
        AddUInt32(bytes, descriptor.SecondaryVertexCount);
        AddUInt32(bytes, descriptor.Stream0Offset);

        if (version >= 8)
        {
            AddUInt32(bytes, descriptor.AuxiliaryStreamOffset);
        }

        AddUInt32(bytes, descriptor.IndexOffset);

        int payloadOffsetPosition = -1;
        if (version > 8)
        {
            payloadOffsetPosition = bytes.Count;
            AddUInt32(bytes, descriptor.PayloadOffset);
            AddUInt32(bytes, descriptor.PayloadLength);
        }

        if (version > 10)
        {
            AddUInt32(bytes, descriptor.Mode3Reserved);
        }

        return payloadOffsetPosition;
    }

    private static void AddUInt16(List<byte> target, ushort value)
    {
        target.Add((byte)(value & 0xFF));
        target.Add((byte)(value >> 8));
    }

    private static void AddUInt32(List<byte> target, uint value)
    {
        target.Add((byte)(value & 0xFF));
        target.Add((byte)((value >> 8) & 0xFF));
        target.Add((byte)((value >> 16) & 0xFF));
        target.Add((byte)(value >> 24));
    }
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Perianth.Tests.Mmb;

/// <summary>
/// Builds synthetic MMB bytes. Nothing here comes from a game file; every
/// envelope is assembled from the field layout in specification section 5.1.
/// </summary>
internal sealed class MmbFileBuilder
{
    public string Label { get; set; } = "part";

    public float[] Values { get; set; } = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    public ushort ZeroPrefix { get; set; }

    public byte[] Declarations { get; set; } = [];

    public byte[] Suffix { get; set; } = [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xF0];

    /// <summary>Stored index values, or null for a direct record.</summary>
    public ushort[]? Indices { get; set; }

    /// <summary>
    /// Position stream entries: UInt32 pool identifiers in mode 2, UInt16 local
    /// identifiers in mode 3. Null leaves the payload as an index buffer alone.
    /// </summary>
    public uint[]? PositionEntries { get; set; }

    /// <summary>Bytes per position entry: 4 for mode 2, 2 for mode 3.</summary>
    public int EntrySize { get; set; } = sizeof(uint);

    /// <summary>
    /// Bytes placed between the position stream and the index buffer, which is
    /// where an auxiliary stream would sit and where an unexplained gap is.
    /// </summary>
    public int GapBytes { get; set; }

    public uint VertexCount { get; set; } = 3;

    public uint BaseBias { get; set; }

    /// <summary>Runs after the descriptor is filled in, to break one field.</summary>
    public Action<uint[]>? Adjust { get; set; }

    /// <summary>Bytes placed before the envelope, shifting every offset.</summary>
    public byte[] Lead { get; set; } = [];

    /// <summary>
    /// How many identical records to lay down, one after another. Each gets its
    /// own descriptor at its own offset, so they are distinct records rather
    /// than one found repeatedly.
    /// </summary>
    public int Repeat { get; set; } = 1;

    public byte[] Build()
    {
        List<byte> file = [.. Lead];
        for (int record = 0; record < Repeat; record++)
        {
            file.AddRange(BuildRecord(file.Count));
        }

        return [.. file];
    }

    private byte[] BuildRecord(int recordOffset)
    {
        byte[] label = Encoding.ASCII.GetBytes(Label);
        List<byte> envelope = [];

        AddUInt16(envelope, (ushort)label.Length);
        envelope.AddRange(label);
        foreach (float value in Values)
        {
            AddUInt32(envelope, (uint)BitConverter.SingleToInt32Bits(value));
        }

        AddUInt16(envelope, ZeroPrefix);
        AddUInt16(envelope, (ushort)(Declarations.Length / 4));
        envelope.AddRange(Declarations);
        envelope.AddRange(Suffix);

        // Without position entries the payload is only an index buffer, which is
        // all the envelope tests need. With them it is the real layout: the
        // position stream first, then the indices after it.
        List<byte> payloadBytes = [];
        if (PositionEntries is not null)
        {
            foreach (uint entry in PositionEntries)
            {
                if (EntrySize == sizeof(ushort))
                {
                    payloadBytes.Add((byte)(entry & 0xFF));
                    payloadBytes.Add((byte)((entry >> 8) & 0xFF));
                }
                else
                {
                    AddUInt32(payloadBytes, entry);
                }
            }
        }

        for (int i = 0; i < GapBytes; i++)
        {
            payloadBytes.Add((byte)(0x50 + i));
        }

        int indexOffset = payloadBytes.Count;
        foreach (ushort index in Indices ?? [])
        {
            payloadBytes.Add((byte)(index & 0xFF));
            payloadBytes.Add((byte)(index >> 8));
        }

        if (PositionEntries is null && Indices is null)
        {
            payloadBytes.AddRange(new byte[12]);
        }

        byte[] payload = [.. payloadBytes];

        int payloadOffset = recordOffset + envelope.Count + (10 * sizeof(uint));
        uint[] descriptor =
        [
            VertexCount,
            BaseBias,
            (uint)(Indices?.Length ?? 0),
            VertexCount,
            0,
            0,
            (uint)indexOffset,
            (uint)payloadOffset,
            (uint)payload.Length,
            0,
        ];

        Adjust?.Invoke(descriptor);
        foreach (uint word in descriptor)
        {
            AddUInt32(envelope, word);
        }

        return [.. envelope, .. payload];
    }

    /// <summary>Where the envelope's twelve floats begin, given the current label.</summary>
    public int ValuesOffset => Lead.Length + 2 + Label.Length;

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

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Perianth.Tests.Mmb;

/// <summary>
/// Builds synthetic MMB bytes. Nothing here comes from a game file; the
/// container and part layout are assembled from Roadmap §10.53's transcription
/// of the loader.
/// </summary>
/// <remarks>
/// Payloads are laid down after every part record rather than beside the record
/// that owns them. That is not decoration: the reader walks parts in sequence,
/// so a payload sitting between two records would be read as the next record's
/// fields. The descriptor's payload offset is absolute, which is what lets them
/// live anywhere.
/// </remarks>
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

    /// <summary>
    /// Bytes placed after the index buffer, which the payload's declared length
    /// covers and no array accounts for.
    /// </summary>
    /// <remarks>
    /// No editable record in the corpus has any — 1,595 of 1,595 account for
    /// every byte — which is why this exists. The guard that refuses such a
    /// payload rather than rebuilding it is unreachable from real files, so a
    /// synthetic one is the only thing that can show it is load-bearing.
    /// </remarks>
    public int TrailingBytes { get; set; }

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

    /// <summary>The container version written into the magic's fourth byte.</summary>
    public int Version { get; set; } = 11;

    /// <summary>How many nodes to declare, each with an empty name.</summary>
    public int NodeCount { get; set; }

    /// <summary>
    /// Names for the node table, where a test needs a part to bind to one.
    /// </summary>
    /// <remarks>
    /// The table used to be written with empty names, which is enough for a
    /// reader test and not for anything that resolves a part's binding — a
    /// part's label names a node of its own model, so a test of that rule needs
    /// a table that says something. Setting this replaces <see cref="NodeCount"/>.
    /// </remarks>
    public string[]? NodeNames { get; set; }

    /// <summary>Overrides the four-byte magic, for the containers that refuse.</summary>
    public byte[]? Magic { get; set; }

    public byte[] Build()
    {
        List<byte> file = [];
        file.AddRange(Magic ?? [(byte)'M', (byte)'M', (byte)'B', (byte)Version]);
        AddUInt32(file, 0);                       // the declared length, unread
        string[] names = NodeNames ?? new string[NodeCount];
        AddUInt32(file, (uint)names.Length);
        for (int node = 0; node < names.Length; node++)
        {
            byte[] name = Encoding.ASCII.GetBytes(names[node] ?? "");
            AddUInt16(file, (ushort)name.Length);
            file.AddRange(name);
            file.AddRange(new byte[64]);          // the matrix
            AddUInt16(file, 0);
        }

        AddUInt32(file, (uint)Repeat);
        file.AddRange(Lead);

        // Two passes: the records first, so their length is known, and then the
        // payloads after them at the absolute offsets the descriptors name.
        List<byte> records = [];
        List<byte> payloads = [];
        int recordBytes = RecordLength();
        for (int record = 0; record < Repeat; record++)
        {
            int recordOffset = file.Count + (record * recordBytes);
            int payloadOffset = file.Count + (Repeat * recordBytes) + payloads.Count;
            records.AddRange(BuildRecord(recordOffset, payloadOffset, payloads));
        }

        file.AddRange(records);
        file.AddRange(payloads);
        return [.. file];
    }

    /// <summary>
    /// One record's length, which every record shares because they are identical.
    /// </summary>
    private int RecordLength()
    {
        List<byte> scratch = [];
        BuildRecord(0, 0, scratch);
        return BuildRecord(0, 0, []).Length;
    }

    private byte[] BuildRecord(int recordOffset, int payloadOffset, List<byte> payloads)
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

        for (int i = 0; i < TrailingBytes; i++)
        {
            payloadBytes.Add((byte)(0x70 + i));
        }

        if (PositionEntries is null && Indices is null)
        {
            payloadBytes.AddRange(new byte[12]);
        }

        byte[] payload = [.. payloadBytes];

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

        // The fields between the descriptor and the next part. The loader reads
        // them; nothing here does, and they must still be present or every
        // later record starts at the wrong byte.
        AddUInt32(envelope, 0);
        AddUInt32(envelope, 0);
        envelope.Add(0);                          // an empty extra-word block
        AddUInt16(envelope, 0);
        AddUInt16(envelope, 0);
        AddUInt32(envelope, 0);
        AddUInt32(envelope, 0);
        AddUInt32(envelope, 0);
        AddUInt32(envelope, 0);

        payloads.AddRange(payload);
        return [.. envelope];
    }

    /// <summary>Where the first record's twelve floats begin.</summary>
    public int ValuesOffset => HeaderLength + Lead.Length + 2 + Label.Length;

    /// <summary>The container before the first record.</summary>
    public int HeaderLength => 4 + 4 + 4 + (NodeCount * 68) + 4;   // empty node names only

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

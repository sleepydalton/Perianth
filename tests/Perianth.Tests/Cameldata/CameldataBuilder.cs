using System;
using System.Collections.Generic;
using System.Numerics;

namespace Perianth.Tests.Cameldata;

/// <summary>
/// Builds synthetic cameldata bytes from the field layouts in specification
/// sections 5.2 and 5.3.
/// </summary>
internal sealed class CameldataBuilder
{
    public int Mode { get; set; } = 2;

    public int Flags { get; set; }

    /// <summary>Replaces the computed header word entirely.</summary>
    public uint? HeaderWord { get; set; }

    /// <summary>Declared constant count, which may disagree with what is written.</summary>
    public uint? DeclaredConstantCount { get; set; }

    public int ConstantCount { get; set; } = 1;

    public uint[] BezierWords { get; set; } = [];

    /// <summary>Whether the sixteen bytes at +48 are filler rather than coverage ranges.</summary>
    /// <remarks>
    /// For the reader's own test, which asserts those bytes survive uninterpreted.
    /// Everything else wants ranges that describe the fixture, because a resize
    /// re-cuts them and checks them first.
    /// </remarks>
    public bool ArbitraryDataIndices { get; set; }

    /// <summary>Each record's draw vertex count, when it is not its pool slice.</summary>
    public int[]? VertexCounts { get; set; }

    /// <summary>The very first float of the first constant, so it can be made non-finite.</summary>
    public float FirstFloat { get; set; } = 1f;

    /// <summary>Packed flags written into every mode-3 constant.</summary>
    public uint PackedFlags { get; set; }

    /// <summary>Per-constant packed flags, where they must differ from each other.</summary>
    public uint[]? PerConstantPackedFlags { get; set; }

    /// <summary>
    /// Each constant's base into the XY and Z pools, when more than one constant
    /// must own a slice of its own. Null writes zero, which is right for a file
    /// with one constant and wrong for any other.
    /// </summary>
    public uint[]? XyBases { get; set; }

    /// <inheritdoc cref="XyBases"/>
    public uint[]? ZBases { get; set; }

    /// <summary>Per-constant UV0 bases, for the unified-UV0 population.</summary>
    public uint[]? Uv0Bases { get; set; }

    public Vector3[] Positions { get; set; } = [new(1, 2, 3)];

    /// <summary>Declared mode-2 position count, which may disagree with what is written.</summary>
    public uint? DeclaredPositionCount { get; set; }

    public Vector2[] Xy { get; set; } = [new(1, 2)];

    public float[] Z { get; set; } = [3f];

    public uint[] Uv0 { get; set; } = [0x0000_4000];

    public uint[] PackedZ { get; set; } = [0u];

    public byte[] Trailing { get; set; } = [];

    /// <summary>Cuts the finished file down to this many bytes.</summary>
    public int? TruncateTo { get; set; }

    public byte[] Build()
    {
        List<byte> file = [];

        uint header = HeaderWord ?? (uint)(Mode | (Flags << 15));
        AddUInt32(file, header);
        AddUInt32(file, DeclaredConstantCount ?? (uint)ConstantCount);
        uint[] bezier = BezierWords.Length == 0 && !ArbitraryDataIndices && Mode == 3
            ? new uint[CoverageWordTotal()]
            : BezierWords;

        AddUInt32(file, (uint)bezier.Length);
        foreach (uint word in bezier)
        {
            AddUInt32(file, word);
        }

        for (int i = 0; i < ConstantCount; i++)
        {
            if (Mode == 3)
            {
                AddMode3Constant(file, i);
            }
            else
            {
                AddMode2Constant(file, i);
            }
        }

        if (Mode == 3)
        {
            AddUInt32(file, (uint)Xy.Length);
            foreach (Vector2 value in Xy)
            {
                AddSingle(file, value.X);
                AddSingle(file, value.Y);
            }

            AddUInt32(file, (uint)Z.Length);
            foreach (float value in Z)
            {
                AddSingle(file, value);
            }

            AddUInt32(file, (uint)Uv0.Length);
            foreach (uint value in Uv0)
            {
                AddUInt32(file, value);
            }

            AddUInt32(file, (uint)PackedZ.Length);
            foreach (uint value in PackedZ)
            {
                AddUInt32(file, value);
            }
        }
        else
        {
            AddUInt32(file, DeclaredPositionCount ?? (uint)Positions.Length);
            foreach (Vector3 position in Positions)
            {
                AddSingle(file, position.X);
                AddSingle(file, position.Y);
                AddSingle(file, position.Z);
            }
        }

        file.AddRange(Trailing);

        byte[] bytes = [.. file];
        return TruncateTo is int limit && limit < bytes.Length ? bytes[..limit] : bytes;
    }

    private void AddMode2Constant(List<byte> file, int ordinal)
    {
        AddSurface(file, ordinal);
        AddMatrix(file);
        AddSingle(file, 2f);
        AddSingle(file, 0.5f);
        AddTail(file);
    }

    private void AddMode3Constant(List<byte> file, int ordinal)
    {
        AddSurface(file, ordinal);
        AddUInt32(file, XyBases is null ? 0 : XyBases[ordinal]);
        AddUInt32(file, ZBases is null ? 0 : ZBases[ordinal]);
        AddUInt32(file, Uv0Bases is null ? 0 : Uv0Bases[ordinal]);
        AddUInt32(file, PerConstantPackedFlags is null ? PackedFlags : PerConstantPackedFlags[ordinal]);
        AddMatrix(file);
        AddSingle(file, 2f);
        AddSingle(file, 0.5f);
        AddTail(file);
    }

    private void AddSurface(List<byte> file, int ordinal)
    {
        // Surface origin, U and V, then the sixteen bytes at +48.
        AddSingle(file, ordinal == 0 ? FirstFloat : 1f);
        for (int i = 1; i < 12; i++)
        {
            AddSingle(file, i);
        }

        if (ArbitraryDataIndices)
        {
            for (int i = 0; i < 16; i++)
            {
                file.Add((byte)(0xA0 + i));
            }

            return;
        }

        // Curved-coverage ranges that tile, as every shipped record's do. A
        // fixture carrying arbitrary bytes here is not a cameldata a resize can
        // be tried against, and 27 tests said so the moment the re-cut started
        // checking: the layout is load-bearing, not decoration.
        int signWords = SignWordsFor(VertexCountOf(ordinal));
        int bitsWords = BitsWordsFor(VertexCountOf(ordinal));
        int signBase = 0;
        for (int before = 0; before < ordinal; before++)
        {
            signBase += SignWordsFor(VertexCountOf(before)) + BitsWordsFor(VertexCountOf(before));
        }

        AddUInt32(file, (uint)signBase);
        AddUInt32(file, (uint)signWords);
        AddUInt32(file, (uint)(signBase + signWords));
        AddUInt32(file, (uint)bitsWords);
    }

    /// <summary>How many vertices a record declares, for its coverage ranges.</summary>
    /// <remarks>
    /// Defaults to the record's slice of the XY pool, which is its vertex count
    /// on every fixture here because none repeats a position. A fixture that
    /// does must say so, since the two numbers part company exactly there.
    /// </remarks>
    private int VertexCountOf(int ordinal)
    {
        if (VertexCounts is not null)
        {
            return VertexCounts[ordinal];
        }

        if (XyBases is null)
        {
            return Xy?.Length ?? 0;
        }

        int start = (int)XyBases[ordinal];
        int end = ordinal + 1 < XyBases.Length ? (int)XyBases[ordinal + 1] : Xy?.Length ?? 0;
        return Math.Max(end - start, 0);
    }

    private static int SignWordsFor(int vertices) => (vertices + 31) / 32;

    private static int BitsWordsFor(int vertices) => (vertices + 15) / 16;

    private int CoverageWordTotal()
    {
        int total = 0;
        for (int ordinal = 0; ordinal < ConstantCount; ordinal++)
        {
            total += SignWordsFor(VertexCountOf(ordinal)) + BitsWordsFor(VertexCountOf(ordinal));
        }

        return total;
    }

    private static void AddMatrix(List<byte> file)
    {
        for (int i = 0; i < 16; i++)
        {
            AddSingle(file, i);
        }
    }

    private void AddTail(List<byte> file)
    {
        if (Flags == 0)
        {
            return;
        }

        for (int i = 0; i < 8; i++)
        {
            file.Add((byte)(0xE0 + i));
        }
    }

    private static void AddSingle(List<byte> file, float value) =>
        AddUInt32(file, (uint)BitConverter.SingleToInt32Bits(value));

    private static void AddUInt32(List<byte> file, uint value)
    {
        file.Add((byte)(value & 0xFF));
        file.Add((byte)((value >> 8) & 0xFF));
        file.Add((byte)((value >> 16) & 0xFF));
        file.Add((byte)(value >> 24));
    }
}

using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Perianth.Formats.Cameldata;

/// <summary>
/// One mode-2 constant record, at stride <c>136 + (flags ? 8 : 0)</c>.
/// </summary>
/// <param name="SurfaceOrigin">The surface origin, used to project UV0.</param>
/// <param name="SurfaceU">The surface U axis.</param>
/// <param name="SurfaceV">The surface V axis.</param>
/// <param name="DataIndices">Sixteen bytes at +48, kept and not interpreted.</param>
/// <param name="InverseLocal">The inverse-local matrix.</param>
/// <param name="PositionXScale">The position-X scale at +128.</param>
/// <param name="InverseUnitScale">The inverse unit scale at +132.</param>
/// <param name="OptionalTail">The eight-byte tail present only when the header flag is set.</param>
public readonly record struct Mode2Constant(
    Vector4 SurfaceOrigin,
    Vector4 SurfaceU,
    Vector4 SurfaceV,
    ReadOnlyMemory<byte> DataIndices,
    SerializedMatrix InverseLocal,
    float PositionXScale,
    float InverseUnitScale,
    ReadOnlyMemory<byte> OptionalTail);

/// <summary>
/// One mode-3 constant record, at stride <c>152 + (flags ? 8 : 0)</c>.
/// </summary>
/// <param name="SurfaceOrigin">The surface origin.</param>
/// <param name="SurfaceU">The surface U axis.</param>
/// <param name="SurfaceV">The surface V axis.</param>
/// <param name="DataIndices">Sixteen bytes at +48, kept and not interpreted.</param>
/// <param name="XyBase">Base index into the XY array.</param>
/// <param name="ZBase">Base index into the Z array.</param>
/// <param name="Uv0Base">Base index into the UV0 array.</param>
/// <param name="PackedFlags">Unified-UV0 selector, UV scale index and Z bit width.</param>
/// <param name="InverseLocal">The inverse-local matrix.</param>
/// <param name="PositionXScale">The position-X scale at +144.</param>
/// <param name="InverseUnitScale">The inverse unit scale at +148.</param>
/// <param name="OptionalTail">The eight-byte tail present only when the header flag is set.</param>
public readonly record struct Mode3Constant(
    Vector4 SurfaceOrigin,
    Vector4 SurfaceU,
    Vector4 SurfaceV,
    ReadOnlyMemory<byte> DataIndices,
    uint XyBase,
    uint ZBase,
    uint Uv0Base,
    uint PackedFlags,
    SerializedMatrix InverseLocal,
    float PositionXScale,
    float InverseUnitScale,
    ReadOnlyMemory<byte> OptionalTail)
{
    /// <summary>Bit 0: whether UV0 comes from the unified packed array.</summary>
    public bool UsesUnifiedUv0 => (PackedFlags & 1) != 0;

    /// <summary>Bits 1 and 2: which entry of the UV scale table applies.</summary>
    /// <remarks>Index 3 has no scale and refuses where it is used.</remarks>
    public int Uv0ScaleIndex => (int)((PackedFlags >> 1) & 3);

    /// <summary>Bits 3 to 7, plus one: the width of a packed Z index, from 1 to 32.</summary>
    public int ZBitWidth => (int)((PackedFlags >> 3) & 0x1F) + 1;

    /// <summary>Bits 16 and above: how many depth planes this record owns.</summary>
    /// <remarks>
    /// The record's slice of the Z pool, restated in its own flag word. It holds
    /// on 5,399,482 records of 5,399,482 across the corpus, and it is derived
    /// rather than authored, so anything that resizes the slice has to move it
    /// too. Roadmap §10.107 records the resize that did not, and what the game
    /// drew as a result.
    /// </remarks>
    public int DepthCount => (int)(PackedFlags >> 16);

    /// <summary>
    /// This constant pointed at a new slice of the Z pool, with the restated
    /// depth count moved to match.
    /// </summary>
    /// <remarks>
    /// The base and the count are one fact stated twice, so they are set
    /// together and there is no way to set only one. That is the same rule
    /// <c>WithGeometry</c> follows for the MMB tail, and for the same reason:
    /// four stale derived fields in one day, and this is the sixth overall.
    /// </remarks>
    public Mode3Constant WithZSlice(int zBase, int depths) => this with
    {
        ZBase = (uint)zBase,
        PackedFlags = (PackedFlags & 0xFFFFu) | ((uint)depths << 16),
    };

    /// <summary>Where this record's curved-coverage sign bits start, in words.</summary>
    /// <remarks>
    /// <para>
    /// The sixteen bytes at <c>+48</c> are <c>myDataIndices</c>, and the shader
    /// header names all four:
    /// <c>(myBezierCurveBitsStartIndex, myBezierCurveBitsLength,
    /// myBezierQuadParamBitsStartIndex, myBezierQuadParamBitsLength)</c>. They
    /// address the Bezier buffer, whose per-vertex selectors trim fragments
    /// inside the shipped triangles — so a part is not the polygon its triangles
    /// describe, it is that polygon with curves cut out of it.
    /// </para>
    /// <para>
    /// Two of the four are <b>derived from the vertex count</b>, which makes
    /// them the fifth stale derived field this project has had to chase:
    /// <c>signWords == ceil(n/32)</c> and <c>bitsWords == ceil(n/16)</c>, with
    /// <c>bitsBase == signBase + signWords</c>. All three hold on 597 posed
    /// characters and on 81,771 of 81,771 records across 521 prop files, where
    /// the slices also tile the buffer exactly with no gap and no overlap.
    /// Roadmap §10.154 and §10.155.
    /// </para>
    /// <para>
    /// <b>n is the draw vertex count, not the pool slice.</b> A mesh that
    /// repeats a position has more vertices than distinct points, and taking the
    /// slice length instead is the mistake `Plan.VertexCount` already made once.
    /// </para>
    /// </remarks>
    public uint CoverageSignBase => DataIndex(0);

    /// <summary>How many words of sign bits, one per vertex.</summary>
    public uint CoverageSignWords => DataIndex(1);

    /// <summary>Where the two-bit corner selectors start, in words.</summary>
    public uint CoverageBitsBase => DataIndex(2);

    /// <summary>How many words of selectors, two bits per vertex.</summary>
    public uint CoverageBitsWords => DataIndex(3);

    /// <summary>How many sign words a record of this many vertices needs.</summary>
    public static int CoverageSignWordsFor(int vertices) => (vertices + 31) / 32;

    /// <summary>How many selector words a record of this many vertices needs.</summary>
    public static int CoverageBitsWordsFor(int vertices) => (vertices + 15) / 16;

    /// <summary>How many words of the Bezier buffer a record of this size occupies.</summary>
    public static int CoverageWordsFor(int vertices) =>
        CoverageSignWordsFor(vertices) + CoverageBitsWordsFor(vertices);

    /// <summary>Whether this record's four coverage fields agree with a vertex count.</summary>
    /// <remarks>
    /// The guard rather than the assumption. Asserting it on every written
    /// record turns a stale count into a refusal, where the corpus proves the
    /// invariant holds everywhere the game itself wrote.
    /// </remarks>
    public bool CoverageAgreesWith(int vertices) =>
        CoverageSignWords == (uint)CoverageSignWordsFor(vertices) &&
        CoverageBitsWords == (uint)CoverageBitsWordsFor(vertices) &&
        CoverageBitsBase == CoverageSignBase + CoverageSignWords;

    /// <summary>
    /// This constant pointed at a new slice of the Bezier buffer, sized for a
    /// vertex count.
    /// </summary>
    /// <remarks>
    /// All four fields move together, as <see cref="WithZSlice"/> moves the base
    /// and the restated depth count together, and for the same reason: they are
    /// one fact stated four times, and a caller able to set one of them alone
    /// would eventually set one of them alone.
    /// </remarks>
    public Mode3Constant WithCoverageSlice(int signBase, int vertices)
    {
        byte[] indices = new byte[DataIndicesLength];
        WriteIndex(indices, 0, (uint)signBase);
        WriteIndex(indices, 1, (uint)CoverageSignWordsFor(vertices));
        WriteIndex(indices, 2, (uint)(signBase + CoverageSignWordsFor(vertices)));
        WriteIndex(indices, 3, (uint)CoverageBitsWordsFor(vertices));
        return this with { DataIndices = indices };
    }

    /// <summary>The sixteen bytes at <c>+48</c>, as four words.</summary>
    internal const int DataIndicesLength = 16;

    private uint DataIndex(int word) =>
        DataIndices.Length >= DataIndicesLength
            ? BinaryPrimitives.ReadUInt32LittleEndian(DataIndices.Span[(word * 4)..])
            : 0u;

    private static void WriteIndex(byte[] indices, int word, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(word * 4), value);

    /// <summary>The widths at which the engine reads a Z index correctly.</summary>
    /// <remarks>
    /// <para>
    /// The field spells thirty-two widths and the engine reads six of them. Its
    /// generated vertex shader extracts an index with
    /// <c>(VertexZIndexBuffer.Load(startBit / 32) &gt;&gt; (startBit % 32)) &amp; mask</c>,
    /// loading <b>one</b> word — so a field straddling a word boundary loses the
    /// bits past it, and the start bit is a plain <c>width * slot</c> with no
    /// padding. A width that does not divide 32 therefore truncates, which for
    /// widths of 32 or less means the width must be a power of two.
    /// </para>
    /// <para>
    /// That is the binding constraint and it subsumes the shader's broken mask
    /// table, which is right only for widths 1 to 4 and the multiples of four.
    /// Every shipped record agrees: 5,241,035 of them across 12,524 files use
    /// widths 1, 2, 4, 8 and 16 and nothing else. Roadmap §10.44 and §10.50.
    /// </para>
    /// </remarks>
    public static bool IsReadableZBitWidth(int width) => width is 1 or 2 or 4 or 8 or 16 or 32;

    /// <summary>
    /// The narrowest readable width that can name <paramref name="depths"/>
    /// entries, or zero if no width can.
    /// </summary>
    /// <remarks>
    /// Rounding up to a readable width costs a few bits per vertex and nothing
    /// else, which is why choosing one needs no judgement and no setting.
    /// </remarks>
    public static int NarrowestZBitWidth(long depths)
    {
        foreach (int width in (int[])[1, 2, 4, 8, 16, 32])
        {
            if (depths <= 1L << width)
            {
                return width;
            }
        }

        return 0;
    }
}

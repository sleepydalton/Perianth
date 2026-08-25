using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Cameldata;

/// <summary>
/// What a cameldata file said, above the point where the two modes diverge.
/// </summary>
/// <remarks>
/// Mode 2 and mode 3 share only the twelve-byte header and the Bezier block.
/// Below that they have different constant strides, different field meanings and
/// different trailing data, so they are different types rather than one type
/// with half its properties null. A caller matches on which it got.
/// </remarks>
public abstract class CameldataFile
{
    private protected CameldataFile(
        string path,
        uint headerWord,
        int mode,
        int flags,
        int bezierWordCount,
        ReadOnlyMemory<byte> bezierBytes,
        ReadOnlyMemory<byte> trailingBytes)
    {
        Path = path;
        HeaderWord = headerWord;
        Mode = mode;
        Flags = flags;
        BezierWordCount = bezierWordCount;
        BezierBytes = bezierBytes;
        TrailingBytes = trailingBytes;
    }

    /// <summary>The path as the caller supplied it.</summary>
    public string Path { get; }

    /// <summary>The packed header word, kept whole.</summary>
    public uint HeaderWord { get; }

    /// <summary>The low two bits of the header: 2 or 3.</summary>
    public int Mode { get; }

    /// <summary>Bit 15 of the header, which adds an eight-byte tail to every constant.</summary>
    public int Flags { get; }

    /// <summary>How many Bezier words the header declared.</summary>
    public int BezierWordCount { get; }

    /// <summary>
    /// The Bezier block, skipped and preserved.
    /// </summary>
    /// <remarks>
    /// Curve coverage is closed by measurement rather than left undone, so these
    /// bytes are never interpreted here — but discarding them would make them
    /// unrecoverable for a writer, and section 15 is explicit that the decode is
    /// fully proven and only the representation was declined.
    /// </remarks>
    public ReadOnlyMemory<byte> BezierBytes { get; }

    /// <summary>
    /// Bytes after the last array the grammar accounts for.
    /// </summary>
    /// <remarks>
    /// Section 13 says trailing cameldata bytes are warned about and ignored,
    /// unlike editordata and BVM where they refuse. There is no warning channel
    /// yet, so they are kept here and the report is left to whoever gains one.
    /// Preserving them costs nothing and losing them would be irreversible.
    /// </remarks>
    public ReadOnlyMemory<byte> TrailingBytes { get; }
}

/// <summary>A mode-2 cameldata file: constants and an absolute XYZ position pool.</summary>
public sealed class Mode2Cameldata : CameldataFile
{
    internal Mode2Cameldata(
        string path,
        uint headerWord,
        int flags,
        int bezierWordCount,
        ReadOnlyMemory<byte> bezierBytes,
        ImmutableArray<Mode2Constant> constants,
        ImmutableArray<Vector3> positions,
        ReadOnlyMemory<byte> trailingBytes)
        : base(path, headerWord, 2, flags, bezierWordCount, bezierBytes, trailingBytes)
    {
        Constants = constants;
        Positions = positions;
    }

    /// <summary>The constant records, in file order.</summary>
    public ImmutableArray<Mode2Constant> Constants { get; }

    /// <summary>
    /// The file-level position pool.
    /// </summary>
    /// <remarks>
    /// A model part's stored identifier indexes this absolutely, not relative to
    /// anything the part declares.
    /// </remarks>
    public ImmutableArray<Vector3> Positions { get; }
}

/// <summary>A mode-3 cameldata file: constants and four counted arrays.</summary>
public sealed class Mode3Cameldata : CameldataFile
{
    internal Mode3Cameldata(
        string path,
        uint headerWord,
        int flags,
        int bezierWordCount,
        ReadOnlyMemory<byte> bezierBytes,
        ImmutableArray<Mode3Constant> constants,
        ImmutableArray<Vector2> xy,
        ImmutableArray<float> z,
        ImmutableArray<uint> uv0,
        ImmutableArray<uint> packedZ,
        ReadOnlyMemory<byte> trailingBytes)
        : base(path, headerWord, 3, flags, bezierWordCount, bezierBytes, trailingBytes)
    {
        Constants = constants;
        Xy = xy;
        Z = z;
        Uv0 = uv0;
        PackedZ = packedZ;
    }

    /// <summary>The constant records, in file order.</summary>
    public ImmutableArray<Mode3Constant> Constants { get; }

    /// <summary>The XY array, indexed by a constant's XY base plus a local identifier.</summary>
    public ImmutableArray<Vector2> Xy { get; }

    /// <summary>The Z array, indexed by a constant's Z base plus a packed Z index.</summary>
    public ImmutableArray<float> Z { get; }

    /// <summary>The unified UV0 array, one packed word per vertex.</summary>
    public ImmutableArray<uint> Uv0 { get; }

    /// <summary>The packed Z-index bit stream, as words.</summary>
    public ImmutableArray<uint> PackedZ { get; }

    /// <summary>
    /// The same file with a different packed Z-index stream.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="WithPositions"/> because a reshape must not touch
    /// it: moving vertices changes where they are, not which depth each one
    /// reads. Only replacing a part's arrangement changes that, and the two
    /// operations are kept apart so the narrower one cannot reach the wider one's
    /// field by accident.
    /// </remarks>
    public Result<Mode3Cameldata> WithPackedZ(ImmutableArray<uint> packedZ)
    {
        if (packedZ.Length != PackedZ.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A replacement supplied {packedZ.Length} packed Z words for a stream of {PackedZ.Length}. The stream has one field per pool slot and the pool's size cannot change."));
        }

        return Result.Ok(new Mode3Cameldata(
            Path, HeaderWord, Flags, BezierWordCount, BezierBytes,
            Constants, Xy, Z, Uv0, packedZ, TrailingBytes));
    }

    /// <summary>
    /// The same file with a different unified UV0 array.
    /// </summary>
    /// <remarks>
    /// A third narrow door, beside positions and the packed Z stream, and narrow
    /// for the same reason: it changes what the records that read this array are
    /// painted with, and nothing else. The length must match, so a part wanting
    /// more entries must be re-based first — which keeps a resize and a repaint
    /// from being mistaken for one another.
    /// </remarks>
    public Result<Mode3Cameldata> WithUv0(ImmutableArray<uint> uv0)
    {
        if (uv0.Length != Uv0.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A replacement supplied {uv0.Length} UV0 words for an array of {Uv0.Length}."));
        }

        return Result.Ok(new Mode3Cameldata(
            Path, HeaderWord, Flags, BezierWordCount, BezierBytes,
            Constants, Xy, Z, uv0, PackedZ, TrailingBytes));
    }

    /// <summary>
    /// The same file with every record's depth index read at a wider scale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a part needing more depth planes than its index can name has to do
    /// first. The width is a property of the <em>model</em> rather than of a
    /// record — every constant carries it and the packed stream is addressed at
    /// it — so widening one record widens all of them, and the whole stream is
    /// re-cut at the new scale. That is the entire operation: the values are
    /// carried across unchanged, no base moves, and no pool changes length.
    /// </para>
    /// <para>
    /// Only widening. Narrowing would silently truncate any index that no longer
    /// fits, and there is nothing to gain by it; refusing removes the check
    /// rather than adding one, since every value fits a wider field by
    /// construction.
    /// </para>
    /// <para>
    /// <b>The width must be one the engine can read</b> — see
    /// <see cref="Mode3Constant.IsReadableZBitWidth"/>, which is a power of two
    /// because the shader loads a single word per index. This is the one place
    /// in the project that chooses a width rather than reading one off a record,
    /// so it is the one place that constraint can be broken.
    /// </para>
    /// </remarks>
    public Result<Mode3Cameldata> Widened(int width)
    {
        if (!Mode3Constant.IsReadableZBitWidth(width))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A depth index {width} bit(s) wide is not one the engine reads: it loads one 32-bit word per index and adds no padding, so a width that does not divide 32 loses the bits past a word boundary. The readable widths are 1, 2, 4, 8, 16 and 32."));
        }

        if (Constants.IsDefaultOrEmpty)
        {
            return Result.Ok(this);
        }

        int current = Constants[0].ZBitWidth;
        foreach (Mode3Constant constant in Constants)
        {
            if (constant.ZBitWidth != current)
            {
                return Refusal.Unsupported(
                    "This model's records do not share one Z-index width, so its packed stream is addressed on two scales at once and cannot be re-cut.");
            }
        }

        if (width < current)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This model's depth index is {current} bit(s) wide and narrowing it to {width} would truncate any index that no longer fits. Only widening is supported."));
        }

        if (width == current)
        {
            return Result.Ok(this);
        }

        PackedBitWriter packed = new(Xy.Length, width);
        for (int slot = 0; slot < Xy.Length; slot++)
        {
            if (!TryReadPacked(PackedZ, (long)slot * current, current, out uint index))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The packed depth stream holds {PackedZ.Length} word(s), which is short of the {Xy.Length} index(es) the pool needs."));
            }

            packed.Write((long)slot * width, index);
        }

        // Bits 3 to 7 and nothing else. The flags word is written whole by the
        // writer because bits 8 upward have no name here, and rebuilding it from
        // the three fields that do would drop them.
        uint field = ((uint)(width - 1) & 0x1F) << 3;
        ImmutableArray<Mode3Constant>.Builder constants =
            ImmutableArray.CreateBuilder<Mode3Constant>(Constants.Length);
        foreach (Mode3Constant constant in Constants)
        {
            constants.Add(constant with { PackedFlags = (constant.PackedFlags & ~(0x1Fu << 3)) | field });
        }

        return Result.Ok(new Mode3Cameldata(
            Path, HeaderWord, Flags, BezierWordCount, BezierBytes,
            constants.MoveToImmutable(), Xy, Z, Uv0, packed.Words(), TrailingBytes));
    }

    /// <summary>
    /// The same file with each record's slice of the pools resized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation new geometry needs, and the only one that may move a base
    /// index. <paramref name="currentWidths"/> comes from the model's records —
    /// a record's slice is as wide as its highest local identifier plus one, not
    /// as long as its identifier list, because a direct record names the same
    /// slot several times over.
    /// </para>
    /// <para>
    /// It is a shift, and that is measured rather than assumed. Over 1,594
    /// model pairs the XY slices are ascending, gapless, non-overlapping and
    /// exactly consume their pool; the unified UV0 slices do the same wherever
    /// any record uses them; and <b>no model mixes Z-index widths</b>. That last
    /// one is what makes the packed stream a uniform bit splice rather than two
    /// scales interleaved, and it is checked here rather than trusted.
    /// </para>
    /// <para>
    /// The Z pool is resized separately, through <paramref name="newDepths"/>,
    /// because its entries are per depth rather than per vertex: a record
    /// gaining vertices reads the same depths it already had. Leave it empty to
    /// keep every record's depths as they are.
    /// </para>
    /// <para>
    /// It tiles exactly as the XY pool does — strictly ascending, sharing
    /// nothing, gapless and exhaustive over 1,594 models — so growing one
    /// record's depths is the same shift. **There is no slack anywhere**: not
    /// one record in 432,489 has a spare entry, so a part gaining a depth always
    /// re-bases and there is no cheaper path to look for.
    /// </para>
    /// <para>
    /// A record cannot exceed the entries its index width can address, and that
    /// refuses here rather than truncating. <see cref="Widened"/> is what lifts
    /// it, and is deliberately a separate call: it re-cuts the packed stream for
    /// every record in the model, so it is not something a re-base should do
    /// quietly on one record's behalf.
    /// </para>
    /// <para>
    /// New slots are left zero. Filling them is the caller's, through
    /// <see cref="WithPositions"/>, which is possible only after this has made
    /// the lengths agree — so a resize and a reposition cannot be confused for
    /// one another.
    /// </para>
    /// <para>
    /// <paramref name="nowCarriesUv0"/> is how a record that computed its
    /// texture coordinates from position comes to store them instead. It is done
    /// here because a record gaining a slice moves every later record's base,
    /// which is the same shift this already performs for the other three pools —
    /// doing it separately would mean re-basing twice and agreeing both times.
    /// The new slice is left zero, exactly as new position slots are, and
    /// <see cref="WithUv0"/> fills it.
    /// </para>
    /// </remarks>
    /// <param name="currentWidths">Each record's present vertex count.</param>
    /// <param name="newWidths">What each record's count becomes.</param>
    /// <param name="newDepths">What each record's depth count becomes, or empty to keep it.</param>
    /// <param name="carryUv0AtScale">
    /// Per record, the UV scale index a record that is <em>not</em> already
    /// carrying should start carrying at, or <c>-1</c> to leave it as it is. One
    /// array rather than two because the two facts are inseparable: a record
    /// that carries needs a scale, and a scale means nothing without carrying.
    /// Empty leaves every record alone.
    /// </param>
    public Result<Mode3Cameldata> Rebased(
        ImmutableArray<int> currentWidths,
        ImmutableArray<int> newWidths,
        ImmutableArray<int> newDepths = default,
        ImmutableArray<int> carryUv0AtScale = default)
    {
        if (!carryUv0AtScale.IsDefaultOrEmpty && carryUv0AtScale.Length != Constants.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A resize named {carryUv0AtScale.Length} texture-coordinate choices for {Constants.Length} records."));
        }

        if (currentWidths.Length != Constants.Length || newWidths.Length != Constants.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A resize named {currentWidths.Length} current and {newWidths.Length} new widths for {Constants.Length} records."));
        }

        for (int record = 0; record < newWidths.Length; record++)
        {
            if (currentWidths[record] < 0 || newWidths[record] < 0)
            {
                return Refusal.Unsupported("A record cannot have a negative number of vertices.");
            }
        }

        // The layout this rests on, checked on the file in hand.
        int cursor = 0;
        for (int record = 0; record < Constants.Length; record++)
        {
            if (Constants[record].XyBase != (uint)cursor)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Record {record} begins at XY slot {Constants[record].XyBase} where the records before it end at {cursor}, so the pool is not a plain sequence of slices and cannot be re-based."));
            }

            cursor += currentWidths[record];
        }

        if (cursor != Xy.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The records account for {cursor} XY slots and the pool holds {Xy.Length}."));
        }

        int width = Constants[0].ZBitWidth;
        foreach (Mode3Constant constant in Constants)
        {
            if (constant.ZBitWidth != width)
            {
                return Refusal.Unsupported(
                    "This model's records do not share one Z-index width, so its packed stream is addressed on two scales at once and cannot be re-based.");
            }
        }

        bool anyUnified = false;
        int uvCursor = 0;
        for (int record = 0; record < Constants.Length; record++)
        {
            if (!Constants[record].UsesUnifiedUv0)
            {
                continue;
            }

            anyUnified = true;
            if (Constants[record].Uv0Base != (uint)uvCursor)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Record {record} begins at UV0 slot {Constants[record].Uv0Base} where the unified records before it end at {uvCursor}."));
            }

            uvCursor += currentWidths[record];
        }

        if (anyUnified && uvCursor != Uv0.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The unified records account for {uvCursor} UV0 slots and the array holds {Uv0.Length}."));
        }

        // The Z slices, derived from the bases the way the XY ones are checked
        // against theirs. Measured to tile on every real file, and refused here
        // rather than assumed.
        int[] currentDepths = new int[Constants.Length];
        int zCursor = 0;
        for (int record = 0; record < Constants.Length; record++)
        {
            if (Constants[record].ZBase != (uint)zCursor)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Record {record} begins at depth slot {Constants[record].ZBase} where the records before it end at {zCursor}, so the depth pool is not a plain sequence of slices and cannot be re-based."));
            }

            int end = record + 1 < Constants.Length ? (int)Constants[record + 1].ZBase : Z.Length;
            currentDepths[record] = Math.Max(end - (int)Constants[record].ZBase, 0);
            zCursor += currentDepths[record];
        }

        if (zCursor != Z.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The records account for {zCursor} depth slots and the pool holds {Z.Length}."));
        }

        int[] depths = newDepths.IsDefaultOrEmpty ? currentDepths : [.. newDepths];
        if (depths.Length != Constants.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A resize named {depths.Length} depth counts for {Constants.Length} records."));
        }

        for (int record = 0; record < depths.Length; record++)
        {
            if (depths[record] < 0)
            {
                return Refusal.Unsupported("A record cannot have a negative number of depths.");
            }

            long addressable = 1L << Constants[record].ZBitWidth;
            if (depths[record] > addressable)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Record {record} would need {depths[record]} depths and its index is {Constants[record].ZBitWidth} bit(s) wide, which can name {addressable}. Widen the model first, through {nameof(Widened)}, which re-cuts the packed stream for every record at once."));
            }
        }

        int total = 0;
        foreach (int newWidth in newWidths)
        {
            total += newWidth;
        }

        int depthTotal = 0;
        foreach (int count in depths)
        {
            depthTotal += count;
        }

        ImmutableArray<Vector2>.Builder xy = ImmutableArray.CreateBuilder<Vector2>(total);
        // Sized for the whole pool but filled only for the records that read it,
        // so it is finished with ToImmutable rather than MoveToImmutable: the
        // count and the capacity do not agree unless every record is unified.
        // A record being converted makes the pool exist even where none did.
        bool anyCarries = anyUnified;
        if (!carryUv0AtScale.IsDefaultOrEmpty)
        {
            foreach (int scale in carryUv0AtScale)
            {
                anyCarries |= scale >= 0;
            }
        }

        ImmutableArray<uint>.Builder uv0 = ImmutableArray.CreateBuilder<uint>(anyCarries ? total : 0);
        ImmutableArray<Mode3Constant>.Builder constants =
            ImmutableArray.CreateBuilder<Mode3Constant>(Constants.Length);

        ImmutableArray<float>.Builder z = ImmutableArray.CreateBuilder<float>(depthTotal);
        PackedBitWriter packed = new(total, width);
        int oldStart = 0;
        int newUvStart = 0;
        int oldDepthStart = 0;
        int newDepthStart = 0;

        for (int record = 0; record < Constants.Length; record++)
        {
            int keep = Math.Min(currentWidths[record], newWidths[record]);
            int newStart = xy.Count;

            for (int slot = 0; slot < newWidths[record]; slot++)
            {
                xy.Add(slot < keep ? Xy[oldStart + slot] : default);
            }

            for (int slot = 0; slot < keep; slot++)
            {
                if (!TryReadPacked(PackedZ, ((long)oldStart + slot) * width, width, out uint index))
                {
                    return Refusal.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Record {record}'s packed Z index runs past the stream."));
                }

                packed.Write(((long)newStart + slot) * width, index);
            }

            for (int depth = 0; depth < depths[record]; depth++)
            {
                z.Add(depth < currentDepths[record] ? Z[oldDepthStart + depth] : 0f);
            }

            Mode3Constant constant = (Constants[record] with { XyBase = (uint)newStart })
                .WithZSlice(newDepthStart, depths[record]);

            bool wasCarrying = Constants[record].UsesUnifiedUv0;
            int startCarryingAt =
                carryUv0AtScale.IsDefaultOrEmpty || wasCarrying ? -1 : carryUv0AtScale[record];

            if (wasCarrying || startCarryingAt >= 0)
            {
                for (int slot = 0; slot < newWidths[record]; slot++)
                {
                    // A record that was not carrying has no old slice to copy,
                    // so every slot of its new one starts zero.
                    uv0.Add(wasCarrying && slot < keep
                        ? Uv0[(int)Constants[record].Uv0Base + slot]
                        : 0u);
                }

                constant = constant with { Uv0Base = (uint)newUvStart };
                newUvStart += newWidths[record];

                if (startCarryingAt >= 0)
                {
                    // Bits 0 to 2 only. The Z width lives above them and must
                    // survive: a record that lost it would read its depths at
                    // the wrong scale and draw at the wrong distances.
                    constant = constant with
                    {
                        PackedFlags = (constant.PackedFlags & ~7u) | 1u | ((uint)startCarryingAt << 1),
                    };
                }
            }

            constants.Add(constant);
            oldStart += currentWidths[record];
            oldDepthStart += currentDepths[record];
            newDepthStart += depths[record];
        }

        return Result.Ok(new Mode3Cameldata(
            Path, HeaderWord, Flags, BezierWordCount, BezierBytes,
            constants.MoveToImmutable(), xy.MoveToImmutable(), z.MoveToImmutable(),
            anyCarries ? uv0.ToImmutable() : Uv0,
            packed.Words(), TrailingBytes));
    }

    /// <summary>
    /// The corner selector and sign that make <c>QuadraticPS</c> discard nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Selector 2 puts the shader's <c>uv</c> at <c>(0.5, 0.5)</c> on every
    /// vertex, so <c>curveEval = uv.x² - uv.y</c> is a constant <c>-0.25</c>:
    /// strictly inside, never at the boundary, no screen-space derivative
    /// evaluated, and the function returns <c>1.0</c> on its first branch.
    /// </para>
    /// <para>
    /// <b>Chosen by rendering all eight constant encodings, not by argument</b>,
    /// and the difference matters. Selectors 0 and 3 are what a paper reading
    /// reaches for, since their corner <c>curveEval</c> is exactly <c>0</c> —
    /// and 0 washes a part to half alpha while 3 dithers it, because 0 is the
    /// comparison's own boundary. The sign is just as sharp: selector 2 with
    /// sign 0 draws <b>nothing at all</b>. Roadmap §10.155 has the contact
    /// sheet.
    /// </para>
    /// </remarks>
    public const uint NeutralCoverageSelector = 2;

    /// <summary>The sign bit that keeps a fragment rather than dropping it.</summary>
    public const uint NeutralCoverageSign = 1;

    /// <summary>
    /// The same file with every record's curved-coverage slice re-cut for a new
    /// vertex count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fourth pool, and the one that was missed. A record's selectors are
    /// indexed by its <em>draw vertex</em> index, so they are not
    /// <see cref="Rebased"/>'s business — that method works in pool slots, which
    /// is a different number wherever a mesh repeats a position. They are re-cut
    /// here instead, from counts only the model can supply.
    /// </para>
    /// <para>
    /// <paramref name="neutralise"/> is the whole point. A <b>reshape</b> moves
    /// points and keeps every vertex, so each one keeps its selector and the
    /// curve travels with the geometry for free — those records pass through
    /// untouched. A <b>redraw</b> replaces the triangles, and its new vertices
    /// have no curves of their own; keeping the old ones trims the new shape
    /// with the outline of the shape that used to be there, which is what two
    /// probes of batch 3 drew in game (Roadmap §10.154). Such a record is
    /// written neutral throughout.
    /// </para>
    /// <para>
    /// Growth alone also neutralises the slots that are new, because there is
    /// nothing else they could hold.
    /// </para>
    /// </remarks>
    /// <param name="currentVertices">Each record's present draw vertex count.</param>
    /// <param name="newVertices">What each record's count becomes.</param>
    /// <param name="neutralise">Which records lose their curves entirely.</param>
    public Result<Mode3Cameldata> WithCoverage(
        ImmutableArray<int> currentVertices,
        ImmutableArray<int> newVertices,
        ImmutableArray<bool> neutralise)
    {
        if (currentVertices.Length != Constants.Length ||
            newVertices.Length != Constants.Length ||
            neutralise.Length != Constants.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Re-cutting coverage named {currentVertices.Length}, {newVertices.Length} and {neutralise.Length} entries for {Constants.Length} records."));
        }

        // The layout this rests on, checked on the file in hand rather than
        // assumed from the census: slices tile the buffer in record order with
        // no gap and no overlap, and each one's four fields agree with its own
        // vertex count.
        long cursor = 0;
        for (int record = 0; record < Constants.Length; record++)
        {
            Mode3Constant constant = Constants[record];
            if (currentVertices[record] < 0 || newVertices[record] < 0)
            {
                return Refusal.Unsupported("A record cannot have a negative number of vertices.");
            }

            if (constant.CoverageSignBase != cursor)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Record {record}'s coverage begins at word {constant.CoverageSignBase} where the records before it end at {cursor}, so the Bezier buffer is not a plain sequence of slices and cannot be re-cut."));
            }

            if (!constant.CoverageAgreesWith(currentVertices[record]))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Record {record} declares {constant.CoverageSignWords} sign and {constant.CoverageBitsWords} selector words, which do not describe {currentVertices[record]} vertices."));
            }

            cursor += Mode3Constant.CoverageWordsFor(currentVertices[record]);
        }

        if (cursor * sizeof(uint) != BezierBytes.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The records account for {cursor} Bezier words and the buffer holds {BezierBytes.Length / sizeof(uint)}."));
        }

        int total = 0;
        foreach (int vertices in newVertices)
        {
            total += Mode3Constant.CoverageWordsFor(vertices);
        }

        uint[] words = new uint[total];
        ImmutableArray<Mode3Constant>.Builder constants =
            ImmutableArray.CreateBuilder<Mode3Constant>(Constants.Length);
        ReadOnlySpan<byte> old = BezierBytes.Span;
        int at = 0;

        for (int record = 0; record < Constants.Length; record++)
        {
            Mode3Constant constant = Constants[record];
            int count = newVertices[record];
            int signWords = Mode3Constant.CoverageSignWordsFor(count);
            int keep = neutralise[record] ? 0 : Math.Min(currentVertices[record], count);

            for (int vertex = 0; vertex < count; vertex++)
            {
                uint selector = NeutralCoverageSelector;
                uint sign = NeutralCoverageSign;
                if (vertex < keep)
                {
                    selector = (Read(old, constant.CoverageBitsBase + (uint)(vertex / 16))
                        >> ((vertex % 16) * 2)) & 3;
                    sign = (Read(old, constant.CoverageSignBase + (uint)(vertex / 32))
                        >> (vertex % 32)) & 1;
                }

                words[at + signWords + (vertex / 16)] |= selector << ((vertex % 16) * 2);
                words[at + (vertex / 32)] |= sign << (vertex % 32);
            }

            constants.Add(constant.WithCoverageSlice(at, count));
            at += Mode3Constant.CoverageWordsFor(count);
        }

        byte[] bytes = new byte[total * sizeof(uint)];
        for (int word = 0; word < total; word++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(word * sizeof(uint)), words[word]);
        }

        return Result.Ok(new Mode3Cameldata(
            Path, HeaderWord, Flags, total, bytes,
            constants.MoveToImmutable(), Xy, Z, Uv0, PackedZ, TrailingBytes));
    }

    /// <summary>One word of the Bezier buffer, zero past its end.</summary>
    private static uint Read(ReadOnlySpan<byte> blob, uint index) =>
        (index + 1) * sizeof(uint) <= (uint)blob.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(blob[(int)(index * sizeof(uint))..])
            : 0u;

    /// <summary>
    /// The same file with one more record, appended after the last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appending rather than inserting, and that is what makes this small.
    /// A record's slice of a pool ends where the <em>next</em> record's base
    /// begins, or at the pool's end for the last one — so a record added at the
    /// end takes a base equal to the old pool length, which is exactly where the
    /// previously-last record's slice already ended. Every existing base stays
    /// valid and nothing re-bases. <see cref="Rebased"/> is for changing what a
    /// record already owns; this is for giving one to a record that had none.
    /// </para>
    /// <para>
    /// The bases on <paramref name="constant"/> are <b>ignored and overwritten</b>.
    /// Where a new slice goes is this method's to decide, not the caller's, and
    /// a caller that had to compute three pool offsets correctly would be a
    /// caller that could get them wrong.
    /// </para>
    /// <para>
    /// The packed Z stream is rebuilt rather than extended, because it is a bit
    /// splice with no word alignment per record: the new record's first field
    /// begins mid-word wherever the last one left off. Rebuilding reads every
    /// existing index at the model's one width and writes it back at the same
    /// slot, so the result is bit-identical below the new record.
    /// </para>
    /// </remarks>
    public Result<Mode3Cameldata> WithAppendedRecord(
        Mode3Constant constant,
        ImmutableArray<Vector2> positions,
        ImmutableArray<float> depths,
        ImmutableArray<uint> depthIndices,
        ImmutableArray<uint> uv0 = default)
    {
        if (positions.IsDefaultOrEmpty)
        {
            return Refusal.Unsupported(
                "A record with no positions draws nothing, so there is no part to add.");
        }

        if (depths.IsDefaultOrEmpty)
        {
            return Refusal.Unsupported(
                "A record with no depths has no plane to sit on.");
        }

        if (depthIndices.IsDefaultOrEmpty || depthIndices.Length != positions.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A new record named {(depthIndices.IsDefault ? 0 : depthIndices.Length)} depth indices for {positions.Length} positions. There is one index per vertex."));
        }

        int width = constant.ZBitWidth;
        if (!Mode3Constant.IsReadableZBitWidth(width))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A new record asked for a {width}-bit Z index. The shader loads one word per index with no padding, so a width that does not divide 32 loses the bits past the boundary; the readable widths are 1, 2, 4, 8, 16 and 32."));
        }

        // One width per model, for the same reason a re-base needs it: the
        // packed stream is a single bit splice, and two scales in one stream is
        // not a thing this can write or the shader can read.
        foreach (Mode3Constant existing in Constants)
        {
            if (existing.ZBitWidth != width)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"This model indexes depth at {existing.ZBitWidth} bit(s) and the new record asked for {width}. Widen the model first, through {nameof(Widened)}, which re-cuts the packed stream for every record at once."));
            }
        }

        long addressable = 1L << width;
        if (depths.Length > addressable)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A new record named {depths.Length} depths and its index is {width} bit(s) wide, which can name {addressable}."));
        }

        foreach (uint index in depthIndices)
        {
            if (index >= (uint)depths.Length)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A new record selects depth {index} of {depths.Length}."));
            }
        }

        bool unified = constant.UsesUnifiedUv0;
        if (unified)
        {
            if (uv0.IsDefaultOrEmpty || uv0.Length != positions.Length)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A unified-UV0 record named {(uv0.IsDefault ? 0 : uv0.Length)} UV0 values for {positions.Length} positions. There is one per vertex."));
            }
        }
        else if (!uv0.IsDefaultOrEmpty)
        {
            return Refusal.Unsupported(
                "A record that derives UV0 from position was given UV0 values, which nothing would read. Set the unified-UV0 flag, or supply none.");
        }

        Mode3Constant appended = (constant with
        {
            XyBase = (uint)Xy.Length,
            Uv0Base = unified ? (uint)Uv0.Length : constant.Uv0Base,
        }).WithZSlice(Z.Length, depths.Length);

        int total = Xy.Length + positions.Length;
        PackedBitWriter packed = new(total, width);
        for (int slot = 0; slot < Xy.Length; slot++)
        {
            if (!TryReadPacked(PackedZ, (long)slot * width, width, out uint index))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The packed Z stream ends at slot {slot} where the XY pool holds {Xy.Length}."));
            }

            packed.Write((long)slot * width, index);
        }

        for (int slot = 0; slot < positions.Length; slot++)
        {
            packed.Write((long)(Xy.Length + slot) * width, depthIndices[slot]);
        }

        return Result.Ok(new Mode3Cameldata(
            Path, HeaderWord, Flags, BezierWordCount, BezierBytes,
            Constants.Add(appended),
            Xy.AddRange(positions),
            Z.AddRange(depths),
            unified ? Uv0.AddRange(uv0) : Uv0,
            packed.Words(),
            TrailingBytes));
    }

    private static bool TryReadPacked(ImmutableArray<uint> words, long bit, int width, out uint value)
    {
        value = 0;
        for (int i = 0; i < width; i++)
        {
            long at = bit + i;
            int word = (int)(at / 32);
            if (word >= words.Length)
            {
                return false;
            }

            value |= ((words[word] >> (int)(at % 32)) & 1u) << i;
        }

        return true;
    }

    /// <summary>Builds a packed Z-index stream one field at a time.</summary>
    /// <remarks>
    /// The word count is exactly what the fields need, rounded up — measured on
    /// all 1,598 single-width files, with no file carrying slack. So a resize
    /// that changes nothing reproduces the stream it read, which is what the
    /// identity property asserts.
    /// </remarks>
    private sealed class PackedBitWriter
    {
        private readonly uint[] _words;
        private readonly int _width;

        public PackedBitWriter(int slots, int width)
        {
            _width = width;
            _words = new uint[(((long)slots * width) + 31) / 32];
        }

        public void Write(long bit, uint value)
        {
            for (int i = 0; i < _width; i++)
            {
                if (((value >> i) & 1u) != 0)
                {
                    long at = bit + i;
                    _words[at / 32] |= 1u << (int)(at % 32);
                }
            }
        }

        public ImmutableArray<uint> Words() => [.. _words];
    }

    /// <summary>
    /// The same file with different vertex positions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only opening a geometry edit gets, and narrow on purpose. Reshaping
    /// changes where vertices are and nothing else: the constants keep their
    /// bases, so no record's slice of the pool moves, and the packed Z indices
    /// keep saying which depth each vertex reads. Handing out the constructor
    /// would let an edit change those too, and a base index off by one produces a
    /// file that loads and draws another part's geometry.
    /// </para>
    /// <para>
    /// The array lengths must match what they replace, for the same reason: a
    /// pool that grew or shrank would leave every base index after the change
    /// pointing somewhere else.
    /// </para>
    /// </remarks>
    public Result<Mode3Cameldata> WithPositions(ImmutableArray<Vector2> xy, ImmutableArray<float> z) =>
        WithPositions(xy, z, Uv0);

    /// <summary>
    /// The same, carrying an edited UV0 pool as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A record that stores its own texture layout indexes UV0 by the same
    /// identifier as XY — <c>Uv0Base + Gfx_PosId</c> — so a reshape that has an
    /// entry to move has a coordinate to move with it, and the two travel
    /// together or the layout describes the shape the part used to be.
    /// </para>
    /// <para>
    /// The length guard is the same and is there for the same reason: the pool's
    /// size is what every record's base is measured against. A reshape may
    /// rewrite entries and may not add or remove one.
    /// </para>
    /// </remarks>
    public Result<Mode3Cameldata> WithPositions(
        ImmutableArray<Vector2> xy, ImmutableArray<float> z, ImmutableArray<uint> uv0)
    {
        if (xy.Length != Xy.Length || z.Length != Z.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A reshape supplied {xy.Length} XY and {z.Length} Z entries for a pool of {Xy.Length} and {Z.Length}. " +
                $"The pool's size is what every part's base index is measured against, so it cannot change."));
        }

        if (uv0.Length != Uv0.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A reshape supplied {uv0.Length} UV0 entries for a pool of {Uv0.Length}. " +
                $"The pool's size is what every storing record's base index is measured against, so it cannot change."));
        }

        return Result.Ok(new Mode3Cameldata(
            Path, HeaderWord, Flags, BezierWordCount, BezierBytes,
            Constants, xy, z, uv0, PackedZ, TrailingBytes));
    }
}

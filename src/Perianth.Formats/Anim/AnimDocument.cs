using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Anim;

/// <summary>
/// An ANIM container read whole: every chunk it holds, in file order, with
/// nothing left uninterpreted.
/// </summary>
/// <remarks>
/// <para>
/// The stronger of the two readings, and a separate operation on purpose, as
/// BVM's two are. <see cref="AnimReader.Read"/> answers "what does this
/// animation pose", which is all the exporter ever needed and which locates the
/// chunks it wants by a bounded tag search. This answers "what is in the file",
/// which import needs, and it walks front to back deriving every chunk's length
/// from the header counts and the selector streams. The specification's §6
/// describes the search; a search is a reader strategy, not a layout, and a
/// writer cannot use one. The same conflation was corrected for the MMB in
/// Roadmap §10.47 and this is the ANIM's turn.
/// </para>
/// <para>
/// Every length here is derived, and the derivation accounts for every byte of
/// all 68,561 shipped animations. <b>One anchor is not sequentially
/// derivable</b>: a compressed channel's value array runs to a <c>CHAK</c> tag
/// whose distance nothing states, so it is located by a bounded search and the
/// derived lengths are then required to reproduce where the search landed. That
/// is a check on the search rather than trust in it.
/// </para>
/// <para>
/// This is what a writer round-trips, so it keeps what nothing reads as well as
/// what everything does — the <c>TYPE</c> bytes, the static blobs, the change
/// tables, the source path the exporter stamped in, and the tail.
/// </para>
/// </remarks>
/// <param name="Header">
/// The fixed header, verbatim. 0x51 bytes at format version 14 and 0x44 at 12
/// and 13; the version's high half, which the header also carries, decides how
/// much tail the file has.
/// </param>
/// <param name="Types">The <c>TYPE</c> chunk: one byte per node, which nothing in this build reads.</param>
/// <param name="Parents">The <c>PRNT</c> chunk, or empty where the file has none.</param>
/// <param name="Channels">Translation, rotation and scale, in that file order.</param>
/// <param name="Names">The <c>NAME</c> table: one name per node.</param>
/// <param name="SourcePath">The authoring path the exporter stamped in, length-prefixed in the file.</param>
/// <param name="TailArray">
/// The array of <c>u32</c> after the source path, present from version high-half
/// 1. Almost always <c>0xFFFFFFFF</c> throughout, and seven shipped files say
/// otherwise, so it is kept rather than assumed.
/// </param>
/// <param name="NodeBits">
/// A per-node bit array, present from version high-half 3, one bit per node
/// rounded up to a byte. <b>What it selects is unknown</b>, and the name says
/// only what it is: 41,389 shipped animations set no bit at all and 26,644 set
/// some, and nine candidate readings were scored against the second group
/// without one reaching 5%. It is kept and written back, never interpreted.
/// </param>
public sealed record AnimDocument(
    ImmutableArray<byte> Header,
    ImmutableArray<byte> Types,
    ImmutableArray<ushort> Parents,
    ImmutableArray<AnimChannelBlock> Channels,
    ImmutableArray<string> Names,
    string SourcePath,
    ImmutableArray<uint> TailArray,
    ImmutableArray<byte> NodeBits)
{
    /// <summary>The number of nodes the header declares, which every per-node chunk is sized by.</summary>
    public int NodeCount => Names.Length;

    /// <summary>The <c>TYPE</c> byte of a joint.</summary>
    /// <remarks>
    /// <b>A node kind, and this is the joint.</b> The corpus holds two values
    /// over 53,124,504 type bytes: <c>5</c> on all but eight, and <c>3</c> on
    /// those eight — every one of which is a node named <c>UberCamera</c>. So the
    /// field distinguishes a joint from a camera, and a new joint is a <c>5</c>
    /// because that is what it is. This was written down as an arbitrary default
    /// until the eight exceptions were looked at; one contrasting case was enough
    /// to turn a decision into a reading.
    /// </remarks>
    public const byte DefaultType = 5;

    /// <summary>A parent index meaning the node has none.</summary>
    public const ushort NoParent = 0xFFFF;

    /// <summary>A selector meaning the channel says nothing about the node.</summary>
    public const ushort Silent = 0xFFFF;

    /// <summary>
    /// Appends a node, moving every count that says how many there are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is here, next to the name table, on purpose.</b> A node changes
    /// the length of every per-node chunk at once — <c>TYPE</c>, <c>PRNT</c>,
    /// all three selector streams, <c>NAME</c> and the tail's bit array — and
    /// restates its own count in the header, in the tail, and inside the header's
    /// working-buffer size. Four derived fields went stale in one day on the MMB
    /// because a rule lived away from its input (Roadmap §10.65 to §10.67), and
    /// the answer there was to keep the rule beside the thing it derives from.
    /// This is that answer applied before the mistake rather than after it.
    /// </para>
    /// <para>
    /// <b>Appending, and inserting would buy nothing.</b> A node's place in the
    /// table is not its place in the tree — <c>PRNT</c> is the tree, and it names
    /// any existing node — so appending can already express every hierarchy an
    /// insertion could. What insertion would cost is real: the table is
    /// topologically sorted, parent before child, on <b>33,237 of 33,237</b>
    /// hierarchies, which appending satisfies by construction because a parent's
    /// index is always below the new node's. It would also have to renumber every
    /// parent entry at or past the insertion, shift the tail's bit array by one
    /// bit, and reckon with the tail's array of <c>u32</c>, which is unread and
    /// may well hold node indices.
    /// </para>
    /// <para>
    /// Note that a <em>selector</em> does not name a node: it names an entry of
    /// the channel's own value or static array. Only <c>PRNT</c> names nodes by
    /// index, which is why insertion is a bounded problem rather than an
    /// impossible one — it is simply not one worth having.
    /// </para>
    /// <para>
    /// The new node <b>states nothing</b>: all three of its selectors are the
    /// sentinel, so it reads as identity and sits exactly on its parent. That is
    /// not a shortcut but the ordinary case — <b>92.1% of the 46,281,883 nodes in
    /// the corpus's setup hierarchies state no channel at all</b>, and only 0.8%
    /// state all three. It also keeps the header's per-channel counts untouched,
    /// so no value array moves and no change table is rebuilt.
    /// </para>
    /// <para>
    /// Giving a new joint a transform of its own is a separate operation and a
    /// solvable one: append an entry to the channel's static blob, point the
    /// selector at it, and move the header's static count. What it needs that
    /// does not exist yet is an <em>encoder</em> for the packed float3 the blob
    /// holds. It is unbuilt because the reason to want it is unresolved — whether
    /// a setup's transform places a part at all is Roadmap §10.81 to §10.84 — and
    /// because a model's own node matrix is the better-supported place to put an
    /// offset today.
    /// </para>
    /// <para>
    /// <b>The working-buffer size is a partial answer and says so.</b> Header
    /// <c>0x14</c> is the block the loader allocates and bump-allocates the
    /// decoded animation out of; its absolute formula is unsolved, because a
    /// decoded channel's size is its codec's to decide. Every term a node touches
    /// has a coefficient of one and none of them is a codec term, so the
    /// <em>delta</em> follows from the loader's own sequence even though the total
    /// does not. That delta is derived from the code and is <b>not
    /// corpus-confirmed</b> — no shipped animation differs from another by exactly
    /// one node — so it is the first thing to check in the next in-game batch.
    /// </para>
    /// </remarks>
    /// <param name="name">The joint's name, which a part's binding will match.</param>
    /// <param name="parent">Its parent's index, or negative for a root. Ignored where the file states no hierarchy.</param>
    public Result<AnimDocument> WithAppendedNode(string name, int parent)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0)
        {
            return Refusal.Unsupported("A node's name is what a part binds to, so it cannot be empty.");
        }

        foreach (char character in name)
        {
            if (character is < ' ' or > '~')
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"An ANIM node name is stored as a NUL-terminated byte string and this one contains U+{(int)character:X4}."));
            }
        }

        foreach (string existing in Names)
        {
            if (string.Equals(existing, name, StringComparison.Ordinal))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"This animation already declares a node called '{name}'. A part binds to a node by name, so two of them would be ambiguous."));
            }
        }

        if (parent >= NodeCount)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A new node named parent {parent} and this animation has {NodeCount} nodes. A parent is an index into the table, and the new node cannot be its own."));
        }

        if (Header.Length < 0x28)
        {
            return Refusal.Unsupported("An ANIM header must be long enough to declare its own node count.");
        }

        int nodes = NodeCount + 1;
        byte[] header = [.. Header];
        WriteU32(header, 0x24, (uint)nodes);

        // Every piece a node adds to the loader's working block, in the order the
        // loader walks them: its parent entry where there is a hierarchy, one
        // selector in each of three streams, its type byte, and its name.
        long buffer = ReadU32(header, 0x14)
            + (Parents.IsEmpty ? 0 : 2)
            + (3 * 2)
            + 1
            + Encoding.Latin1.GetByteCount(name) + 1;
        if (buffer > uint.MaxValue)
        {
            return Refusal.Unsupported("The ANIM working-buffer size would not fit in its own field.");
        }

        WriteU32(header, 0x14, (uint)buffer);

        ImmutableArray<AnimChannelBlock>.Builder channels =
            ImmutableArray.CreateBuilder<AnimChannelBlock>(Channels.Length);
        foreach (AnimChannelBlock channel in Channels)
        {
            channels.Add(channel with { Selectors = channel.Selectors.Add(Silent) });
        }

        // One bit per node, so the array grows only when the count crosses a byte.
        ImmutableArray<byte> bits = NodeBits;
        if (!bits.IsEmpty && (nodes + 7) / 8 > bits.Length)
        {
            bits = bits.Add(0);
        }

        return Result.Ok(this with
        {
            Header = [.. header],
            Types = Types.Add(DefaultType),
            Parents = Parents.IsEmpty
                ? Parents
                : Parents.Add(parent < 0 ? NoParent : (ushort)parent),
            Channels = channels.MoveToImmutable(),
            Names = Names.Add(name),
            NodeBits = bits,
        });
    }

    private static uint ReadU32(ReadOnlySpan<byte> bytes, int at) =>
        (uint)(bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24));

    private static void WriteU32(Span<byte> bytes, int at, uint value)
    {
        bytes[at] = (byte)value;
        bytes[at + 1] = (byte)(value >> 8);
        bytes[at + 2] = (byte)(value >> 16);
        bytes[at + 3] = (byte)(value >> 24);
    }
}

/// <summary>
/// One channel's four chunks: its static values, its per-node selectors, its
/// animated values, and the change table that indexes them.
/// </summary>
/// <remarks>
/// <para>
/// A selector is one of three things — <c>0xFFFF</c> or <c>0xFFFE</c> for a node
/// the channel says nothing about, a value at or above <c>0x8000</c> naming an
/// entry of <see cref="Statics"/>, and anything below naming a channel of
/// <see cref="Values"/>. Those three readings are what size the chunks, which is
/// why a channel cannot be written without its stream.
/// </para>
/// <para>
/// <see cref="Compressed"/> is a property of the file rather than a choice: a
/// flat channel stores every animated value at every sample and has no change
/// table at all, while a compressed one stores each channel's first value and
/// then only the samples it changes on. Both shapes ship, so both are written.
/// </para>
/// </remarks>
/// <param name="Statics">The <c>DTRA</c>, <c>DROT</c> or <c>DSCA</c> payload: one entry per static selector.</param>
/// <param name="Selectors">The <c>TRAI</c>, <c>ROTI</c> or <c>SCAI</c> stream: one per node.</param>
/// <param name="Values">The <c>TRAD</c>, <c>ROTD</c> or <c>SCAD</c> payload, verbatim.</param>
/// <param name="Compressed">Whether a <c>CHAK</c>/<c>CAKS</c> change table follows the values.</param>
/// <param name="Changes">The <c>CHAK</c> chunk: the sample each stored value takes effect on.</param>
/// <param name="Offsets">The <c>CAKS</c> chunk: where each animated channel's changes begin, one longer than the channel count.</param>
public sealed record AnimChannelBlock(
    ImmutableArray<byte> Statics,
    ImmutableArray<ushort> Selectors,
    ImmutableArray<byte> Values,
    bool Compressed,
    ImmutableArray<ushort> Changes,
    ImmutableArray<ushort> Offsets);

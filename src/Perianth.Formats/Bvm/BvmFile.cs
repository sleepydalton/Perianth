using System.Collections.Immutable;

namespace Perianth.Formats.Bvm;

/// <summary>
/// A BVM container as far as it is read: the string table, and the extent of the
/// graph that follows it.
/// </summary>
/// <remarks>
/// <para>
/// Two source extensions use this container — <c>.manimsys</c>, an animation
/// system, and <c>.mgraphobject</c>, an actor definition — and they are the same
/// bytes with different contents. The lip-sync database is a third user of the
/// same integer encoding but not of this layout, which is why it has its own
/// reader rather than being made to share this one.
/// </para>
/// <para>
/// <see cref="Graph"/> is deliberately a range and not a decoded tree. The graph
/// is a tagged value tree whose container header is not yet read; keeping its
/// extent rather than its contents says exactly that, and lets the range be
/// verified — a file whose pool ends anywhere but at a container tag is
/// malformed, which is the check that proved this encoding correct on 800
/// systems.
/// </para>
/// </remarks>
/// <param name="Strings">The string table, in file order. Entries may be empty.</param>
/// <param name="Graph">The unread remainder, beginning at the first container tag.</param>
public sealed record BvmFile(ImmutableArray<string> Strings, ByteRange Graph);

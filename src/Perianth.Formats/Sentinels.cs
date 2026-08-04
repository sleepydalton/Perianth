using System;

namespace Perianth.Formats;

/// <summary>
/// The sentinel values shared across more than one grammar, from porting
/// specification section 4. They live in one place so that a reader of the ANIM
/// or SDF code meets a name rather than a literal, and so that the two meanings
/// of <c>0xFFFF</c> stay two names.
/// </summary>
/// <remarks>
/// This is deliberately only section 4's table. Constants belonging to a single
/// grammar — the SDF version byte, the BVM and DDS signatures, the bake's pixel
/// cap, the smallest-three rotation constants — stay with that grammar. A file
/// that accepts every constant in the project becomes the thing this one exists
/// to prevent.
/// </remarks>
public static class Sentinels
{
    /// <summary>An ANIM node with no parent.</summary>
    public const ushort AnimNoParent = 0xFFFF;

    /// <summary>
    /// An ANIM selector meaning hidden for SCAI, and identity or absent for
    /// transform evaluation.
    /// </summary>
    public const ushort AnimSelectorHiddenOrIdentity = 0xFFFE;

    /// <summary>
    /// An ANIM selector meaning locally active for SCAI, and identity or absent
    /// for transform evaluation. Shares a value with <see cref="AnimNoParent"/>
    /// and nothing else: they are sentinels in different streams, and merging
    /// the names would lose that.
    /// </summary>
    public const ushort AnimSelectorActiveOrIdentity = 0xFFFF;

    /// <summary>
    /// An ANIM selector at or above this value, excluding the two sentinels,
    /// indexes the static table at <c>selector - AnimSelectorStaticBase</c>.
    /// Below it, the selector is an animated channel index.
    /// </summary>
    public const ushort AnimSelectorStaticBase = 0x8000;

    /// <summary>An SDF page is 64 KiB.</summary>
    public const int SdfPageSize = 64 * 1024;

    /// <summary>
    /// A paged stored-size of zero means a full <see cref="SdfPageSize"/> page,
    /// not an empty one — the field is a UInt16 and cannot hold 65536.
    /// </summary>
    public const ushort SdfFullPageStoredSize = 0;

    /// <summary>
    /// A BVM compact value carries six payload bits in its first byte; the top
    /// two bits select how many little-endian bytes follow.
    /// </summary>
    public const int BvmCompactPayloadBits = 6;

    /// <summary>
    /// The follower counts a BVM compact value's top two bits select. A signed
    /// compact value sign-extends at width
    /// <c>BvmCompactPayloadBits + 8 * extraCount</c>.
    /// </summary>
    public static ReadOnlySpan<byte> BvmCompactExtraByteCounts => [0, 1, 3, 7];
}

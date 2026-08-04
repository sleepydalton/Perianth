namespace Perianth.Formats.Dds;

/// <summary>
/// The fixed tables BC7 decoding needs: per-mode field widths, the partition
/// shapes, the anchor positions, and the interpolation weights.
/// </summary>
/// <remarks>
/// These are constants of the format, not of this corpus. They are transcribed
/// rather than derived, so the conformance suite over the recorded BC7 inputs
/// is what establishes they are right: a single wrong entry in a partition
/// table shows up as a handful of blocks disagreeing, which is exactly the
/// failure the manifest oracle exists to make visible.
/// </remarks>
internal static class Bc7Tables
{
    /// <summary>Field widths for each of the eight modes.</summary>
    internal static readonly Bc7Mode[] Modes =
    [
        //           NS  PB  RB ISB  CB  AB EPB SPB  IB IB2
        new Bc7Mode(  3,  4,  0,  0,  4,  0,  1,  0,  3,  0),
        new Bc7Mode(  2,  6,  0,  0,  6,  0,  0,  1,  3,  0),
        new Bc7Mode(  3,  6,  0,  0,  5,  0,  0,  0,  2,  0),
        new Bc7Mode(  2,  6,  0,  0,  7,  0,  1,  0,  2,  0),
        new Bc7Mode(  1,  0,  2,  1,  5,  6,  0,  0,  2,  3),
        new Bc7Mode(  1,  0,  2,  0,  7,  8,  0,  0,  2,  2),
        new Bc7Mode(  1,  0,  0,  0,  7,  7,  1,  0,  4,  0),
        new Bc7Mode(  2,  6,  0,  0,  5,  5,  1,  0,  2,  0),
    ];

    /// <summary>Interpolation weights for two-, three- and four-bit indices.</summary>
    internal static readonly byte[] Weights2 = [0, 21, 43, 64];

    /// <summary>Three-bit interpolation weights.</summary>
    internal static readonly byte[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];

    /// <summary>Four-bit interpolation weights.</summary>
    /// <remarks>
    /// Entry 13 is 55. It was first transcribed as 56, and because mode 6 is
    /// the only mode with four-bit indices, that single digit failed 1,569 of
    /// the 2,747 recorded BC7 files while leaving every other mode correct.
    /// </remarks>
    internal static readonly byte[] Weights4 =
        [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Which subset each texel belongs to, for the 64 two-subset partitions.</summary>
    internal static readonly byte[] Partitions2 =
    [
        0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,
        0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,
        0,1,1,1,0,1,1,1,0,1,1,1,0,1,1,1,
        0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1,
        0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1,
        0,0,1,1,0,1,1,1,0,1,1,1,1,1,1,1,
        0,0,0,1,0,0,1,1,0,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,1,0,0,1,1,0,1,1,1,
        0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1,
        0,0,1,1,0,1,1,1,1,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,1,0,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,0,0,0,0,1,0,1,1,1,
        0,0,0,1,0,1,1,1,1,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,0,1,1,1,1,1,1,1,1,
        0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1,
        0,0,0,0,1,0,0,0,1,1,1,0,1,1,1,1,
        0,1,1,1,0,0,0,1,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,1,0,0,0,1,1,1,0,
        0,1,1,1,0,0,1,1,0,0,0,1,0,0,0,0,
        0,0,1,1,0,0,0,1,0,0,0,0,0,0,0,0,
        0,0,0,0,1,0,0,0,1,1,0,0,1,1,1,0,
        0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0,
        0,1,1,1,0,0,1,1,0,0,1,1,0,0,0,1,
        0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,0,
        0,0,0,0,1,0,0,0,1,0,0,0,1,1,0,0,
        0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,0,
        0,0,1,1,0,1,1,0,0,1,1,0,1,1,0,0,
        0,0,0,1,0,1,1,1,1,1,1,0,1,0,0,0,
        0,0,0,0,1,1,1,1,1,1,1,1,0,0,0,0,
        0,1,1,1,0,0,0,1,1,0,0,0,1,1,1,0,
        0,0,1,1,1,0,0,1,1,0,0,1,1,1,0,0,
        0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,
        0,0,0,0,1,1,1,1,0,0,0,0,1,1,1,1,
        0,1,0,1,1,0,1,0,0,1,0,1,1,0,1,0,
        0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0,
        0,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,
        0,1,0,1,0,1,0,1,1,0,1,0,1,0,1,0,
        0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1,
        0,1,0,1,1,0,1,0,1,0,1,0,0,1,0,1,
        0,1,1,1,0,0,1,1,1,1,0,0,1,1,1,0,
        0,0,0,1,0,0,1,1,1,1,0,0,1,0,0,0,
        0,0,1,1,0,0,1,0,0,1,0,0,1,1,0,0,
        0,0,1,1,1,0,1,1,1,1,0,1,1,1,0,0,
        0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,
        0,0,1,1,1,1,0,0,1,1,0,0,0,0,1,1,
        0,1,1,0,0,1,1,0,1,0,0,1,1,0,0,1,
        0,0,0,0,0,1,1,0,0,1,1,0,0,0,0,0,
        0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,0,
        0,0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,
        0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,
        0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0,
        0,1,1,0,1,1,0,0,1,0,0,1,0,0,1,1,
        0,0,1,1,0,1,1,0,1,1,0,0,1,0,0,1,
        0,1,1,0,0,0,1,1,1,0,0,1,1,1,0,0,
        0,0,1,1,1,0,0,1,1,1,0,0,0,1,1,0,
        0,1,1,0,1,1,0,0,1,1,0,0,1,0,0,1,
        0,1,1,0,0,0,1,1,0,0,1,1,1,0,0,1,
        0,1,1,1,1,1,1,0,1,0,0,0,0,0,0,1,
        0,0,0,1,1,0,0,0,1,1,1,0,0,1,1,1,
        0,0,0,0,1,1,1,1,0,0,1,1,0,0,1,1,
        0,0,1,1,0,0,1,1,1,1,1,1,0,0,0,0,
        0,0,1,0,0,0,1,0,1,1,1,0,1,1,1,0,
        0,1,0,0,0,1,0,0,0,1,1,1,0,1,1,1,
    ];

    /// <summary>Which subset each texel belongs to, for the 64 three-subset partitions.</summary>
    internal static readonly byte[] Partitions3 =
    [
        0,0,1,1,0,0,1,1,0,2,2,1,2,2,2,2,
        0,0,0,1,0,0,1,1,2,2,1,1,2,2,2,1,
        0,0,0,0,2,0,0,1,2,2,1,1,2,2,1,1,
        0,2,2,2,0,0,2,2,0,0,1,1,0,1,1,1,
        0,0,0,0,0,0,0,0,1,1,2,2,1,1,2,2,
        0,0,1,1,0,0,1,1,0,0,2,2,0,0,2,2,
        0,0,2,2,0,0,2,2,1,1,1,1,1,1,1,1,
        0,0,1,1,0,0,1,1,2,2,1,1,2,2,1,1,
        0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,
        0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2,
        0,0,0,0,1,1,1,1,2,2,2,2,2,2,2,2,
        0,0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,
        0,1,1,2,0,1,1,2,0,1,1,2,0,1,1,2,
        0,1,2,2,0,1,2,2,0,1,2,2,0,1,2,2,
        0,0,1,1,0,1,1,2,1,1,2,2,1,2,2,2,
        0,0,1,1,2,0,0,1,2,2,0,0,2,2,2,0,
        0,0,0,1,0,0,1,1,0,1,1,2,1,1,2,2,
        0,1,1,1,0,0,1,1,2,0,0,1,2,2,0,0,
        0,0,0,0,1,1,2,2,1,1,2,2,1,1,2,2,
        0,0,2,2,0,0,2,2,0,0,2,2,1,1,1,1,
        0,1,1,1,0,1,1,1,0,2,2,2,0,2,2,2,
        0,0,0,1,0,0,0,1,2,2,2,1,2,2,2,1,
        0,0,0,0,0,0,1,1,0,1,2,2,0,1,2,2,
        0,0,0,0,1,1,0,0,2,2,1,0,2,2,1,0,
        0,1,2,2,0,1,2,2,0,0,1,1,0,0,0,0,
        0,0,1,2,0,0,1,2,1,1,2,2,2,2,2,2,
        0,1,1,0,1,2,2,1,1,2,2,1,0,1,1,0,
        0,0,0,0,0,1,1,0,1,2,2,1,1,2,2,1,
        0,0,2,2,1,1,0,2,1,1,0,2,0,0,2,2,
        0,1,1,0,0,1,1,0,2,0,0,2,2,2,2,2,
        0,0,1,1,0,1,2,2,0,1,2,2,0,0,1,1,
        0,0,0,0,2,0,0,0,2,2,1,1,2,2,2,1,
        0,0,0,0,0,0,0,2,1,1,2,2,1,2,2,2,
        0,2,2,2,0,0,2,2,0,0,1,2,0,0,1,1,
        0,0,1,1,0,0,1,2,0,0,2,2,0,2,2,2,
        0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,0,
        0,0,0,0,1,1,1,1,2,2,2,2,0,0,0,0,
        0,1,2,0,1,2,0,1,2,0,1,2,0,1,2,0,
        0,1,2,0,2,0,1,2,1,2,0,1,0,1,2,0,
        0,0,1,1,2,2,0,0,1,1,2,2,0,0,1,1,
        0,0,1,1,1,1,2,2,2,2,0,0,0,0,1,1,
        0,1,0,1,0,1,0,1,2,2,2,2,2,2,2,2,
        0,0,0,0,0,0,0,0,2,1,2,1,2,1,2,1,
        0,0,2,2,1,1,2,2,0,0,2,2,1,1,2,2,
        0,0,2,2,0,0,1,1,0,0,2,2,0,0,1,1,
        0,2,2,0,1,2,2,1,0,2,2,0,1,2,2,1,
        0,1,0,1,2,2,2,2,2,2,2,2,0,1,0,1,
        0,0,0,0,2,1,2,1,2,1,2,1,2,1,2,1,
        0,1,0,1,0,1,0,1,0,1,0,1,2,2,2,2,
        0,2,2,2,0,1,1,1,0,2,2,2,0,1,1,1,
        0,0,0,2,1,1,1,2,0,0,0,2,1,1,1,2,
        0,0,0,0,2,1,1,2,2,1,1,2,2,1,1,2,
        0,2,2,2,0,1,1,1,0,1,1,1,0,2,2,2,
        0,0,0,2,1,1,1,2,1,1,1,2,0,0,0,2,
        0,1,1,0,0,1,1,0,0,1,1,0,2,2,2,2,
        0,0,0,0,0,0,0,0,2,1,1,2,2,1,1,2,
        0,1,1,0,0,1,1,0,2,2,2,2,2,2,2,2,
        0,0,2,2,0,0,1,1,0,0,1,1,0,0,2,2,
        0,0,2,2,1,1,2,2,1,1,2,2,0,0,2,2,
        0,0,0,0,0,0,0,0,0,0,0,0,2,1,1,2,
        0,0,0,2,0,0,0,1,0,0,0,2,0,0,0,1,
        0,2,2,2,1,2,2,2,0,2,2,2,1,2,2,2,
        0,1,0,1,2,2,2,2,2,2,2,2,2,2,2,2,
        0,1,1,1,2,0,1,1,2,2,0,1,2,2,2,0,
    ];

    /// <summary>Texel holding subset 1's anchor, for each two-subset partition.</summary>
    internal static readonly byte[] Anchors2Subset1 =
    [
        15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,15,
        15, 2, 8, 2, 2, 8, 8,15, 2, 8, 2, 2, 8, 8, 2, 2,
        15,15, 6, 8, 2, 8,15,15, 2, 8, 2, 2, 2,15,15, 6,
         6, 2, 6, 8,15,15, 2, 2,15,15,15,15,15, 2, 2,15,
    ];

    /// <summary>Texel holding subset 1's anchor, for each three-subset partition.</summary>
    internal static readonly byte[] Anchors3Subset1 =
    [
         3, 3,15,15, 8, 3,15,15, 8, 8, 6, 6, 6, 5, 3, 3,
         3, 3, 8,15, 3, 3, 6,10, 5, 8, 8, 6, 8, 5,15,15,
         8,15, 3, 5, 6,10, 8,15,15, 3,15, 5,15,15,15,15,
         3,15, 5, 5, 5, 8, 5,10, 5,10, 8,13,15,12, 3, 3,
    ];

    /// <summary>Texel holding subset 2's anchor, for each three-subset partition.</summary>
    internal static readonly byte[] Anchors3Subset2 =
    [
        15, 8, 8, 3,15,15, 3, 8,15,15,15,15,15,15,15, 8,
        15, 8,15, 3,15, 8,15, 8, 3,15, 6,10,15,15,10, 8,
        15, 3,15,10,10, 8, 9,10, 6,15, 8,15, 3, 6, 6, 8,
        15, 3,15,15,15,15,15,15,15,15,15,15, 3,15,15, 8,
    ];
}

/// <summary>
/// One mode's field widths.
/// </summary>
/// <param name="Subsets">Partitions the block is split into: 1, 2 or 3.</param>
/// <param name="PartitionBits">Width of the partition selector.</param>
/// <param name="RotationBits">Width of the channel-rotation selector.</param>
/// <param name="IndexSelectionBits">Width of the bit that swaps the two index sets.</param>
/// <param name="ColourBits">Bits per colour endpoint component, before any P-bit.</param>
/// <param name="AlphaBits">Bits per alpha endpoint, before any P-bit; zero means opaque.</param>
/// <param name="EndpointPBits">One P-bit per endpoint when set.</param>
/// <param name="SharedPBits">One P-bit per subset, shared by both its endpoints, when set.</param>
/// <param name="IndexBits">Width of a primary index.</param>
/// <param name="SecondaryIndexBits">Width of a secondary index, or zero if there is one index set.</param>
internal readonly record struct Bc7Mode(
    int Subsets,
    int PartitionBits,
    int RotationBits,
    int IndexSelectionBits,
    int ColourBits,
    int AlphaBits,
    int EndpointPBits,
    int SharedPBits,
    int IndexBits,
    int SecondaryIndexBits);

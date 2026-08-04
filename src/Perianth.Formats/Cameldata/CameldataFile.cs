using System;
using System.Collections.Immutable;
using System.Numerics;

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
}

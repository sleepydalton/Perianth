using System;
using System.Collections.Immutable;

namespace Perianth.Formats.Bvm;

/// <summary>
/// One value of a BVM graph — the tagged tree that follows a container's string
/// table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value keeps its own tag, and nothing here interprets one.</b> Four
/// pairs of tags carry byte-identical payloads and differ only in the value type
/// the engine constructs from them: <c>0x0d</c> and <c>0x0e</c> are both string
/// references, and <c>0x05</c>, <c>0x0b</c> and <c>0x11</c> are all eight raw
/// bytes. A reader that merged them would be right about the tree and unable to
/// write it back, which is the whole reason this shape exists.
/// </para>
/// <para>
/// So the fixed-width tags keep their bytes rather than becoming floats. It is
/// tempting to decode <c>0x09</c> as three floats, and the prototype did — but
/// a float that is NaN in the file has more than one bit pattern, and a writer
/// that round-tripped it through <see cref="float"/> would silently pick one.
/// Roadmap §10.86 and the same rule <c>EditordataWriter</c> keeps.
/// </para>
/// <para>
/// The grammar is the engine's own <c>DecodeBvmValue</c> and
/// <c>DecodeBvmContainer</c>, not an inference from the bytes.
/// </para>
/// </remarks>
public abstract record BvmValue
{
    /// <summary>The tag byte this value was read from, and will be written as.</summary>
    public abstract byte Tag { get; }
}

/// <summary>A value that is entirely its tag — empty, true or false.</summary>
/// <remarks>
/// Three tags with no payload at all. They are one type rather than three
/// because there is nothing to tell apart beyond the tag, and a type apiece
/// would be the class-per-hypothesis failure in miniature.
/// </remarks>
/// <param name="Tag">
/// <c>0x00</c> empty, <c>0x02</c> true, or <c>0x03</c> false.
/// </param>
public sealed record BvmMarker(byte Tag) : BvmValue
{
    /// <summary>The tag of a value that is present but holds nothing.</summary>
    public const byte Empty = 0x00;

    /// <summary>The tag of a true.</summary>
    public const byte True = 0x02;

    /// <summary>The tag of a false.</summary>
    public const byte False = 0x03;

    /// <inheritdoc/>
    public override byte Tag { get; } = Tag;
}

/// <summary>An array of values and a map of key-value pairs, in one value.</summary>
/// <remarks>
/// <para>
/// The shape that makes the format readable: a count of array entries, a count
/// of map entries, then the array and then the pairs. <b>A key is a full tagged
/// value</b>, not a string index, which is why one container can be keyed by
/// name and another by integer.
/// </para>
/// <para>
/// Both are kept even when empty, because a container declaring zero of each is
/// two bytes that a writer has to put back.
/// </para>
/// </remarks>
/// <param name="Items">The array entries, in file order.</param>
/// <param name="Entries">The map entries, in file order — never sorted.</param>
public sealed record BvmContainer(
    ImmutableArray<BvmValue> Items, ImmutableArray<BvmPair> Entries) : BvmValue
{
    /// <summary>The tag every container carries.</summary>
    public const byte ContainerTag = 0x01;

    /// <inheritdoc/>
    public override byte Tag => ContainerTag;
}

/// <summary>One key and value of a container's map.</summary>
/// <param name="Key">The key, itself a full value.</param>
/// <param name="Value">What it maps to.</param>
public readonly record struct BvmPair(BvmValue Key, BvmValue Value);

/// <summary>A reference into the container's string table.</summary>
/// <param name="Tag"><c>0x0d</c> or <c>0x0e</c> — decoded identically, written apart.</param>
/// <param name="Index">Which entry of the table.</param>
public sealed record BvmString(byte Tag, int Index) : BvmValue
{
    /// <summary>The commoner of the two string tags.</summary>
    public const byte StringA = 0x0d;

    /// <summary>The rarer one, and a different value type in the engine.</summary>
    public const byte StringB = 0x0e;

    /// <inheritdoc/>
    public override byte Tag { get; } = Tag;
}

/// <summary>One to four signed compact integers.</summary>
/// <param name="Tag"><c>0x04</c>, <c>0x0c</c>, <c>0x0a</c> or <c>0x08</c>, for one to four.</param>
/// <param name="Values">The integers, as many as the tag says.</param>
public sealed record BvmNumbers(byte Tag, ImmutableArray<int> Values) : BvmValue
{
    /// <inheritdoc/>
    public override byte Tag { get; } = Tag;

    /// <summary>How many signed integers a tag carries, or zero if it carries none.</summary>
    public static int CountFor(byte tag) => tag switch
    {
        0x04 => 1,
        0x0c => 2,
        0x0a => 3,
        0x08 => 4,
        _ => 0,
    };
}

/// <summary>A fixed-width run of bytes, kept undecoded.</summary>
/// <remarks>
/// Seven tags land here and three of them are the same width. What they mean —
/// a float, a pair of floats, a matrix row — is the engine's business; what
/// matters to a writer is that the same bytes go back under the same tag.
/// </remarks>
/// <param name="Tag">The tag these bytes belong to.</param>
/// <param name="Bytes">Exactly as many bytes as <see cref="WidthFor"/> says.</param>
public sealed record BvmRaw(byte Tag, ReadOnlyMemory<byte> Bytes) : BvmValue
{
    /// <inheritdoc/>
    public override byte Tag { get; } = Tag;

    /// <summary>How many raw bytes a tag carries, or zero if it carries none.</summary>
    public static int WidthFor(byte tag) => tag switch
    {
        0x05 => 8,
        0x06 => 4,
        0x07 => 16,
        0x09 => 12,
        0x0b => 8,
        0x0f => 16,
        0x10 => 16,
        0x11 => 8,
        _ => 0,
    };
}

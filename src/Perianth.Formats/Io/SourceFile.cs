using System;
using Perianth.Formats.Binary;

namespace Perianth.Formats.Io;

/// <summary>
/// A whole input file, held as a snapshot that was verified not to change while
/// it was read.
/// </summary>
/// <remarks>
/// <para>
/// This carries the caller's own spelling of the path, not a resolved or
/// normalized one. Specification section 16 asks every decoded object to be able
/// to explain where it came from, and the spelling is part of that; a future
/// writer needs it, and nothing downstream should have to guess it back.
/// </para>
/// <para>
/// It is a class rather than a record on purpose. Value equality over a
/// multi-megabyte buffer would be a comparison nobody wants and everybody would
/// get by accident.
/// </para>
/// </remarks>
public sealed class SourceFile
{
    private readonly ReadOnlyMemory<byte> _bytes;

    internal SourceFile(string path, ReadOnlyMemory<byte> bytes)
    {
        Path = path;
        _bytes = bytes;
    }

    /// <summary>
    /// Wraps bytes that are already in hand, naming where they came from.
    /// </summary>
    /// <remarks>
    /// The ordinary way in is <see cref="SourceFileReader"/>, which reads a file
    /// and proves it did not change while being read. That guarantee is about
    /// files on disk; bytes handed over directly — a payload already pulled out
    /// of an archive, say — cannot change underneath the reader at all, so there
    /// is nothing to prove. The name is still required, because every decoded
    /// object has to be able to say where it came from.
    /// </remarks>
    public static SourceFile FromMemory(string name, ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new SourceFile(name, bytes);
    }

    /// <summary>The path as the caller supplied it.</summary>
    public string Path { get; }

    /// <summary>The file's contents.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes.Span;

    /// <summary>
    /// The same contents as memory, so a grammar can hand a record a view of the
    /// bytes it came from without copying them.
    /// </summary>
    internal ReadOnlyMemory<byte> Memory => _bytes;

    /// <summary>The file's size in bytes.</summary>
    public int Length => _bytes.Length;

    /// <summary>A bounded reader positioned at the start of the file.</summary>
    public SpanReader CreateReader() => new(_bytes.Span);
}

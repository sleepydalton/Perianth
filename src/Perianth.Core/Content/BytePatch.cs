using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Content;

/// <summary>What a patch says about itself before it is applied.</summary>
/// <param name="VirtualPath">The archive path the result stands in for.</param>
/// <param name="OriginalSha256">The digest of the file this applies to.</param>
/// <param name="ResultSha256">The digest of what applying it produces.</param>
/// <param name="OriginalLength">The expected length of the original.</param>
/// <param name="ResultLength">The length of the result.</param>
/// <param name="ChangedBytes">How many bytes of replacement the patch carries.</param>
public sealed record PatchHeader(
    string VirtualPath,
    string OriginalSha256,
    string ResultSha256,
    long OriginalLength,
    long ResultLength,
    long ChangedBytes)
{
    /// <summary>
    /// Whether this patch carries a file the game never had, rather than a
    /// difference against one it did.
    /// </summary>
    /// <remarks>
    /// A mod that adds art needs both kinds in one set: patches against the
    /// game's own files, which must not carry those bytes, and the author's new
    /// files, which are theirs to give away whole. Applying this kind needs no
    /// original, and there is nothing to verify it against — the length being
    /// zero is what says so.
    /// </remarks>
    public bool IsNewFile => OriginalLength == 0;
}

/// <summary>
/// A byte-level difference between an original file and an edited one.
/// </summary>
/// <remarks>
/// <para>
/// This is how a modification travels without the game's own bytes travelling
/// with it. A patch holds only the regions that differ, so it can be shared
/// while the original stays where it legitimately is — on the disk of whoever
/// owns the game. Applying it needs their copy, and says so by refusing when
/// the digest does not match.
/// </para>
/// <para>
/// No format knowledge whatsoever: this diffs bytes, so it works on a texture,
/// a model or a file this build has never heard of. That is deliberate. A
/// patcher that understood textures would be a patcher that only worked on
/// them, and the formats it did not know would be the ones somebody needed.
/// </para>
/// <para>
/// Fixed blocks and deflate, rather than a suffix-sorting delta algorithm. The
/// case this serves is an edited image, where the changed region is contiguous
/// and the rest is identical — because the textures this tool writes are
/// uncompressed, so painting one corner changes one corner. A block-compressed
/// format would perturb nearly every block and no algorithm would help much;
/// this one is a few hundred lines instead of a few thousand and loses little.
/// </para>
/// <para>
/// <strong>Size is not the point, and must not become a condition.</strong> A
/// patch for a 394-byte file measured 442 bytes, and it is still exactly as
/// worth making: the reason to build one is that a modder can share their work
/// without ever handing anyone else the game's own bytes. Do not add a
/// threshold below which this suggests shipping the file instead, or a warning
/// that a patch "is not saving anything" — that would trade the whole purpose
/// for an efficiency nobody asked for.
/// </para>
/// </remarks>
public static class BytePatch
{
    private const int BlockSize = 4096;
    private const int DigestBytes = 32;
    private const int MaxPathBytes = 1024;

    /// <summary>What every patch begins with, in every version there will be.</summary>
    private static ReadOnlySpan<byte> Tag => "PERIANTHPATCH\x00"u8;

    /// <summary>
    /// This build's format version, written as two bytes after the tag.
    /// </summary>
    /// <remarks>
    /// Read separately from the tag so a patch from a later build refuses by
    /// saying so, rather than by claiming not to be a patch at all. That
    /// distinction costs nothing now and cannot be added later: once somebody
    /// else holds a patch file, how this build reads its first bytes is fixed.
    /// </remarks>
    private const ushort Version = 1;

    private static int HeaderStart => Tag.Length + 2;

    /// <summary>
    /// Builds a patch turning <paramref name="original"/> into
    /// <paramref name="edited"/>.
    /// </summary>
    /// <param name="original">The file as the game ships it.</param>
    /// <param name="edited">The file as the author wants it.</param>
    /// <param name="virtualPath">The archive path the result replaces.</param>
    public static Result<byte[]> Make(
        ReadOnlySpan<byte> original, ReadOnlySpan<byte> edited, string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(virtualPath);

        byte[] path = Encoding.UTF8.GetBytes(virtualPath);
        if (path.Length == 0 || path.Length > MaxPathBytes)
        {
            return Refusal.Unsupported(
                $"'{virtualPath}' is not a usable archive path for a patch.");
        }

        if (original.SequenceEqual(edited))
        {
            return Refusal.Unsupported(
                "The edited file is identical to the original, so there is nothing to patch.");
        }

        using MemoryStream changes = new();
        Span<byte> head = stackalloc byte[12];

        // Blocks of the *result*, so a file that grew is covered to its end
        // rather than only where the original reached.
        for (int at = 0; at < edited.Length; at += BlockSize)
        {
            int length = Math.Min(BlockSize, edited.Length - at);
            ReadOnlySpan<byte> block = edited.Slice(at, length);

            bool same = at + length <= original.Length
                && original.Slice(at, length).SequenceEqual(block);

            if (same)
            {
                continue;
            }

            BinaryPrimitives.WriteInt64LittleEndian(head, at);
            BinaryPrimitives.WriteInt32LittleEndian(head[8..], length);
            changes.Write(head);
            changes.Write(block);
        }

        byte[] raw = changes.ToArray();
        byte[] deflated = Deflate(raw);

        using MemoryStream output = new();
        output.Write(Tag);
        WriteUInt16(output, Version);
        WriteInt64(output, original.Length);
        output.Write(SHA256.HashData(original));
        WriteInt64(output, edited.Length);
        output.Write(SHA256.HashData(edited));
        WriteInt32(output, path.Length);
        output.Write(path);
        WriteInt64(output, raw.Length);
        WriteInt64(output, deflated.Length);
        output.Write(deflated);

        return Result.Ok(output.ToArray());
    }

    /// <summary>
    /// Builds a patch carrying a file the game never had, as an addition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mod that adds art rather than only changing it needs both kinds in one
    /// set. The reason a patch exists is that the game's bytes must not travel;
    /// an author's own new texture is not the game's bytes, so it travels whole
    /// and legitimately. The result is the file plus a little overhead, which is
    /// the honest price and not a defect — see the note on size above.
    /// </para>
    /// <para>
    /// Expressed as a difference against nothing, so one format carries both and
    /// applying does not branch: the original length is zero, which is what
    /// <see cref="PatchHeader.IsNewFile"/> reads.
    /// </para>
    /// </remarks>
    /// <param name="contents">The whole file.</param>
    /// <param name="virtualPath">The archive path it stands at.</param>
    public static Result<byte[]> MakeAddition(ReadOnlySpan<byte> contents, string virtualPath) =>
        contents.IsEmpty
            ? Refusal.Unsupported("An empty file is not something to add to a mod.")
            : Make(ReadOnlySpan<byte>.Empty, contents, virtualPath);

    /// <summary>
    /// Reads what a patch claims, without applying it.
    /// </summary>
    /// <remarks>
    /// So a caller can say what a patch is for, and which file it wants, before
    /// asking anyone to find that file.
    /// </remarks>
    public static Result<PatchHeader> Describe(ReadOnlySpan<byte> patch)
    {
        Result<Parsed> parsed = Parse(patch);
        return parsed.TryGetValue(out Parsed plan, out Refusal? refusal)
            ? Result.Ok(new PatchHeader(
                plan.VirtualPath,
                Convert.ToHexStringLower(plan.OriginalSha),
                Convert.ToHexStringLower(plan.ResultSha),
                plan.OriginalLength,
                plan.ResultLength,
                plan.RawLength))
            : refusal;
    }

    /// <summary>
    /// Applies <paramref name="patch"/> to <paramref name="original"/>.
    /// </summary>
    /// <remarks>
    /// The original is verified before anything is built and the result is
    /// verified after. A patch that applied to the wrong file would produce a
    /// plausible, broken asset — the exact failure this project refuses
    /// everywhere else — and the digests make that impossible rather than
    /// unlikely.
    /// </remarks>
    public static Result<byte[]> Apply(ReadOnlySpan<byte> patch, ReadOnlySpan<byte> original)
    {
        Result<Parsed> read = Parse(patch);
        if (!read.TryGetValue(out Parsed plan, out Refusal? refusal))
        {
            return refusal;
        }

        if (original.Length != plan.OriginalLength)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This patch is for a {plan.OriginalLength}-byte file and the one given is {original.Length} bytes."));
        }

        if (!SHA256.HashData(original).AsSpan().SequenceEqual(plan.OriginalSha))
        {
            return Refusal.Unsupported(
                "This patch is for a different file: the one given does not have the contents the patch "
                + "was built against. Check it is the unmodified original from your own copy of the game.");
        }

        if (plan.ResultLength > int.MaxValue)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"The result would be {plan.ResultLength} bytes, which does not fit in one buffer."));
        }

        byte[] result = new byte[plan.ResultLength];
        original[..(int)Math.Min(original.Length, plan.ResultLength)].CopyTo(result);

        byte[] changes;
        try
        {
            changes = Inflate(plan.Deflated, plan.RawLength);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // A damaged patch is an expected thing to be handed, not a fault:
            // a truncated stream and an invalid one are the same answer, and
            // both are a refusal rather than an exception out of the library.
            return Refusal.Malformed("The patch's compressed section is not readable.");
        }

        int at = 0;
        while (at < changes.Length)
        {
            if (at + 12 > changes.Length)
            {
                return Refusal.Malformed("The patch ends inside a change record.");
            }

            long offset = BinaryPrimitives.ReadInt64LittleEndian(changes.AsSpan(at));
            int length = BinaryPrimitives.ReadInt32LittleEndian(changes.AsSpan(at + 8));
            at += 12;

            if (offset < 0 || length < 0 || at + length > changes.Length ||
                offset + length > plan.ResultLength)
            {
                return Refusal.Malformed("The patch describes a change outside the file it produces.");
            }

            changes.AsSpan(at, length).CopyTo(result.AsSpan((int)offset));
            at += length;
        }

        if (!SHA256.HashData(result).AsSpan().SequenceEqual(plan.ResultSha))
        {
            return Refusal.Malformed(
                "Applying the patch did not produce the file it says it produces, so it is damaged.");
        }

        return Result.Ok(result);
    }

    private static Result<Parsed> Parse(ReadOnlySpan<byte> patch)
    {
        int at = HeaderStart;

        if (patch.Length < at || !patch[..Tag.Length].SequenceEqual(Tag))
        {
            return Refusal.Malformed("This is not a Perianth patch.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(patch[Tag.Length..]);
        if (version != Version)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This patch is version {version} and this build reads version {Version}. Use the version of Perianth that made it."));
        }

        // 8 + 32 + 8 + 32 + 4, then the path, then 8 + 8.
        if (patch.Length < at + 84)
        {
            return Refusal.Malformed("The patch header is truncated.");
        }

        long originalLength = BinaryPrimitives.ReadInt64LittleEndian(patch[at..]);
        at += 8;
        byte[] originalSha = patch.Slice(at, DigestBytes).ToArray();
        at += DigestBytes;
        long resultLength = BinaryPrimitives.ReadInt64LittleEndian(patch[at..]);
        at += 8;
        byte[] resultSha = patch.Slice(at, DigestBytes).ToArray();
        at += DigestBytes;
        int pathLength = BinaryPrimitives.ReadInt32LittleEndian(patch[at..]);
        at += 4;

        if (pathLength <= 0 || pathLength > MaxPathBytes || patch.Length < at + pathLength + 16)
        {
            return Refusal.Malformed("The patch header is truncated.");
        }

        string virtualPath = Encoding.UTF8.GetString(patch.Slice(at, pathLength));
        at += pathLength;

        long rawLength = BinaryPrimitives.ReadInt64LittleEndian(patch[at..]);
        at += 8;
        long deflatedLength = BinaryPrimitives.ReadInt64LittleEndian(patch[at..]);
        at += 8;

        if (originalLength < 0 || resultLength < 0 || rawLength < 0 || deflatedLength < 0 ||
            rawLength > int.MaxValue || patch.Length - at != deflatedLength)
        {
            return Refusal.Malformed("The patch's declared lengths do not match its contents.");
        }

        return Result.Ok(new Parsed(
            virtualPath,
            originalSha,
            resultSha,
            originalLength,
            resultLength,
            (int)rawLength,
            patch[at..].ToArray()));
    }

    private static byte[] Deflate(byte[] raw)
    {
        using MemoryStream output = new();
        using (DeflateStream stream = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            stream.Write(raw);
        }

        return output.ToArray();
    }

    private static byte[] Inflate(byte[] deflated, int rawLength)
    {
        byte[] raw = new byte[rawLength];
        using MemoryStream input = new(deflated);
        using DeflateStream stream = new(input, CompressionMode.Decompress);

        stream.ReadExactly(raw, 0, rawLength);
        return raw;
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private readonly record struct Parsed(
        string VirtualPath,
        byte[] OriginalSha,
        byte[] ResultSha,
        long OriginalLength,
        long ResultLength,
        int RawLength,
        byte[] Deflated);
}

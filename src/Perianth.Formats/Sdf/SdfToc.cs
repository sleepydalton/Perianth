using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Sdf;

/// <summary>
/// One decoded table of contents.
/// </summary>
/// <remarks>
/// The resident table is kept as raw bytes and read one record at a time, so
/// a lookup never materializes records it does not need.
/// </remarks>
public sealed class SdfToc
{
    private const int ResidentStride = 0x98;
    private const int ResidentCapacity = ResidentStride - 4;

    private readonly ReadOnlyMemory<byte> _residentTable;

    internal SdfToc(
        uint version,
        ReadOnlyMemory<byte> fileTable,
        ReadOnlyMemory<byte> residentTable,
        int residentCount,
        int installPartCount,
        uint firstInstallPart,
        SdfIndexLayout layout)
    {
        Version = version;
        FileTable = fileTable;
        _residentTable = residentTable;
        ResidentCount = residentCount;
        InstallPartCount = installPartCount;
        FirstInstallPart = firstInstallPart;
        Layout = layout;
    }

    /// <summary>The container version. Only 0x16 is decoded.</summary>
    public uint Version { get; }

    /// <summary>The inflated compact path index.</summary>
    public ReadOnlyMemory<byte> FileTable { get; }

    /// <summary>Records in the resident-prefix table.</summary>
    public int ResidentCount { get; }

    /// <summary>Install parts the header declares.</summary>
    public int InstallPartCount { get; }

    /// <summary>The first install part's number.</summary>
    public uint FirstInstallPart { get; }

    /// <summary>Container facts the index bytes do not describe.</summary>
    public SdfIndexLayout Layout { get; }

    /// <summary>
    /// Returns the resident bytes a terminal's resident index selects.
    /// </summary>
    /// <remarks>
    /// Every payload in the verified container happens to be a DDS header, but
    /// that is an observation about this content rather than the meaning of
    /// the field. The mechanism is a generic resident prefix and nothing here
    /// inspects what it holds.
    /// </remarks>
    public Result<ReadOnlyMemory<byte>> ResidentPrefix(int index)
    {
        if (index < 0 || index >= ResidentCount)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Resident prefix index {index} is outside the {ResidentCount}-record table."));
        }

        int start = index * ResidentStride;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(_residentTable.Span[start..]);

        if (size > ResidentCapacity)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Resident prefix {index} declares {size} bytes, which exceeds its {ResidentCapacity}-byte record capacity."));
        }

        return Result.Ok(_residentTable.Slice(start + 4, (int)size));
    }
}

/// <summary>
/// Decodes the <c>sdf.sdftoc</c> header and its compressed file table.
/// </summary>
/// <remarks>
/// <para>
/// Every offset is computed from declared counts and measured record strides
/// rather than from a fixed constant. The compressed file table's position is
/// not stored anywhere: it is wherever the fixed header, the identity record,
/// the optional reserved block, the two install-part tables and the
/// resident-prefix table happen to end. Published notes elsewhere hardcode the
/// value that derivation produces for one container; that constant is correct
/// for that container alone and does not survive a different part or prefix
/// count.
/// </para>
/// <para>
/// Versions above 0x16 are reported to insert a further header field. Because
/// every later offset is derived by walking forward, an unexpected field would
/// silently shift everything, so unknown versions refuse rather than being
/// parsed optimistically.
/// </para>
/// </remarks>
public static class SdfTocReader
{
    private const uint SupportedVersion = 0x16;
    private const int IdentityBlobBytes = 0x20;
    private const int ReservedBlockBytes = 0x140;
    private const int ResidentStride = 0x98;

    /// <summary>Decodes one complete table of contents.</summary>
    public static Result<SdfToc> Read(ReadOnlyMemory<byte> source)
    {
        ReadOnlySpan<byte> bytes = source.Span;

        if (bytes.Length < 4 || bytes[0] != (byte)'W' || bytes[1] != (byte)'E' ||
            bytes[2] != (byte)'S' || bytes[3] != (byte)'T')
        {
            // The magic identifies the Snowdrop family, not this game.
            return Refusal.Unsupported("This file is not an SDF table of contents: the WEST magic is missing.");
        }

        int offset = 4;
        if (!TryUInt32(bytes, ref offset, out uint version))
        {
            return Refusal.Malformed("The sdftoc header is truncated.");
        }

        if (version != SupportedVersion)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The sdftoc declares version 0x{version:X}; only 0x{SupportedVersion:X} has been verified."));
        }

        if (!TryUInt32(bytes, ref offset, out uint inflatedSize) ||
            !TryUInt32(bytes, ref offset, out uint compressedSize) ||
            !TryUInt32(bytes, ref offset, out uint firstInstallPart) ||
            !TryUInt32(bytes, ref offset, out uint installPartCount) ||
            !TryUInt32(bytes, ref offset, out uint residentCount))
        {
            return Refusal.Malformed("The sdftoc header is truncated.");
        }

        if (!TrySkipIdentity(bytes, ref offset))
        {
            return Refusal.Malformed("The sdftoc start identity record is truncated or unterminated.");
        }

        if (offset >= bytes.Length)
        {
            return Refusal.Malformed("The sdftoc layout flag is missing.");
        }

        // The flag must actually be read: assuming either case breaks the other.
        byte layoutFlag = bytes[offset++];
        if (layoutFlag != 0 && !TrySkip(bytes, ref offset, ReservedBlockBytes))
        {
            return Refusal.Malformed("The sdftoc reserved block is truncated.");
        }

        if (!TrySkip(bytes, ref offset, (long)installPartCount * 4))
        {
            return Refusal.Malformed("The sdftoc install-part size table is truncated.");
        }

        if (installPartCount > 0)
        {
            // Measure the stride from the first record rather than assuming
            // 0x30: a build with different vendor labels would otherwise
            // misalign every table that follows.
            int firstRecordStart = offset;
            if (!TrySkipIdentity(bytes, ref offset))
            {
                return Refusal.Malformed("The sdftoc install-part identity table is truncated.");
            }

            int stride = offset - firstRecordStart;
            if (stride <= 0)
            {
                return Refusal.Malformed("The sdftoc install-part identity stride is invalid.");
            }

            offset = firstRecordStart;
            if (!TrySkip(bytes, ref offset, (long)installPartCount * stride))
            {
                return Refusal.Malformed("The sdftoc install-part identity table is truncated.");
            }
        }

        long residentBytes = (long)residentCount * ResidentStride;
        if (residentBytes > bytes.Length - offset)
        {
            return Refusal.Malformed("The sdftoc resident-prefix table is truncated.");
        }

        ReadOnlyMemory<byte> residentTable = source.Slice(offset, (int)residentBytes);
        offset += (int)residentBytes;

        if (compressedSize > bytes.Length - offset)
        {
            return Refusal.Malformed("The sdftoc compressed file table is truncated.");
        }

        Result<byte[]> table = Inflate(
            source.Slice(offset, (int)compressedSize),
            inflatedSize,
            "the sdftoc file table");

        if (!table.TryGetValue(out byte[]? fileTable, out Refusal? refusal))
        {
            return refusal;
        }

        return Result.Ok(new SdfToc(
            version,
            fileTable,
            residentTable,
            (int)residentCount,
            (int)installPartCount,
            firstInstallPart,
            SdfIndexLayout.V16));
    }

    /// <summary>
    /// Inflates exactly <paramref name="expected"/> bytes of zlib data.
    /// </summary>
    /// <remarks>
    /// Bounding the inflate by the declared size keeps a corrupt header from
    /// forcing a large allocation, and requiring an exact match keeps a codec
    /// that returns a short buffer from passing as a partial success.
    /// </remarks>
    internal static Result<byte[]> Inflate(ReadOnlyMemory<byte> payload, long expected, string what)
    {
        if (expected < 0 || expected > int.MaxValue)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"{what} declares {expected} inflated bytes, which does not fit in one buffer."));
        }

        byte[] output = new byte[expected];

        try
        {
            using MemoryStream input = new(payload.ToArray(), writable: false);
            using ZLibStream stream = new(input, CompressionMode.Decompress);

            int read = stream.ReadAtLeast(output, output.Length, throwOnEndOfStream: false);
            if (read != output.Length)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{what} inflated to {read} bytes, but {expected} were declared."));
            }

            if (stream.ReadByte() != -1)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{what} inflates to more than its declared {expected} bytes."));
            }
        }
        catch (InvalidDataException error)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"{what} cannot be inflated: {error.Message}"));
        }
        catch (OutOfMemoryException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"Resources are insufficient to inflate {what}."));
        }

        return Result.Ok(output);
    }

    private static bool TryUInt32(ReadOnlySpan<byte> bytes, ref int offset, out uint value)
    {
        if (offset < 0 || bytes.Length - offset < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        offset += 4;
        return true;
    }

    private static bool TrySkip(ReadOnlySpan<byte> bytes, ref int offset, long count)
    {
        if (count < 0 || count > bytes.Length - offset)
        {
            return false;
        }

        offset += (int)count;
        return true;
    }

    /// <summary>
    /// Skips one identity record: a NUL-terminated label, an opaque blob, then
    /// a second NUL-terminated label.
    /// </summary>
    private static bool TrySkipIdentity(ReadOnlySpan<byte> bytes, ref int offset) =>
        TrySkipCString(bytes, ref offset) &&
        TrySkip(bytes, ref offset, IdentityBlobBytes) &&
        TrySkipCString(bytes, ref offset);

    private static bool TrySkipCString(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset < 0 || offset >= bytes.Length)
        {
            return false;
        }

        int end = bytes[offset..].IndexOf((byte)0);
        if (end < 0)
        {
            return false;
        }

        offset += end + 1;
        return true;
    }
}

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Editordata;

/// <summary>
/// Decodes an editordata file into its sections, materials and custom records.
/// </summary>
/// <remarks>
/// <para>
/// This reader keeps what the bytes say and decides nothing. It does not choose
/// a material, judge a shader family, resolve a texture path or apply a
/// default: those are association and reconstruction, and they belong above
/// this layer. Keeping every record, including the ones the exporter ignores,
/// is what lets a writer be added later without a second decode path.
/// </para>
/// <para>
/// Strings are length-prefixed Latin-1, which round-trips every byte: a texture
/// path is a byte string that happens to be spelled in ASCII, and decoding it
/// as UTF-8 would corrupt any path that is not.
/// </para>
/// </remarks>
public static class EditordataReader
{
    private const int IntermediateDataBytes = 12;

    /// <summary>Decodes <paramref name="file"/>.</summary>
    public static Result<EditordataFile> Read(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        ReadOnlySpan<byte> bytes = file.Memory.Span;
        int offset = 0;

        if (!TryUInt32(bytes, ref offset, out uint sectionCount))
        {
            return Refusal.Malformed("The editordata section count is truncated.");
        }

        // Every section contributes at least a material count, so a declared
        // count larger than the remaining bytes can hold is a malformed header
        // rather than an enormous allocation.
        if (sectionCount > (bytes.Length - offset) / 4)
        {
            return Refusal.Malformed("The editordata section table is truncated.");
        }

        ImmutableArray<EditordataMaterial>[] materials = new ImmutableArray<EditordataMaterial>[sectionCount];

        for (int section = 0; section < sectionCount; section++)
        {
            Result<ImmutableArray<EditordataMaterial>> read = ReadMaterials(bytes, ref offset, section);
            if (!read.TryGetValue(out ImmutableArray<EditordataMaterial> decoded, out Refusal? refusal))
            {
                return refusal;
            }

            materials[section] = decoded;
        }

        // One intermediate record per section, always present, always consumed
        // and never interpreted. The validated corpus holds zeros in all of it.
        string[] intermediateNames = new string[sectionCount];
        ImmutableArray<byte>[] intermediateData = new ImmutableArray<byte>[sectionCount];

        for (int section = 0; section < sectionCount; section++)
        {
            if (!TryString(bytes, ref offset, out string? name))
            {
                return Refusal.Malformed(Describe(section, "intermediate record name is truncated"));
            }

            if (!TryBytes(bytes, ref offset, IntermediateDataBytes, out ReadOnlySpan<byte> data))
            {
                return Refusal.Malformed(Describe(section, "intermediate record data is truncated"));
            }

            intermediateNames[section] = name;
            intermediateData[section] = [.. data];
        }

        int? customVersion = null;
        ImmutableArray<EditordataCustomRecord>[] customRecords =
            new ImmutableArray<EditordataCustomRecord>[sectionCount];
        Array.Fill(customRecords, ImmutableArray<EditordataCustomRecord>.Empty);

        if (offset < bytes.Length)
        {
            Result<int> tail = ReadCustomTail(bytes, ref offset, (int)sectionCount, customRecords);
            if (!tail.TryGetValue(out int version, out Refusal? refusal))
            {
                return refusal;
            }

            customVersion = version;
        }

        if (offset != bytes.Length)
        {
            // Trailing bytes mean the grammar and the file disagree about where
            // the file ends, which is exactly the condition a shifted cursor
            // produces. Refusing beats exporting from a reading that does not
            // account for every byte.
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The editordata has {bytes.Length - offset} unconsumed trailing bytes."));
        }

        ImmutableArray<EditordataSection>.Builder sections =
            ImmutableArray.CreateBuilder<EditordataSection>((int)sectionCount);

        for (int section = 0; section < sectionCount; section++)
        {
            sections.Add(new EditordataSection(
                section,
                materials[section],
                intermediateNames[section],
                intermediateData[section],
                customRecords[section]));
        }

        return Result.Ok(new EditordataFile(file.Path, sections.MoveToImmutable(), customVersion));
    }

    private static Result<ImmutableArray<EditordataMaterial>> ReadMaterials(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int section)
    {
        if (!TryUInt32(bytes, ref offset, out uint materialCount))
        {
            return Refusal.Malformed(Describe(section, "material count is truncated"));
        }

        // A section authored with no material at all is a real state, not a
        // truncation, and yields an empty list rather than a refusal.
        if (materialCount == 0)
        {
            return Result.Ok(ImmutableArray<EditordataMaterial>.Empty);
        }

        // Each material contributes at least two length prefixes and a count.
        if (materialCount > (bytes.Length - offset) / 8)
        {
            return Refusal.Malformed(Describe(section, "material table is truncated"));
        }

        ImmutableArray<EditordataMaterial>.Builder materials =
            ImmutableArray.CreateBuilder<EditordataMaterial>((int)materialCount);

        for (int material = 0; material < materialCount; material++)
        {
            if (!TryString(bytes, ref offset, out string? name))
            {
                return Refusal.Malformed(Describe(section, material, "name is truncated"));
            }

            if (!TryString(bytes, ref offset, out string? shader))
            {
                return Refusal.Malformed(Describe(section, material, "shader is truncated"));
            }

            if (!TryUInt32(bytes, ref offset, out uint channelCount))
            {
                return Refusal.Malformed(Describe(section, material, "channel count is truncated"));
            }

            if (channelCount > (bytes.Length - offset) / 4)
            {
                return Refusal.Malformed(Describe(section, material, "channel table is truncated"));
            }

            ImmutableArray<EditordataChannel>.Builder channels =
                ImmutableArray.CreateBuilder<EditordataChannel>((int)channelCount);

            for (int channel = 0; channel < channelCount; channel++)
            {
                if (!TryString(bytes, ref offset, out string? channelName) ||
                    !TryString(bytes, ref offset, out string? texturePath))
                {
                    return Refusal.Malformed(Describe(section, material, $"channel {channel} is truncated"));
                }

                channels.Add(new EditordataChannel(channelName, texturePath));
            }

            materials.Add(new EditordataMaterial(name, shader, channels.MoveToImmutable()));
        }

        return Result.Ok(materials.MoveToImmutable());
    }

    private static Result<int> ReadCustomTail(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int sectionCount,
        ImmutableArray<EditordataCustomRecord>[] records)
    {
        if (!TryUInt32(bytes, ref offset, out uint version))
        {
            return Refusal.Malformed("The editordata custom-data version is truncated.");
        }

        if (version is not (1 or 2 or 3))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The editordata declares custom-data version {version}, and only 1, 2 and 3 are implemented."));
        }

        for (int section = 0; section < sectionCount; section++)
        {
            if (!TryUInt32(bytes, ref offset, out uint recordCount))
            {
                return Refusal.Malformed(Describe(section, "custom record count is truncated"));
            }

            int recordBytes = RecordBytes((int)version);
            if (recordCount > (bytes.Length - offset) / recordBytes)
            {
                return Refusal.Malformed(Describe(section, "custom record table is truncated"));
            }

            ImmutableArray<EditordataCustomRecord>.Builder builder =
                ImmutableArray.CreateBuilder<EditordataCustomRecord>((int)recordCount);

            for (int record = 0; record < recordCount; record++)
            {
                Result<EditordataCustomRecord> decoded = ReadCustomRecord(bytes, ref offset, (int)version, section, record);
                if (!decoded.TryGetValue(out EditordataCustomRecord value, out Refusal? refusal))
                {
                    return refusal;
                }

                builder.Add(value);
            }

            records[section] = builder.MoveToImmutable();
        }

        return Result.Ok((int)version);
    }

    /// <summary>Bytes one custom record occupies at each version: 32, 40, 104.</summary>
    private static int RecordBytes(int version) => version switch
    {
        1 => 32,
        2 => 40,
        _ => 104,
    };

    private static Result<EditordataCustomRecord> ReadCustomRecord(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int version,
        int section,
        int record)
    {
        if (!TryFloat4(bytes, ref offset, out Float4 slot10) ||
            !TryFloat4(bytes, ref offset, out Float4 slot20))
        {
            return Refusal.Malformed(DescribeCustom(section, record, "is truncated"));
        }

        Float2 uvRepeat = new(1f, 1f);
        Float4 slot30 = new(1f, 1f, 1f, 1f);
        Float4 slot40 = default;
        Float4 slot50 = default;
        Float4 slot60 = default;

        if (version >= 2 && !TryFloat2(bytes, ref offset, out uvRepeat))
        {
            return Refusal.Malformed(DescribeCustom(section, record, "has a truncated uv repeat"));
        }

        if (version >= 3 &&
            (!TryFloat4(bytes, ref offset, out slot30) ||
             !TryFloat4(bytes, ref offset, out slot40) ||
             !TryFloat4(bytes, ref offset, out slot50) ||
             !TryFloat4(bytes, ref offset, out slot60)))
        {
            return Refusal.Malformed(DescribeCustom(section, record, "is truncated"));
        }

        // Every value this record can contribute to an export must be finite.
        // slot_50's RGB is the ambient term of the brightness scale and is
        // therefore checked; its W is excluded deliberately, being a packed
        // bitfield of raw UInt32 patterns rather than a float, so testing it
        // would refuse well-formed files over a value nothing reads as a number.
        if (!Finite(slot10) || !Finite(slot20) || !Finite(uvRepeat) ||
            !Finite(slot30) || !Finite(slot40) || !Finite(slot60) ||
            !float.IsFinite(slot50.X) || !float.IsFinite(slot50.Y) || !float.IsFinite(slot50.Z))
        {
            return Refusal.Malformed(DescribeCustom(section, record, "contains a non-finite value"));
        }

        return Result.Ok(new EditordataCustomRecord(
            version, slot10, slot20, uvRepeat, slot30, slot40, slot50, slot60));
    }

    private static bool Finite(Float4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool Finite(Float2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static string Describe(int section, string what) => string.Create(
        CultureInfo.InvariantCulture,
        $"The editordata section {section} {what}.");

    private static string Describe(int section, int material, string what) => string.Create(
        CultureInfo.InvariantCulture,
        $"The editordata section {section} material {material} {what}.");

    private static string DescribeCustom(int section, int record, string what) => string.Create(
        CultureInfo.InvariantCulture,
        $"The editordata section {section} custom record {record} {what}.");

    private static bool TryUInt32(ReadOnlySpan<byte> bytes, ref int offset, out uint value)
    {
        SpanReader reader = new(bytes);
        if (!reader.TrySeek(offset) || !reader.TryReadUInt32(out value))
        {
            value = 0;
            return false;
        }

        offset = reader.Position;
        return true;
    }

    private static bool TryBytes(ReadOnlySpan<byte> bytes, ref int offset, int count, out ReadOnlySpan<byte> value)
    {
        if (offset < 0 || count < 0 || bytes.Length - offset < count)
        {
            value = default;
            return false;
        }

        value = bytes.Slice(offset, count);
        offset += count;
        return true;
    }

    /// <summary>
    /// Reads a u16 length followed by that many Latin-1 bytes.
    /// </summary>
    private static bool TryString(ReadOnlySpan<byte> bytes, ref int offset, out string value)
    {
        SpanReader reader = new(bytes);
        if (!reader.TrySeek(offset) || !reader.TryReadUInt16(out ushort length))
        {
            value = string.Empty;
            return false;
        }

        offset = reader.Position;
        if (!TryBytes(bytes, ref offset, length, out ReadOnlySpan<byte> payload))
        {
            value = string.Empty;
            return false;
        }

        // Latin-1 round-trips every byte value, so a path spelled outside ASCII
        // survives verbatim instead of becoming a replacement character.
        value = Encoding.Latin1.GetString(payload);
        return true;
    }

    private static bool TryFloat4(ReadOnlySpan<byte> bytes, ref int offset, out Float4 value)
    {
        SpanReader reader = new(bytes);
        if (!reader.TrySeek(offset) ||
            !reader.TryReadSingle(out float x) || !reader.TryReadSingle(out float y) ||
            !reader.TryReadSingle(out float z) || !reader.TryReadSingle(out float w))
        {
            value = default;
            return false;
        }

        offset = reader.Position;
        value = new Float4(x, y, z, w);
        return true;
    }

    private static bool TryFloat2(ReadOnlySpan<byte> bytes, ref int offset, out Float2 value)
    {
        SpanReader reader = new(bytes);
        if (!reader.TrySeek(offset) ||
            !reader.TryReadSingle(out float x) || !reader.TryReadSingle(out float y))
        {
            value = default;
            return false;
        }

        offset = reader.Position;
        value = new Float2(x, y);
        return true;
    }
}

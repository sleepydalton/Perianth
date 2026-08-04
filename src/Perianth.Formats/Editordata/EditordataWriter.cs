using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Editordata;

/// <summary>
/// Writes an <see cref="EditordataFile"/> back to bytes.
/// </summary>
/// <remarks>
/// <para>
/// The first half of import that is not a texture (Roadmap §6.3), and the
/// reason <see cref="EditordataReader"/> keeps every record it decodes rather
/// than only the ones export selects: a writer needs the fields nothing reads
/// as much as the fields everything does.
/// </para>
/// <para>
/// <strong>The correctness bar here is byte equality, not plausibility.</strong>
/// A wrong read refuses; a wrong write produces a file that loads and
/// misbehaves, which the game will render without complaint and the author will
/// discover as a visual defect with no message attached. So this writes only
/// what a read proved, emits no default it was not given, and reorders nothing.
/// Read a real file, write it back, require the bytes to match — that is the
/// oracle, and it runs over the corpus in <c>EditordataCorpusTests</c>.
/// </para>
/// <para>
/// It follows that this writer has no opinions. It will not normalise a name,
/// supply an absent channel, sort anything, or upgrade a version 1 tail to
/// version 3. Every one of those would be an improvement that breaks the only
/// check capable of telling us the writer works at all.
/// </para>
/// </remarks>
public static class EditordataWriter
{
    private const int IntermediateDataBytes = 12;

    /// <summary>Bytes one custom record occupies at each version, matching the reader.</summary>
    private static int RecordBytes(int version) => version switch
    {
        1 => 32,
        2 => 40,
        _ => 104,
    };

    /// <summary>
    /// Serializes <paramref name="file"/>, or refuses if it cannot be spelled.
    /// </summary>
    /// <remarks>
    /// The refusals are all the same shape: a record holding something the
    /// grammar has no way to express. A name longer than a <c>u16</c> length
    /// prefix can count, a character Latin-1 cannot represent, a custom record
    /// whose version disagrees with the file's. None of them can arise from a
    /// file this reader decoded — they arise from a record assembled in code,
    /// which is exactly what import will do.
    /// </remarks>
    public static Result<byte[]> Write(EditordataFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.CustomVersion is int declared && declared is not (1 or 2 or 3))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The editordata declares custom-data version {declared}, and only 1, 2 and 3 are implemented."));
        }

        List<byte> bytes = new(Estimate(file));
        AddUInt32(bytes, (uint)file.Sections.Length);

        for (int index = 0; index < file.Sections.Length; index++)
        {
            EditordataSection section = file.Sections[index];

            // The ordinal is positional in the file and is not written, so a
            // record whose ordinal disagrees with its position would be
            // serialized as though it agreed. Refusing says which one is wrong
            // instead of silently picking the position.
            if (section.Ordinal != index)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The editordata section at position {index} carries ordinal {section.Ordinal}."));
            }

            AddUInt32(bytes, (uint)section.Materials.Length);

            foreach (EditordataMaterial material in section.Materials)
            {
                if (!TryAddString(bytes, material.Name, out Refusal? refusal) ||
                    !TryAddString(bytes, material.Shader, out refusal))
                {
                    return refusal;
                }

                AddUInt32(bytes, (uint)material.Channels.Length);

                foreach (EditordataChannel channel in material.Channels)
                {
                    if (!TryAddString(bytes, channel.Channel, out refusal) ||
                        !TryAddString(bytes, channel.TexturePath, out refusal))
                    {
                        return refusal;
                    }
                }
            }
        }

        foreach (EditordataSection section in file.Sections)
        {
            if (!TryAddString(bytes, section.IntermediateName, out Refusal? refusal))
            {
                return refusal;
            }

            if (section.IntermediateData.Length != IntermediateDataBytes)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The editordata section {section.Ordinal} intermediate record holds {section.IntermediateData.Length} bytes, and the grammar has exactly {IntermediateDataBytes}."));
            }

            bytes.AddRange(section.IntermediateData);
        }

        if (file.CustomVersion is int version)
        {
            AddUInt32(bytes, (uint)version);

            foreach (EditordataSection section in file.Sections)
            {
                AddUInt32(bytes, (uint)section.CustomRecords.Length);

                foreach (EditordataCustomRecord record in section.CustomRecords)
                {
                    // A record decoded at one version cannot be written at
                    // another: the versions differ in which fields exist, so
                    // the extra ones would be invented and the missing ones
                    // dropped. Both produce a file that loads.
                    if (record.Version != version)
                    {
                        return Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture,
                            $"The editordata section {section.Ordinal} holds a version {record.Version} custom record in a version {version} file."));
                    }

                    AddCustomRecord(bytes, record, version);
                }
            }
        }
        else
        {
            // No tail declared, so no record may exist. The reader produces this
            // pairing only together; a hand-built one could separate them, and
            // the records would then be dropped without a word.
            foreach (EditordataSection section in file.Sections)
            {
                if (!section.CustomRecords.IsEmpty)
                {
                    return Refusal.Unsupported(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The editordata section {section.Ordinal} holds {section.CustomRecords.Length} custom records, and the file declares no custom-data version to write them under."));
                }
            }
        }

        return Result.Ok(bytes.ToArray());
    }

    private static void AddCustomRecord(List<byte> bytes, EditordataCustomRecord record, int version)
    {
        AddFloat4(bytes, record.Slot10);
        AddFloat4(bytes, record.Slot20);

        if (version >= 2)
        {
            AddFloat(bytes, record.UvRepeat.X);
            AddFloat(bytes, record.UvRepeat.Y);
        }

        if (version >= 3)
        {
            AddFloat4(bytes, record.Slot30);
            AddFloat4(bytes, record.Slot40);
            AddFloat4(bytes, record.Slot50);
            AddFloat4(bytes, record.Slot60);
        }
    }

    /// <summary>
    /// A starting capacity, so the common file does not copy its way up from
    /// four bytes. Being wrong costs a reallocation and nothing else.
    /// </summary>
    private static int Estimate(EditordataFile file)
    {
        int total = 4;

        foreach (EditordataSection section in file.Sections)
        {
            // Two length prefixes and a count per material, two per channel,
            // plus the intermediate record. Strings are counted below.
            total += 4 + (section.Materials.Length * 8);
            total += 2 + IntermediateDataBytes;

            foreach (EditordataMaterial material in section.Materials)
            {
                total += material.Name.Length + material.Shader.Length;
                total += material.Channels.Length * 4;

                foreach (EditordataChannel channel in material.Channels)
                {
                    total += channel.Channel.Length + channel.TexturePath.Length;
                }
            }

            if (file.CustomVersion is int version)
            {
                total += 4 + (section.CustomRecords.Length * RecordBytes(version));
            }
        }

        return file.CustomVersion is null ? total : total + 4;
    }

    private static void AddUInt32(List<byte> target, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        target.AddRange(bytes);
    }

    private static void AddFloat(List<byte> target, float value)
    {
        // A bit reinterpretation, not a conversion: slot_50's W is a packed
        // feature bitfield whose raw UInt32 patterns include ones that are NaN
        // when read as a float, and they must survive being written back.
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
        target.AddRange(bytes);
    }

    private static void AddFloat4(List<byte> target, Float4 value)
    {
        AddFloat(target, value.X);
        AddFloat(target, value.Y);
        AddFloat(target, value.Z);
        AddFloat(target, value.W);
    }

    /// <summary>
    /// Writes a u16 length followed by that many Latin-1 bytes, mirroring
    /// <c>EditordataReader.TryString</c>.
    /// </summary>
    private static bool TryAddString(
        List<byte> target, string value, [NotNullWhen(false)] out Refusal? refusal)
    {
        if (value.Length > ushort.MaxValue)
        {
            refusal = Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"An editordata string is {value.Length} characters, and its length prefix holds {ushort.MaxValue}."));
            return false;
        }

        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)value.Length);
        target.AddRange(length);

        foreach (char character in value)
        {
            // Latin-1 is one byte per character and covers U+0000..U+00FF
            // exactly. Encoding.Latin1 substitutes '?' for anything above that,
            // which would write a valid file spelling a different path — so the
            // check is here rather than left to the encoder.
            if (character > 0xFF)
            {
                refusal = Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"An editordata string holds U+{(int)character:X4}, which Latin-1 cannot spell."));
                return false;
            }

            target.Add((byte)character);
        }

        refusal = null;
        return true;
    }
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Perianth.Tests.Editordata;

/// <summary>
/// Builds synthetic editordata files.
/// </summary>
internal sealed class EditordataBuilder
{
    private readonly List<SectionSpec> _sections = [];

    /// <summary>The custom-tail version, or null to emit no tail at all.</summary>
    public int? CustomVersion { get; set; } = 3;

    /// <summary>Bytes appended after everything the grammar accounts for.</summary>
    public byte[] Trailing { get; set; } = [];

    public EditordataBuilder Section(params MaterialSpec[] materials)
    {
        _sections.Add(new SectionSpec([.. materials], []));
        return this;
    }

    public EditordataBuilder SectionWithCustom(MaterialSpec[] materials, params CustomSpec[] custom)
    {
        _sections.Add(new SectionSpec([.. materials], [.. custom]));
        return this;
    }

    public byte[] Build()
    {
        List<byte> bytes = [];
        AddUInt32(bytes, (uint)_sections.Count);

        foreach (SectionSpec section in _sections)
        {
            AddUInt32(bytes, (uint)section.Materials.Count);
            foreach (MaterialSpec material in section.Materials)
            {
                AddString(bytes, material.Name);
                AddString(bytes, material.Shader);
                AddUInt32(bytes, (uint)material.Channels.Count);
                foreach ((string channel, string texture) in material.Channels)
                {
                    AddString(bytes, channel);
                    AddString(bytes, texture);
                }
            }
        }

        foreach (SectionSpec _ in _sections)
        {
            AddString(bytes, "intermediate");
            bytes.AddRange(new byte[12]);
        }

        if (CustomVersion is int version)
        {
            AddUInt32(bytes, (uint)version);
            foreach (SectionSpec section in _sections)
            {
                AddUInt32(bytes, (uint)section.Custom.Count);
                foreach (CustomSpec custom in section.Custom)
                {
                    AddFloat4(bytes, custom.Slot10);
                    AddFloat4(bytes, custom.Slot20);
                    if (version >= 2)
                    {
                        AddFloat(bytes, custom.UvRepeat.X);
                        AddFloat(bytes, custom.UvRepeat.Y);
                    }

                    if (version >= 3)
                    {
                        AddFloat4(bytes, custom.Slot30);
                        AddFloat4(bytes, custom.Slot40);
                        AddFloat4(bytes, custom.Slot50);
                        AddFloat4(bytes, custom.Slot60);
                    }
                }
            }
        }

        bytes.AddRange(Trailing);
        return [.. bytes];
    }

    private static void AddUInt32(List<byte> target, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        target.AddRange(bytes);
    }

    private static void AddString(List<byte> target, string value)
    {
        byte[] payload = Encoding.Latin1.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)payload.Length);
        target.AddRange(length);
        target.AddRange(payload);
    }

    private static void AddFloat(List<byte> target, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        target.AddRange(bytes);
    }

    private static void AddFloat4(List<byte> target, (float X, float Y, float Z, float W) value)
    {
        AddFloat(target, value.X);
        AddFloat(target, value.Y);
        AddFloat(target, value.Z);
        AddFloat(target, value.W);
    }

    private sealed record SectionSpec(List<MaterialSpec> Materials, List<CustomSpec> Custom);
}

/// <summary>One material to emit.</summary>
internal sealed record MaterialSpec(
    string Name,
    string Shader,
    List<(string Channel, string Texture)> Channels)
{
    /// <summary>The five channels every corpus material carries.</summary>
    public static MaterialSpec Standard(
        string name = "mat",
        string shader = "CamelDefaultShader",
        string diffuse = "tex/d.dds",
        string transparent = "",
        string emissive = "") => new(name, shader,
        [
            ("DiffuseColor", diffuse),
            ("NormalMap", ""),
            ("SpecularColor", ""),
            ("TransparentColor", transparent),
            ("EmissiveColor", emissive),
        ]);
}

/// <summary>One custom record to emit.</summary>
internal sealed record CustomSpec
{
    public (float X, float Y, float Z, float W) Slot10 { get; init; } = (1, 1, 1, 1);

    public (float X, float Y, float Z, float W) Slot20 { get; init; } = (0, 0, 0, 1);

    public (float X, float Y) UvRepeat { get; init; } = (1, 1);

    public (float X, float Y, float Z, float W) Slot30 { get; init; } = (1, 1, 1, 1);

    public (float X, float Y, float Z, float W) Slot40 { get; init; }

    public (float X, float Y, float Z, float W) Slot50 { get; init; }

    public (float X, float Y, float Z, float W) Slot60 { get; init; }
}

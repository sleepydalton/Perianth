using System.Collections.Immutable;

namespace Perianth.Formats.Editordata;

/// <summary>
/// The shader families whose material record 0 this build reconstructs.
/// </summary>
public enum ShaderFamily
{
    /// <summary>
    /// <c>CamelDefaultShader</c>. Opaque; keeps the diffuse image's own alpha
    /// bytes, which standard viewers then do not use for coverage.
    /// </summary>
    Ordinary,

    /// <summary>
    /// <c>CamelDefaultShader_Trans</c>. Replaces image alpha with the
    /// <c>TransparentColor</c> image's alpha band.
    /// </summary>
    Transparent,

    /// <summary>
    /// <c>CamelDefaultShader_Emissive</c>. An additive companion with no
    /// surface of its own, merged onto the base material it names.
    /// </summary>
    Emissive,
}

/// <summary>
/// One serialized texture channel: an engine channel name and the path it binds.
/// </summary>
/// <remarks>
/// Every material in the surveyed corpus carries the same five channels —
/// <c>DiffuseColor</c>, <c>NormalMap</c>, <c>SpecularColor</c>,
/// <c>TransparentColor</c> and <c>EmissiveColor</c> — and all five are kept
/// even though only three are read. <c>NormalMap</c> in particular is a raw-UV
/// colour-ramp input rather than a tangent-space normal map and must never
/// become a glTF <c>normalTexture</c>; keeping it named rather than silently
/// dropping it is what makes that decision visible.
/// </remarks>
/// <param name="Channel">The engine channel name, as serialized.</param>
/// <param name="TexturePath">The virtual path it binds, as serialized.</param>
public readonly record struct EditordataChannel(string Channel, string TexturePath);

/// <summary>
/// One material record, as the file spells it.
/// </summary>
/// <remarks>
/// Records after the first are decoded so the cursor stays correct and are
/// otherwise unused: only record 0 is selected. In the surveyed corpus every
/// one of 119,297 sections carries exactly one record, so the later-record path
/// is structural rather than exercised.
/// </remarks>
/// <param name="Name">The material name. An <c>__E</c> suffix marks an emissive companion.</param>
/// <param name="Shader">The shader name exactly as serialized.</param>
/// <param name="Channels">Every channel the record binds, in source order.</param>
public sealed record EditordataMaterial(
    string Name,
    string Shader,
    ImmutableArray<EditordataChannel> Channels);

/// <summary>
/// The per-section custom record, whichever version the file declares.
/// </summary>
/// <remarks>
/// <para>
/// Fields whose meaning is unresolved are kept whole rather than dropped. The
/// specification is explicit that <c>slot_30</c> and <c>slot_40</c> are
/// <em>observed</em> to behave as a colour gain and offset but were never read
/// out of a constant updater the way <c>slot_60.rgb</c> was, and this record
/// does not promote that inference by naming them for their supposed use.
/// </para>
/// <para>
/// <c>Slot50</c>'s W component is a packed shader-feature bitfield holding 52
/// distinct raw UInt32 patterns across the corpus, not a float and not culling
/// state. Authored two-sidedness does not exist anywhere in this record.
/// </para>
/// </remarks>
/// <param name="Version">1, 2 or 3, as the tail declared.</param>
/// <param name="Slot10">RGB is a behaviourally proven albedo tint; W is unresolved.</param>
/// <param name="Slot20">W is a behaviourally proven constant alpha; RGB is unresolved.</param>
/// <param name="UvRepeat">Shader-proven <c>myUVRepeat</c>. Present from version 2.</param>
/// <param name="Slot30">Observed, consistent with a colour gain. Present from version 3.</param>
/// <param name="Slot40">Observed, consistent with a colour offset. Present from version 3.</param>
/// <param name="Slot50">RGB ambient-consistent; W a packed feature bitfield. Present from version 3.</param>
/// <param name="Slot60">RGB is the runtime-proven emissive factor; W is runtime state. Present from version 3.</param>
public readonly record struct EditordataCustomRecord(
    int Version,
    Float4 Slot10,
    Float4 Slot20,
    Float2 UvRepeat,
    Float4 Slot30,
    Float4 Slot40,
    Float4 Slot50,
    Float4 Slot60);

/// <summary>Four consecutive binary32 values, kept whole.</summary>
public readonly record struct Float4(float X, float Y, float Z, float W);

/// <summary>Two consecutive binary32 values, kept whole.</summary>
public readonly record struct Float2(float X, float Y);

/// <summary>
/// One section of an editordata file.
/// </summary>
/// <param name="Ordinal">Its position, which must equal the source model-part ordinal.</param>
/// <param name="Materials">
/// Every material record, in source order. Empty when the section is authored
/// with no material at all, which is a real authored state rather than a
/// truncation, and distinct from a parse failure.
/// </param>
/// <param name="IntermediateName">The intermediate record's name.</param>
/// <param name="IntermediateData">Its twelve raw bytes, consumed and unresolved.</param>
/// <param name="CustomRecords">
/// The section's custom records, empty when the file carries no custom tail.
/// Only record 0 affects the selected material.
/// </param>
public sealed record EditordataSection(
    int Ordinal,
    ImmutableArray<EditordataMaterial> Materials,
    string IntermediateName,
    ImmutableArray<byte> IntermediateData,
    ImmutableArray<EditordataCustomRecord> CustomRecords);

/// <summary>
/// What an editordata file said.
/// </summary>
/// <param name="Path">The path as the caller supplied it.</param>
/// <param name="Sections">Every section, in source order.</param>
/// <param name="CustomVersion">
/// The custom-tail version, or null when the file carries no tail. Every one of
/// the 317 files in the surveyed corpus declares version 3; versions 1 and 2
/// are accepted because the specification names them, not because they occur.
/// </param>
public sealed record EditordataFile(
    string Path,
    ImmutableArray<EditordataSection> Sections,
    int? CustomVersion);

namespace Perianth.Formats.Diagnostics;

/// <summary>
/// Stable machine-readable diagnostic identifiers, from porting specification
/// section 12.1. Lower snake case, and they never embed a path, an ordinal or
/// prose — adding context to a diagnostic must not change its identifier.
/// </summary>
/// <remarks>
/// Only the general identifiers appear here, because they are the only ones
/// this build can raise. The rest arrive with the code that raises them; an
/// identifier for a condition that cannot yet occur is untestable, and section
/// 12.1 withdraws three identifiers precisely because they named conditions
/// that never arise.
/// </remarks>
public static class DiagnosticIds
{
    /// <summary>The bytes contradict a grammar.</summary>
    public const string InputMalformed = "input_malformed";

    /// <summary>A coherent mode this build does not implement, or a request the data cannot satisfy.</summary>
    public const string FormatUnsupported = "format_unsupported";

    /// <summary>Memory, disk or allocation capacity was exhausted.</summary>
    public const string ResourceInsufficient = "resource_insufficient";

    /// <summary>A required file or texture is absent, or an explicitly supplied root is invalid.</summary>
    public const string ResourceMissing = "resource_missing";

    /// <summary>
    /// The export is the model's complete part list rather than an appearance,
    /// because no setup hierarchy was supplied.
    /// </summary>
    public const string ExportUnposed = "export_unposed";

    /// <summary>
    /// A reconstructed material approximates the source shader, whose several
    /// runtime and additive inputs core glTF cannot express.
    /// </summary>
    public const string MaterialApproximated = "material_approximated";

    /// <summary>
    /// A transparent material's opacity is reconstructed from the two proven
    /// contributions, but its later serialized and runtime effects are not.
    /// </summary>
    public const string TransparentMaterialApproximated = "transparent_material_approximated";

    /// <summary>
    /// One primitive was omitted because reconciling its DiffuseColor repeat with
    /// its clamped TransparentColor alpha would bake an image past the size cap.
    /// The rest of the export is unaffected, so it is a partial export rather than
    /// a refusal.
    /// </summary>
    public const string PrimitiveOmittedBakeTooLarge = "primitive_omitted_bake_too_large";

    /// <summary>
    /// A file changed while it was being read, so the bytes in hand are not a
    /// coherent snapshot of any version of it. Not one of section 12.1's
    /// initial identifiers: that list covers the conditions the specification
    /// had already met, and section 2 requires this refusal without naming it.
    /// </summary>
    public const string InputChangedDuringRead = "input_changed_during_read";

    /// <summary>
    /// An extraction succeeded here but wrote a path longer than Windows accepts
    /// by default, so unpacking the same set there would refuse. A warning
    /// rather than a refusal: the extraction on this machine is correct and
    /// complete, and only its portability is in question.
    /// </summary>
    public const string ExtractionPathNotPortable = "extraction_path_not_portable";

    /// <summary>
    /// The caller stopped an extraction partway. The files already written are
    /// complete and described by the manifest; nothing was wrong with the
    /// request, which is why this is a warning on a result rather than a
    /// refusal.
    /// </summary>
    public const string ExtractionCancelled = "extraction_cancelled";

    /// <summary>
    /// An authored texture is a different size from the one it replaces. The
    /// game stretches it over the same surface, so this is often deliberate and
    /// is never refused — whether it is a good idea belongs to whoever drew it.
    /// </summary>
    public const string TextureSizeChanged = "texture_size_changed";

    /// <summary>
    /// An authored texture carries fewer mip levels than the one it replaces.
    /// It loads either way — tested in game — so the cost is shimmering at a
    /// distance rather than a failure. Worth saying because 46,890 of the
    /// 47,321 shipped textures carry a full chain, so a single level is
    /// overwhelmingly a first attempt rather than a choice.
    /// </summary>
    public const string TextureMipsDropped = "texture_mips_dropped";
}

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

    /// <summary>
    /// The chosen animation has a duration but no movement: every channel holds
    /// one value for its whole length. 632 of the game's 9,469 ANIMs are
    /// authored this way — prop states such as opened or destroyed, idles, and
    /// loops that hold while something else moves — so this is a routine
    /// outcome rather than a broken file, and the animation still chooses a
    /// pose worth exporting.
    /// <para>
    /// It carries an identifier because the remedy is to ask for the same
    /// export without animation, and each front end spells that differently:
    /// the window has a "Play the whole clip" checkbox, the command line has
    /// <c>--animate</c>. Naming either one in the shared message would put the
    /// wrong instruction in front of half the users.
    /// </para>
    /// </summary>
    public const string ClipHasNoMotion = "clip_has_no_motion";

    /// <summary>
    /// A hierarchy names this model's parts and its visibility shows none of
    /// them.
    /// </summary>
    /// <remarks>
    /// Ordinary for equipment rather than a fault. A character's setup names
    /// every alternative piece and hides them all — 294 of the game's 1,196
    /// equipment models come out this way — because a pose has to choose one and
    /// the choice is made elsewhere. It carries an identifier so a caller
    /// drawing several models can leave that one out and say so, instead of
    /// failing an export over a piece somebody merely ticked.
    /// </remarks>
    public const string PoseSelectsNothing = "pose_selects_nothing";

    /// <summary>
    /// A material edit named something no section binds.
    /// </summary>
    /// <remarks>
    /// A refusal, because an edit that quietly changed nothing would write a
    /// mod indistinguishable from a working one. It carries an id because one
    /// caller legitimately expects it: applying an item's own colour to each of
    /// a hairstyle's six variants, where a variant need not use every sheet the
    /// others do. Telling that apart from a mistyped path is the whole point.
    /// </remarks>
    public const string MaterialEditMatchedNothing = "material_edit_matched_nothing";
}

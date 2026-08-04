namespace Perianth.Gltf;

/// <summary>
/// Strings that appear in the output file and are therefore part of the
/// behaviour, not decoration.
/// </summary>
public static class GlbNames
{
    /// <summary>
    /// The scene name when no setup hierarchy was applied.
    /// </summary>
    /// <remarks>
    /// Specification section 11 calls this observable output a port must
    /// reproduce. An unposed export is the model's complete part list rather
    /// than an appearance: nothing places the parts and nothing selects between
    /// alternate states, so every variant the setup would have chosen between is
    /// present at once. A GLB outlives the tool that made it, so the file has to
    /// say which kind it is, and viewers show this in their outliner.
    /// </remarks>
    public const string UnposedScene = "unposed-all-parts";

    /// <summary>The scene name when a setup hierarchy was applied.</summary>
    public const string PosedScene = "posed";

    /// <summary>
    /// The name of the root node that converts source space to glTF space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This said <c>MMBTool</c> until 2026-08-03, matching the frozen Python
    /// reference verbatim, on the grounds that the structural baseline compares
    /// node names and a rename would fail every specimen while looking like a
    /// tidy-up.
    /// </para>
    /// <para>
    /// That was the wrong call. The name is not an internal detail: it is
    /// written into every file this tool produces and read by whoever opens one
    /// in Blender, and it named a program that no longer exists. Renamed
    /// deliberately, with the 34 specimens re-recorded — the divergence from the
    /// reference is one string, and it is the reference that is wrong here.
    /// </para>
    /// </remarks>
    public const string PresentationBasisNode = "Perianth source-to-glTF presentation basis";

    /// <summary>
    /// The generator recorded in the asset block.
    /// </summary>
    /// <remarks>
    /// Deliberately not the Python tool's <c>MMBTool 0.1</c>. This field is not
    /// part of the compared fingerprint, and it should say honestly which
    /// program wrote the file.
    /// </remarks>
    public const string Generator = "Perianth 0.1";

    /// <summary>The suffix a mesh's node name adds to the mesh name.</summary>
    public const string NodeSuffix = "-node";

    /// <summary>
    /// The material every untextured posed part shares, carrying only its
    /// double-sidedness.
    /// </summary>
    /// <remarks>
    /// Posed parts are emitted double-sided to preserve mirrored planar winding,
    /// and glTF expresses that on a material. A part with no reconstructed
    /// material still needs one to say so, and one shared instance is enough.
    /// Compared verbatim by the baseline, so the name is behaviour.
    /// </remarks>
    public const string PlanarDefaultMaterial = "Camel planar default";
}

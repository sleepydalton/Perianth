using System.Globalization;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using Perianth.Core.Geometry;
using Perianth.Formats.Binary;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;

namespace Perianth.Core.Content;

/// <summary>What bringing an edited model back changed.</summary>
/// <param name="Cameldata">The edited positions, ready to write.</param>
/// <param name="Model">
/// The MMB to ship. Byte-identical to the original unless a part was rebuilt,
/// and even then only inside the payloads a part already owned.
/// </param>
/// <param name="Added">
/// How many parts the edited file named beyond the model's end, and which were
/// therefore created. Reported because a part appearing in a model is the least
/// visible thing an import can do: the copy it starts as is identical to its
/// source until the mesh that asked for it is applied.
/// </param>
/// <param name="AddedBinding">
/// The node the added parts bind to, or empty where none were added. Reported
/// because it is chosen for the author rather than by them, and because a node
/// the setup hides makes the new part invisible with nothing to say so — which
/// is exactly what happened to an in-game probe: the model's last part bound to
/// a node under a hidden parent, so the part that copied it never drew and the
/// null result was read as a fact about the game. Roadmap §10.118.
/// </param>
/// <param name="Converted">
/// How many parts were switched to store the texture layout the mesh brought,
/// rather than working one out from position.
/// </param>
/// <param name="LayoutIgnored">
/// How many parts brought a texture layout that went unused. Nothing in the
/// written files shows this, and the difference is a model painted as its author
/// intended against one painted by a projector — so it is counted and said.
/// </param>
/// <param name="Reshaped">How many parts kept their arrangement and only moved.</param>
/// <param name="Rebuilt">How many parts were given a new arrangement.</param>
/// <param name="Slots">How many pool entries the reshaped parts moved.</param>
/// <param name="Depths">How many of those were depths rather than XY.</param>
/// <param name="Uv0Slots">
/// How many texture coordinates the reshaped parts moved. Only a part that
/// stores its own has any, and moving its points without them left the layout
/// describing the shape it used to be.
/// </param>
/// <param name="LayoutUnconvertible">
/// How many parts asked to store the layout they brought and could not, because
/// they kept their arrangement and a reshape writes no payload. Counted
/// separately from <paramref name="LayoutIgnored"/> because the advice differs:
/// there is nothing the author can pass to make this one happen, and the flag
/// used to be accepted in silence. Roadmap §10.121.
/// </param>
/// <param name="Triangles">How many triangles the rebuilt parts now draw.</param>
public sealed record GeometryImportResult(
    Mode3Cameldata Cameldata,
    byte[] Model,
    int Added,
    string AddedBinding,
    int Reshaped,
    int Rebuilt,
    int Converted,
    int LayoutIgnored,
    int Slots,
    int Depths,
    int Uv0Slots,
    int LayoutUnconvertible,
    int Triangles)
{
    /// <summary>Whether the edit would change anything at all.</summary>
    /// <remarks>
    /// A rebuilt part changed by definition — that is what put it on that side of
    /// the split — so only the reshaped ones need counting. Writing a mod that
    /// changes nothing is the failure worth naming: it installs, it loads, and it
    /// is indistinguishable from one that silently did not work.
    ///
    /// A model that only gained parts has changed too, even where nothing moved:
    /// duplicating a part in Blender and exporting without touching it is a small
    /// thing to do and the resulting mod is not a no-op.
    ///
    /// So has one whose texture coordinates moved and whose points did not.
    /// Re-laying a storing part's layout without touching its shape is an
    /// ordinary thing to do in a 3D package, and leaving it out here refused the
    /// edit while advising the author to use Edit Mode — which is what they had
    /// just done.
    /// </remarks>
    public bool Moved => Added > 0 || Rebuilt > 0 || Slots > 0 || Depths > 0 || Uv0Slots > 0;
}

/// <summary>
/// Brings an edited mesh back into a model, whichever kind of edit it was.
/// </summary>
/// <remarks>
/// <para>
/// Reshaping a part and rebuilding it are two operations with two sets of limits,
/// but they are not two things to choose between: an author edited a mesh and
/// wants it in the game. Which operation applies is a property of the file rather
/// than a decision, so it is read off the file.
/// </para>
/// <para>
/// <strong>The question is whether the mesh still shares the corners the part
/// does.</strong> A part stores one position per shared corner and names it two
/// to twelve times, so moving the corner moves every vertex on it. If every such
/// group still sits at one position, the arrangement survived and the edit is a
/// <see cref="GeometryEdit">reshape</see> — which writes only the cameldata, and
/// so also serves the parts a rebuild cannot reach. If any group came apart, the
/// mesh is a different picture and it is a
/// <see cref="GeometryReplace">rebuild</see>, which reassigns the corners and
/// rewrites the part's payload in place.
/// </para>
/// <para>
/// The split is per part, so one file may do both: a model whose hat was
/// redrawn and whose arms were merely stretched is one import.
/// </para>
/// <para>
/// This is a look, not a retry. Nothing is attempted and abandoned — the
/// predicate below is exactly the pair of conditions a reshape refuses on, so
/// the part is sent where it can succeed rather than sent twice.
/// </para>
/// </remarks>
public static class GeometryImport
{
    /// <summary>Applies every mesh in <paramref name="parts"/> to the model it names.</summary>
    /// <param name="modelFile">The model's own bytes.</param>
    /// <param name="model">The model to change.</param>
    /// <param name="cameldata">Its coordinates.</param>
    /// <param name="parts">The meshes, each naming the part it applies to.</param>
    /// <param name="ownUv0">
    /// Whether a redrawn part should start storing the texture layout its mesh
    /// brought instead of working one out from position. See
    /// <see cref="GeometryReplace.Replace"/>; a reshaped part is unaffected,
    /// because a reshape writes no payload and the layout it works out follows
    /// the points wherever they go.
    /// </param>
    public static Result<GeometryImportResult> Apply(
        SourceFile modelFile,
        MmbModel model,
        CameldataFile cameldata,
        IReadOnlyList<EditedPart> parts,
        bool ownUv0 = false)
    {
        ArgumentNullException.ThrowIfNull(modelFile);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cameldata);
        ArgumentNullException.ThrowIfNull(parts);

        if (cameldata is not Mode3Cameldata source)
        {
            return Refusal.Unsupported(
                "This model's cameldata is mode 2, whose position pool is shared between parts, so "
                + "editing one part could move another. Only mode 3 can be edited.");
        }

        if (parts.Count == 0)
        {
            return Refusal.Unsupported(
                "The edited file has no meshes in it, so there is nothing to bring back.");
        }

        // A mesh naming a part beyond the model's end is a part the author added
        // in Blender, so the model grows to hold it before anything is applied.
        // Each new part starts as a copy of the last one, which is how it comes
        // by the declarations, flag bytes and LOD flags that make a record legal
        // -- none of which could be invented here. The copy's geometry is then
        // overwritten by the mesh that asked for it, below, exactly as any other
        // part's would be.
        Result<Grown> grown = GrowToFit(model, source, parts);
        if (!grown.TryGetValue(out Grown made, out Refusal? growing))
        {
            return growing;
        }

        source = made.Cameldata;
        int added = made.Added;
        string addedBinding = added > 0 ? made.Model.Parts[^1].BindingNode : string.Empty;
        if (added > 0)
        {
            // Everything below writes payloads *into* the file it was read from,
            // at the offsets the records name — and the file in hand has no new
            // part in it. So the grown model is serialized and read straight
            // back, and the rest of the import works on a file whose bytes and
            // records agree. Doing it here rather than in each branch is what
            // keeps the two halves unaware that a part was ever added.
            Result<byte[]> serialized = MmbContainerWriter.Write(made.Model);
            if (!serialized.TryGetValue(out byte[]? written, out Refusal? writing))
            {
                return writing;
            }

            modelFile = SourceFile.FromMemory(modelFile.Path, written);
            Result<MmbModel> reread = MmbReader.Read(modelFile);
            if (!reread.TryGetValue(out MmbModel? grownModel, out Refusal? rereading))
            {
                return rereading;
            }

            model = grownModel;
        }
        else
        {
            model = made.Model;
        }

        List<EditedPart> reshape = [];
        List<EditedPart> rebuild = [];

        foreach (EditedPart part in parts)
        {
            Result<int> ordinal = GeometryEdit.Ordinal(part.Name);
            if (!ordinal.TryGetValue(out int index, out Refusal? refusal))
            {
                return refusal;
            }

            // A mesh naming a part the model does not have is a refusal either
            // way, and the reshape's is the one that names the model's size.
            bool known = index < model.Parts.Length && index < source.Constants.Length;
            bool keeps = !known ||
                KeepsArrangement(part, model.Parts[index], source.Constants[index], source);

            (keeps ? reshape : rebuild).Add(part);
        }

        Mode3Cameldata current = source;
        int reshaped = 0;
        int slots = 0;
        int depths = 0;
        int uv0Slots = 0;
        int reshapeIgnored = 0;

        if (reshape.Count > 0)
        {
            Result<GeometryEditResult> edit = GeometryEdit.Reshape(model, current, reshape);
            if (!edit.TryGetValue(out GeometryEditResult? moved, out Refusal? refusal))
            {
                return refusal;
            }

            current = moved.Cameldata;
            model = moved.Model;
            reshaped = moved.Parts;
            slots = moved.Slots;
            depths = moved.Depths;
            uv0Slots = moved.Uv0Slots;
            reshapeIgnored = moved.LayoutIgnored;
        }

        // Every record owns a private slice of the pools, so the order the two
        // halves run in cannot matter: neither can reach the other's entries.
        int rebuilt = 0;
        int triangles = 0;
        int converted = 0;
        int ignored = 0;
        byte[] bytes;

        if (rebuild.Count > 0)
        {
            Result<GeometryReplacement> replaced =
                GeometryReplace.Replace(modelFile, model, current, rebuild, ownUv0);
            if (!replaced.TryGetValue(out GeometryReplacement? done, out Refusal? refusal))
            {
                return refusal;
            }

            current = done.Cameldata;
            rebuilt = done.Parts;
            triangles = done.Triangles;
            converted = done.Converted;
            ignored = done.LayoutIgnored;
            bytes = done.Model;
        }
        else if (reshaped > 0)
        {
            // A reshape writes no payload, but it moves geometry, and the
            // bounding block is derived from geometry and lives in the MMB. So
            // the model is serialized rather than spliced: WithPayloads copies
            // the original bytes and replaces payloads inside them, which would
            // carry every reshaped part's old volume through untouched.
            Result<byte[]> written = MmbContainerWriter.Write(model);
            if (!written.TryGetValue(out byte[]? reserialized, out Refusal? refusal))
            {
                return refusal;
            }

            bytes = reserialized;
        }
        else
        {
            // Nothing was reshaped and nothing rebuilt, so the answer is the
            // original, byte for byte. Asking the writer for it with nothing to
            // replace is how Core gets at the bytes without learning to read an
            // MMB, and it keeps an edit that changed nothing producing the bytes
            // it read rather than a re-serialization that merely ought to match.
            Result<byte[]> copy = MmbWriter.WithPayloads(modelFile, model, new Dictionary<int, byte[]>());
            if (!copy.TryGetValue(out byte[]? original, out Refusal? refusal))
            {
                return refusal;
            }

            bytes = original;
        }

        // A part that kept its arrangement cannot be converted: ownUv0 reaches
        // GeometryReplace alone, and the flag was accepted in silence until this
        // counted it (Roadmap §10.121).
        //
        // Only counted when the flag is set, and that is not a softening. A
        // reshaped projecting part is re-projected from its new points, so its
        // layout does follow them and nothing was lost -- while every GLB
        // carries coordinates on everything, so counting them all would report a
        // loss on every reshape ever run. What is worth saying is the narrow
        // thing: the author asked for the layout to be stored and it was not.
        int unconvertible = ownUv0 ? reshapeIgnored : 0;

        return Result.Ok(
            new GeometryImportResult(
                current, bytes, added, addedBinding, reshaped, rebuilt, converted,
                ignored + unconvertible, slots, depths, uv0Slots, unconvertible, triangles));
    }

    /// <summary>
    /// Whether an indexed part's corners still read the entries the host's do.
    /// </summary>
    /// <param name="part">The edited mesh, stating a slot for each of its vertices.</param>
    /// <param name="stored">The host's index buffer, one vertex number per corner.</param>
    /// <param name="hostIds">The host's identifiers, one pool entry per vertex.</param>
    private static bool ReadsTheSameEntries(
        EditedPart part, ImmutableArray<int> stored, ImmutableArray<int> hostIds)
    {
        if (part.Indices.Length != stored.Length)
        {
            return false;
        }

        for (int corner = 0; corner < stored.Length; corner++)
        {
            int vertex = part.Indices[corner];
            if (vertex < 0 || vertex >= part.PoolSlots.Length ||
                stored[corner] < 0 || stored[corner] >= hostIds.Length)
            {
                return false;
            }

            if (part.PoolSlots[vertex] != hostIds[stored[corner]])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="part"/> still shares the corners its host does.
    /// </summary>
    /// <remarks>
    /// True sends it to the reshape, which is preferred wherever it applies: it
    /// writes no payload at all, and so reaches the two kinds of part a rebuild
    /// refuses — one storing an index buffer, and one carrying its own texture
    /// coordinates. Anything the reshape would refuse for a reason other than the
    /// arrangement is also sent there, so the refusal explaining it is the one
    /// written for that case.
    /// </remarks>
    /// <summary>A model and its pools after growing to fit an edited file.</summary>
    private readonly record struct Grown(MmbModel Model, Mode3Cameldata Cameldata, int Added);

    /// <summary>
    /// Adds the parts an edited file names beyond the model's end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blender's importer names a mesh <c>mode3-record-N</c>, and an author who
    /// duplicated one gets <c>N</c> past the model's last part. Refusing that was
    /// the honest thing to do while nothing could add a part; now that something
    /// can, it is the ordinary way a model gains geometry.
    /// </para>
    /// <para>
    /// The ordinals must be contiguous. A file naming parts 40 and 42 for a model
    /// of 40 has lost one somewhere — Blender renames a duplicate rather than
    /// renumbering the rest — and inventing part 41 to fill the gap would put a
    /// copy of somebody else's geometry into the model unasked.
    /// </para>
    /// <para>
    /// Each new part copies the model's <b>last</b> part, and which one hardly
    /// matters: every field a copy carries is either overwritten by the mesh that
    /// asked for it or a constant of the format. It keeps the source's label,
    /// which binds it to a node the model already declares — a new part on a new
    /// joint is a further rung and is not this.
    /// </para>
    /// </remarks>
    private static Result<Grown> GrowToFit(
        MmbModel model, Mode3Cameldata cameldata, IReadOnlyList<EditedPart> parts)
    {
        int highest = -1;
        foreach (EditedPart part in parts)
        {
            Result<int> ordinal = GeometryEdit.Ordinal(part.Name);
            if (ordinal.TryGetValue(out int index, out _) && index > highest)
            {
                highest = index;
            }
        }

        int wanted = highest + 1 - model.Parts.Length;
        if (wanted <= 0)
        {
            return Result.Ok(new Grown(model, cameldata, 0));
        }

        if (model.Parts.Length == 0)
        {
            return Refusal.Unsupported(
                "This model has no parts to copy, so there is nothing for a new one to be made from.");
        }

        // Contiguity, checked before anything is built.
        HashSet<int> named = [];
        foreach (EditedPart part in parts)
        {
            Result<int> ordinal = GeometryEdit.Ordinal(part.Name);
            if (ordinal.TryGetValue(out int index, out _))
            {
                _ = named.Add(index);
            }
        }

        for (int index = model.Parts.Length; index <= highest; index++)
        {
            if (!named.Contains(index))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The edited file names part {highest} for a model of {model.Parts.Length}, but nothing in it names part {index}. New parts have to run on from the last one without a gap; renumber the added meshes so they do."));
            }
        }

        MmbModel grown = model;
        Mode3Cameldata pools = cameldata;
        for (int made = 0; made < wanted; made++)
        {
            Result<PartAddOutcome> added = PartAdd.Duplicate(
                grown, pools, grown.Parts.Length - 1, grown.Parts[^1].Label);
            if (!added.TryGetValue(out PartAddOutcome? outcome, out Refusal? refusal))
            {
                return refusal;
            }

            grown = outcome.Model;
            pools = outcome.Cameldata;
        }

        return Result.Ok(new Grown(grown, pools, wanted));
    }

    private static bool KeepsArrangement(
        EditedPart part, MmbModelPart host, Mode3Constant constant, Mode3Cameldata source)
    {
        ImmutableArray<int> localIds;
        if (!part.PoolSlots.IsDefaultOrEmpty)
        {
            localIds = part.PoolSlots;
        }
        else
        {
            Result<ImmutableArray<int>> ids = GeometryAssembler.LocalIds(host);
            if (ids.IsRefused)
            {
                return true;
            }

            localIds = ids.Value;
        }

        // A part that stores an index buffer can be re-topologised without a
        // single point moving: the corners say which vertex each triangle draws,
        // and changing those changes the picture entirely. The position checks
        // below cannot see it -- everything is where it was -- so the part would
        // be reshaped, which writes no payload, and the edit would vanish.
        //
        // **Which pool entry each corner reads, not which vertex.** Comparing
        // the raw numbers reads an untouched round trip as re-topologised: a
        // tool that drops a vertex no triangle references renumbers the rest,
        // which Blender does and is right to do. The entry a corner ends up
        // reading is what the picture is made of, and it survives renumbering.
        if (host.Descriptor.IsIndexed && !part.Indices.IsDefaultOrEmpty && !part.PoolSlots.IsDefaultOrEmpty)
        {
            // A host whose identifiers cannot be read cannot be compared
            // against, and "cannot tell" is not "unchanged". Treating it as
            // unchanged sends a redraw to the reshape, which writes no payload,
            // so the edit disappears without a word -- exactly the failure the
            // comparison above exists to prevent. It goes to the rebuild
            // instead, whose refusal names what about the payload it cannot read.
            Result<ImmutableArray<int>> hostIds = GeometryAssembler.LocalIds(host);
            if (!hostIds.TryGetValue(out ImmutableArray<int> known, out _) ||
                !ReadsTheSameEntries(part, host.StoredIndices, known))
            {
                return false;
            }
        }

        // A changed vertex count used to be refused by both, so it went to the
        // reshape for the better message. A rebuild can now do it: the pools are
        // re-based and the container written afresh, so a direct part may gain
        // or lose points. It goes there instead, and an indexed part -- which
        // still cannot -- gets the refusal that says why.
        if (localIds.Length != part.Positions.Length)
        {
            return false;
        }

        BitReader packed = new(source.PackedZ.AsSpan());
        Dictionary<int, Vector2> flat = [];
        Dictionary<long, float> plane = [];

        for (int vertex = 0; vertex < localIds.Length; vertex++)
        {
            Vector3D position = part.Positions[vertex];

            // Narrowed before comparing, as the edit narrows before writing: the
            // pool is float, so two doubles differing past float precision are
            // one position here and must not read as the arrangement changing.
            Vector2 xy = new((float)position.X, (float)position.Y);
            float depth = (float)position.Z;

            if (flat.TryGetValue(localIds[vertex], out Vector2 already) && already != xy)
            {
                return false;
            }

            flat[localIds[vertex]] = xy;

            long xyIndex = constant.XyBase + (long)localIds[vertex];
            if (!packed.TryRead(xyIndex * constant.ZBitWidth, constant.ZBitWidth, out uint offset))
            {
                return true;
            }

            long zIndex = constant.ZBase + offset;
            if (plane.TryGetValue(zIndex, out float alreadyDepth) && !alreadyDepth.Equals(depth))
            {
                return false;
            }

            plane[zIndex] = depth;
        }

        return true;
    }
}

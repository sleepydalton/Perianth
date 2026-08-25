using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Perianth.Core.Geometry;
using Perianth.Formats.Binary;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;

namespace Perianth.Core.Content;

/// <summary>What replacing a part's geometry produced.</summary>
/// <param name="Model">The MMB, with the rewritten payloads.</param>
/// <param name="Cameldata">The cameldata, with the rewritten pools.</param>
/// <param name="Parts">How many parts were given new geometry.</param>
/// <param name="Triangles">How many triangles those parts now draw.</param>
/// <param name="Converted">
/// How many parts were switched from working their texture layout out to storing
/// the one the mesh brought.
/// </param>
/// <param name="LayoutIgnored">
/// How many parts brought a texture layout that was not used, because they work
/// theirs out from position and were not asked to stop. Counted rather than left
/// silent: it is the difference between a model painted as its author intended
/// and one painted by a projector, and nothing in the written files shows which
/// happened.
/// </param>
public sealed record GeometryReplacement(
    byte[] Model,
    Mode3Cameldata Cameldata,
    int Parts,
    int Triangles,
    int Converted = 0,
    int LayoutIgnored = 0);

/// <summary>
/// Puts a different mesh into an existing part.
/// </summary>
/// <remarks>
/// <para>
/// The ladder's second rung. Where a reshape moves the vertices a part already
/// has, this replaces the arrangement as well: which vertices share a position,
/// and therefore what the part is a picture of. It still writes no container —
/// a direct record's payload is one identifier per vertex, so at a fixed vertex
/// count its length is fixed and it can be rewritten where it lies.
/// </para>
/// <para>
/// A <strong>direct</strong> part may now be given more triangles, more points
/// and more depth planes than it had. None of those was ever caution; each was a
/// thing the file could not express until the piece underneath it was built.
/// The container is written rather than patched, so a payload may change length;
/// the pools are re-based, so a slice may grow; and the depth index is widened
/// to fit, so a part may stand on more planes than its model's records used to
/// name between them.
/// </para>
/// <para>
/// A mesh with fewer triangles than its host is welcome too: collapse the spare
/// ones to a point and they draw nothing, which is the same move that makes a
/// part vanish.
/// </para>
/// <para>
/// A part storing an <strong>index buffer</strong> is written too, by a second
/// path: its identifiers and its index buffer are two arrays rather than one, at
/// offsets that cannot move, and an index is stored biased. Such a part keeps
/// the author's own corners rather than being re-welded into new ones, and may
/// come back with fewer vertices than it had — a vertex no triangle references
/// draws nothing, and tools drop those — in which case the identifiers nothing
/// looks at keep the bytes they had.
/// </para>
/// <para>
/// Two kinds of part are still refused, both per part and never per model: one
/// that carries its own texture coordinates instead of computing them, since
/// rearranging its vertices would keep the old coordinates and paint it wrongly;
/// and one whose payload holds a further per-vertex stream after the identifiers
/// (14 parts in 7,923), which rearranging would likewise leave describing the
/// arrangement that was. Both can still be reshaped, which writes no payload.
/// </para>
/// </remarks>
public static class GeometryReplace
{
    /// <summary>Replaces the geometry of every part <paramref name="parts"/> names.</summary>
    /// <param name="modelFile">The model's own bytes, for the container writer.</param>
    /// <param name="model">The model to change.</param>
    /// <param name="cameldata">Its coordinates.</param>
    /// <param name="parts">The meshes, each naming the part it replaces.</param>
    /// <param name="ownUv0">
    /// Whether a part that works its texture layout out from position should be
    /// switched to store the one the mesh brought. Off by default, because 86%
    /// of parts work it out and for flat art a projection is the right answer —
    /// switching every one of them would grow the file and lose the free
    /// reprojection a later reshape gets. On, it is what makes a genuinely
    /// three-dimensional imported mesh paint as its author intended rather than
    /// having one image smeared down its sides.
    /// </param>
    public static Result<GeometryReplacement> Replace(
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
                "This model's cameldata is mode 2, whose position pool is shared between parts, "
                + "so giving one part new geometry could move another. Only mode 3 can be replaced.");
        }

        // Plan every part before writing any of it. A part that gained points
        // moves every later part's slice, so no address is known until all of
        // them have said how much room they want.
        Dictionary<int, Plan> planned = [];
        foreach (EditedPart part in parts)
        {
            Result<int> ordinal = GeometryEdit.Ordinal(part.Name);
            if (!ordinal.TryGetValue(out int index, out Refusal? nameRefusal))
            {
                return nameRefusal;
            }

            if (index >= model.Parts.Length || index >= source.Constants.Length)
            {
                return Refusal.Unsupported(Describe(
                    part.Name, $"names part {index}, and this model has {model.Parts.Length}"));
            }

            Result<Plan> plan = PlanOne(part, model.Parts[index], source, index, ownUv0);
            if (!plan.TryGetValue(out Plan value, out Refusal? refusal))
            {
                return refusal;
            }

            planned[index] = value;
        }

        if (planned.Count == 0)
        {
            return Refusal.Unsupported(
                "None of the meshes named a part of this model, so there was nothing to replace. "
                + $"A mesh must keep the name the export gave it, of the form {GeometryEdit.NamePrefix}N.");
        }

        // Every record's current slice, read from the bases rather than from the
        // model's identifiers. A slice is the region between one base and the
        // next, which tiles the pool by construction; the highest identifier a
        // record happens to use is a lower bound on that and not the same thing.
        // Roadmap §10.52 measured the two to agree on all 1,594 real pairs, and
        // taking the bases means a record holding a slot it does not use keeps
        // it instead of having it silently reclaimed.
        int[] current = new int[source.Constants.Length];
        for (int record = 0; record < source.Constants.Length; record++)
        {
            current[record] = Slice(source, record, c => (int)c.XyBase, source.Xy.Length);
        }

        int[] wanted = [.. current];
        int[] depths = new int[source.Constants.Length];
        for (int record = 0; record < depths.Length; record++)
        {
            depths[record] = Slice(source, record, c => (int)c.ZBase, source.Z.Length);
        }

        foreach (Plan plan in planned.Values)
        {
            wanted[plan.Index] = plan.Positions.Length;
            depths[plan.Index] = plan.Depths.Length;
        }

        // The depth index is a model-wide scale, so it is chosen once, from the
        // hungriest part, before any slice is re-based. Widening carries every
        // stored index across unchanged, so the parts nobody edited are
        // unaffected beyond costing a few more bits each.
        int deepest = 0;
        foreach (Plan plan in planned.Values)
        {
            deepest = Math.Max(deepest, plan.Depths.Length);
        }

        int chosen = Math.Max(
            source.Constants[0].ZBitWidth, Mode3Constant.NarrowestZBitWidth(deepest));
        Result<Mode3Cameldata> widened = source.Widened(chosen);
        if (!widened.TryGetValue(out Mode3Cameldata? scaled, out Refusal? widthRefusal))
        {
            return widthRefusal;
        }

        // Which parts start storing a layout they used to work out, and at what
        // scale. Left at -1 for every part nobody asked about.
        int[] carry = new int[source.Constants.Length];
        Array.Fill(carry, -1);
        foreach (Plan plan in planned.Values)
        {
            carry[plan.Index] = plan.CarryAtScale;
        }

        Result<Mode3Cameldata> rebased =
            scaled.Rebased([.. current], [.. wanted], [.. depths], [.. carry]);
        if (!rebased.TryGetValue(out Mode3Cameldata? sliced, out Refusal? rebaseRefusal))
        {
            return rebaseRefusal;
        }

        // The fourth pool, and the one whose absence shipped. A part's curved
        // coverage is a per-vertex trim of the shape its triangles describe,
        // and it is keyed to the draw vertex count rather than to the pool
        // slice — so it cannot ride along with the re-base above, which works
        // in slots. A redrawn part's new vertices have no curves of their own;
        // left alone they inherit the outline of the part they replaced, which
        // is what two probes drew in game. Roadmap §10.154.
        int[] currentVertices = new int[model.Parts.Length];
        int[] newVertices = new int[model.Parts.Length];
        bool[] neutralise = new bool[model.Parts.Length];
        for (int record = 0; record < model.Parts.Length; record++)
        {
            currentVertices[record] = (int)model.Parts[record].Descriptor.VertexCount;
            newVertices[record] = planned.TryGetValue(record, out Plan redrawn)
                ? redrawn.VertexCount
                : currentVertices[record];
            neutralise[record] = planned.ContainsKey(record);
        }

        Result<Mode3Cameldata> covered =
            sliced.WithCoverage([.. currentVertices], [.. newVertices], [.. neutralise]);
        if (!covered.TryGetValue(out Mode3Cameldata? pools, out Refusal? coverageRefusal))
        {
            return coverageRefusal;
        }

        Vector2[] xy = [.. pools.Xy];
        float[] z = [.. pools.Z];
        uint[] packed = [.. pools.PackedZ];
        uint[] uv0 = [.. pools.Uv0];
        Dictionary<int, byte[]> payloads = [];
        int triangles = 0;

        foreach (Plan plan in planned.Values)
        {
            Mode3Constant constant = pools.Constants[plan.Index];
            MmbModelPart host = model.Parts[plan.Index];

            for (int slot = 0; slot < plan.Positions.Length; slot++)
            {
                xy[(int)constant.XyBase + slot] = plan.Positions[slot];

                BitWriter writer = new(packed);
                if (!writer.TryWrite(
                        ((long)constant.XyBase + slot) * constant.ZBitWidth,
                        constant.ZBitWidth,
                        (uint)plan.DepthOfSlot[slot]))
                {
                    return Refusal.Malformed(
                        $"Part {plan.Index} writes a depth index outside the cameldata bit stream.");
                }
            }

            for (int depth = 0; depth < plan.Depths.Length; depth++)
            {
                z[(int)constant.ZBase + depth] = plan.Depths[depth];
            }

            for (int slot = 0; slot < plan.Uv0.Length; slot++)
            {
                uv0[(int)constant.Uv0Base + slot] = plan.Uv0[slot];
            }

            Result<byte[]> payload = host.Descriptor.IsIndexed
                ? MmbWriter.RebuiltIndexedPayload(
                    plan.Ids, plan.Indices, host.Descriptor.BaseBias)
                : MmbWriter.DirectPayload(plan.Ids);

            if (!payload.TryGetValue(out byte[]? bytes, out Refusal? payloadRefusal))
            {
                return payloadRefusal;
            }

            payloads[plan.Index] = bytes;
            triangles += plan.Corners / 3;
        }

        // The container is written afresh rather than patched, which is what
        // lets a payload change length: every payload offset is recomputed as
        // the file is laid out, so a part that grew pushes the rest along
        // instead of overwriting them.
        ImmutableArray<MmbModelPart>.Builder rewritten =
            ImmutableArray.CreateBuilder<MmbModelPart>(model.Parts.Length);
        for (int record = 0; record < model.Parts.Length; record++)
        {
            MmbModelPart host = model.Parts[record];
            if (!payloads.TryGetValue(record, out byte[]? bytes))
            {
                rewritten.Add(host);
                continue;
            }

            uint vertices = (uint)planned[record].VertexCount;
            MmbGeometryDescriptor descriptor = host.Descriptor with
            {
                VertexCount = vertices,
                PayloadLength = (uint)bytes.Length,

                // Word 3 is a check field that a mode-3 record with one
                // declaration must carry its vertex count in, and a stale one is
                // a file our own assembler refuses. It is updated where it
                // tracked the count and left alone where it did not, rather than
                // being overwritten on the strength of what it usually means.
                SecondaryVertexCount = host.Descriptor.SecondaryVertexCount == host.Descriptor.VertexCount
                    ? vertices
                    : host.Descriptor.SecondaryVertexCount,
            };

            // The payload is written rather than overwritten, so it states where
            // its own index buffer begins: immediately after the identifiers,
            // which is where every editable record in the corpus has it.
            descriptor = descriptor with
            {
                IndexCount = (uint)(host.Descriptor.IsIndexed ? planned[record].Indices.Length : 0),
                IndexOffset = vertices * sizeof(ushort),
            };

            // The bounding block is derived from the geometry, so a part that
            // was redrawn has to state its new volume rather than its old one.
            // A stale box is a part the game may cull while it is on screen,
            // and nothing in an offline render would show it, because an
            // offline render does not cull.
            Plan drawn = planned[record];
            Vector3[] cloud = new Vector3[drawn.Positions.Length];
            for (int slot = 0; slot < cloud.Length; slot++)
            {
                cloud[slot] = new Vector3(
                    drawn.Positions[slot].X,
                    drawn.Positions[slot].Y,
                    drawn.Depths[drawn.DepthOfSlot[slot]]);
            }

            rewritten.Add(host.WithGeometry(descriptor, bytes, MmbPartBounds.Compute(cloud)));
        }

        Result<MmbModel> replaced = model.WithParts(rewritten.MoveToImmutable());
        if (!replaced.TryGetValue(out MmbModel? edited, out Refusal? partsRefusal))
        {
            return partsRefusal;
        }

        Result<byte[]> written = MmbContainerWriter.Write(edited);
        if (!written.TryGetValue(out byte[]? mmb, out Refusal? modelRefusal))
        {
            return modelRefusal;
        }

        Result<Mode3Cameldata> positioned = pools.WithPositions([.. xy], [.. z]);
        if (!positioned.TryGetValue(out Mode3Cameldata? file, out Refusal? cameldataRefusal))
        {
            return cameldataRefusal;
        }

        Result<Mode3Cameldata> repacked = file.WithPackedZ([.. packed]);
        if (!repacked.TryGetValue(out Mode3Cameldata? withDepths, out Refusal? packedRefusal))
        {
            return packedRefusal;
        }

        int converted = 0;
        int ignored = 0;
        foreach (Plan plan in planned.Values)
        {
            if (plan.CarryAtScale >= 0)
            {
                converted++;
            }

            if (plan.LayoutIgnored)
            {
                ignored++;
            }
        }

        Result<Mode3Cameldata> painted = withDepths.WithUv0([.. uv0]);
        return painted.TryGetValue(out Mode3Cameldata? final, out Refusal? uvRefusal)
            ? Result.Ok(new GeometryReplacement(
                mmb, final, payloads.Count, triangles, converted, ignored))
            : uvRefusal;
    }

    /// <summary>What one part's replacement will need, before anything is written.</summary>
    private readonly record struct Plan(
        int Index,
        ImmutableArray<int> Ids,
        ImmutableArray<Vector2> Positions,
        ImmutableArray<float> Depths,
        ImmutableArray<int> DepthOfSlot,
        int Corners,
        int VertexCount,
        ImmutableArray<int> Indices,
        ImmutableArray<uint> Uv0,
        int CarryAtScale,
        bool LayoutIgnored);

    /// <summary>
    /// Works out one part's new geometry without writing any of it.
    /// </summary>
    /// <remarks>
    /// Planning is separate from writing because the pools have to be re-based
    /// before a single position can be stored: a part that gained points moves
    /// every later part's slice, so the address to write to is not known until
    /// every part has been asked how much room it wants.
    /// </remarks>
    private static Result<Plan> PlanOne(
        EditedPart part, MmbModelPart host, Mode3Cameldata source, int index, bool ownUv0)
    {
        MmbGeometryDescriptor descriptor = host.Descriptor;

        // Where a record's pool identifiers end. For a direct record that is the
        // whole payload; for an indexed one it is where the index buffer starts.
        // Anything between is a further stream, keyed per vertex and undecoded,
        // which rearranging the vertices would leave describing the arrangement
        // they had.
        long identifiers = (long)descriptor.VertexCount * sizeof(ushort);
        long identifiersEnd = descriptor.IsIndexed ? descriptor.IndexOffset : descriptor.PayloadLength;
        if (identifiersEnd != identifiers)
        {
            return Refusal.Unsupported(Describe(
                part.Name,
                "names a part carrying a per-vertex stream this cannot rewrite, so redrawing it would "
                + "leave that stream describing the arrangement the part used to have. It can still be "
                + "reshaped, and the model's other parts can still be redrawn"));
        }

        ImmutableArray<Vector3D> welding = descriptor.IsIndexed ? part.Positions : part.Corners();
        int corners = descriptor.IsIndexed ? part.Indices.Length : welding.Length;

        if (corners % 3 != 0)
        {
            return Refusal.Unsupported(Describe(
                part.Name,
                string.Create(CultureInfo.InvariantCulture,
                    $"draws {corners} corners, which is not a whole number of triangles")));
        }

        // An indexed part's payload is written afresh, which is what lets it be
        // resized -- so what it must not hold is a byte nothing has decoded,
        // since writing the payload means writing that byte too. The check above
        // covers anything before the index buffer; this covers anything after.
        // Measured at zero records: of the 1,595 editable indexed records in the
        // corpus, every one accounts for every byte (Roadmap §10.58). It is
        // checked rather than trusted, because a writer proceeding on a census
        // it did not verify is how a plausible broken file gets made.
        if (descriptor.IsIndexed && !descriptor.AccountsForEveryByte)
        {
            return Refusal.Unsupported(Describe(
                part.Name,
                string.Create(CultureInfo.InvariantCulture,
                    $"names an indexed part whose payload is {descriptor.PayloadLength} bytes where its identifiers and index buffer account for {descriptor.IndexOffset + (descriptor.IndexCount * sizeof(ushort))}. Rewriting it would have to invent the rest")));
        }

        Mode3Constant constant = source.Constants[index];

        // A part either works its texture layout out from position or stores
        // one. Storing used to refuse, because new geometry would have kept the
        // old layout and been painted wrongly -- there were none to write. A GLB
        // brings one, so the refusal now applies only when the mesh brought
        // none, which is the case that would still be wrong.
        //
        // Storing is the side an imported mesh usually wants to be on: working
        // it out is a planar projection, right for the flat cut-outs all the
        // shipped art is and wrong for anything with sides, where one image is
        // smeared down all of them.
        //
        // Which side a part is on is decided by whichever shipped part it was
        // based on, and 86% work it out -- so an author who modelled something
        // solid lands on the wrong side six times in seven. ownUv0 is what moves
        // it, and a part left on the wrong side is counted rather than passed
        // over in silence.
        bool carries = constant.UsesUnifiedUv0;
        int carryAtScale = -1;
        bool layoutIgnored = false;

        if (!carries && !part.Uv0.IsDefaultOrEmpty)
        {
            if (ownUv0)
            {
                Result<int> scale = Uv0Projection.ScaleFor(part.Uv0);
                if (!scale.TryGetValue(out carryAtScale, out Refusal? scaleRefusal))
                {
                    return scaleRefusal;
                }

                carries = true;
            }
            else
            {
                layoutIgnored = true;
            }
        }

        if (constant.UsesUnifiedUv0 && part.Uv0.IsDefaultOrEmpty)
        {
            return Refusal.Unsupported(Describe(
                part.Name,
                "names a part that carries its own texture coordinates, and the mesh brought none, "
                + "so new geometry would keep the old ones and be painted wrongly. Export it with "
                + "texture coordinates, or reshape the part instead of redrawing it"));
        }

        if (carries && part.Uv0.Length != part.Positions.Length)
        {
            return Refusal.Unsupported(Describe(
                part.Name,
                string.Create(CultureInfo.InvariantCulture,
                    $"carries {part.Uv0.Length} texture coordinates for {part.Positions.Length} points, and a part that stores its own needs one for each")));
        }

        // The depth pool is re-based alongside the others, and the index that
        // reads it is widened to fit, so neither the part's own slice nor the
        // width it arrived with caps this. What is left is the widest index the
        // field can spell, which no mesh reaches: a part would need more than
        // four billion distinct depths, having at most one per vertex.
        long addressable = 1L << 32;

        Dictionary<(float X, float Y, float Z), int> seen = [];
        List<float> distinctDepths = [];
        Dictionary<float, int> depthOf = [];
        List<Vector2> positions = [];
        List<int> depthOfSlot = [];
        List<uint> packedUv0 = [];
        int[] ids = new int[welding.Length];

        // A carried coordinate is looked up by the identifier a position is, so
        // it is welded alongside the position rather than separately. Two
        // vertices sharing a position must therefore share a coordinate; if they
        // do not, the part cannot store both and says so rather than keeping
        // whichever came first.
        bool carriesUv0 = carries;
        int packScale = carryAtScale >= 0 ? carryAtScale : constant.Uv0ScaleIndex;
        ImmutableArray<Vector2D> uvOf = carriesUv0
            ? (descriptor.IsIndexed ? part.Uv0 : CornerUv0(part))
            : [];

        for (int vertex = 0; vertex < welding.Length; vertex++)
        {
            Vector3D position = welding[vertex];
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
            {
                return Refusal.Malformed(Describe(part.Name, $"has a position at vertex {vertex} that is not finite"));
            }

            (float x, float y, float depth) = ((float)position.X, (float)position.Y, (float)position.Z);

            if (!depthOf.TryGetValue(depth, out int depthIndex))
            {
                if (distinctDepths.Count >= addressable)
                {
                    return Refusal.Unsupported(Describe(
                        part.Name,
                        string.Create(CultureInfo.InvariantCulture,
                            $"needs more than {addressable} distinct depth(s), which is more than the widest depth index the format can spell")));
                }

                depthIndex = distinctDepths.Count;
                depthOf[depth] = depthIndex;
                distinctDepths.Add(depth);
            }

            // Welded in order of first appearance, so a mesh whose vertices
            // already share positions keeps sharing them.
            if (!seen.TryGetValue((x, y, depth), out int id))
            {
                id = seen.Count;
                seen[(x, y, depth)] = id;
                positions.Add(new Vector2(x, y));
                depthOfSlot.Add(depthIndex);

                if (carriesUv0)
                {
                    Result<uint> word = Uv0Projection.Pack(uvOf[vertex], packScale);
                    if (!word.TryGetValue(out uint packedWord, out Refusal? uvRefusal))
                    {
                        return uvRefusal;
                    }

                    packedUv0.Add(packedWord);
                }

                // A pool identifier is stored as a u16, which is the one cap a
                // re-base cannot lift.
                if (id > ushort.MaxValue)
                {
                    return Refusal.Unsupported(Describe(
                        part.Name,
                        string.Create(CultureInfo.InvariantCulture,
                            $"has more than {ushort.MaxValue + 1} distinct points, and a point is named by a sixteen-bit identifier")));
                }
            }

            else if (carriesUv0)
            {
                // A stored coordinate belongs to the *position*, not the vertex,
                // so two vertices welded onto one point cannot hold two of them.
                // Keeping whichever came first is how a cube silently loses five
                // of its six faces: every corner is shared by three faces, each
                // wanting a different part of the image.
                Result<uint> word = Uv0Projection.Pack(uvOf[vertex], packScale);
                if (!word.TryGetValue(out uint packedWord, out Refusal? uvRefusal))
                {
                    return uvRefusal;
                }

                if (packedUv0[id] != packedWord)
                {
                    return Refusal.Unsupported(Describe(
                        part.Name,
                        string.Create(CultureInfo.InvariantCulture,
                            $"has two points at the same place wanting different bits of the image, at vertex {vertex}. A stored texture layout belongs to the position rather than to the vertex, so a shape whose faces meet at a shared corner cannot keep both. Separate the faces slightly, or split the corners in your 3D package")));
                }
            }

            ids[vertex] = id;
        }

        // One identifier per vertex, which is what the payload stores and what
        // the index buffer counts from -- not one per distinct position, which is
        // the pool slice and a different number wherever a mesh repeats a point.
        // This used to keep the record's original count for an indexed part,
        // because the payload was overwritten in place and its spare identifiers
        // stayed as they were. A written payload has exactly as many as it says.
        int vertexCount = ids.Length;
        return Result.Ok(new Plan(
            index, [.. ids], [.. positions], [.. distinctDepths], [.. depthOfSlot],
            corners, vertexCount, part.Indices, [.. packedUv0], carryAtScale, layoutIgnored));
    }

    private static int Slice(Mode3Cameldata source, int index, Func<Mode3Constant, int> baseOf, int total)
    {
        int start = baseOf(source.Constants[index]);
        int end = index + 1 < source.Constants.Length ? baseOf(source.Constants[index + 1]) : total;
        return Math.Max(end - start, 0);
    }

    /// <summary>
    /// A direct part's coordinates resolved through the indices, so they line up
    /// with <see cref="EditedPart.Corners"/> rather than with the vertex list.
    /// </summary>
    private static ImmutableArray<Vector2D> CornerUv0(EditedPart part)
    {
        if (part.Indices.IsDefaultOrEmpty)
        {
            return part.Uv0;
        }

        ImmutableArray<Vector2D>.Builder corners =
            ImmutableArray.CreateBuilder<Vector2D>(part.Indices.Length);
        foreach (int index in part.Indices)
        {
            corners.Add(part.Uv0[index]);
        }

        return corners.MoveToImmutable();
    }

    private static string Describe(string name, string what) => $"The mesh '{name}' {what}.";
}

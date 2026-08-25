using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Perianth.Core.Geometry;
using Perianth.Formats.Diagnostics;

namespace Perianth.Gltf;

/// <summary>One mesh a GLB carried.</summary>
/// <param name="Name">The mesh's own name, which is how a caller identifies it.</param>
/// <param name="Positions">Its <c>POSITION</c> values, in the file's own vertex order.</param>
/// <param name="Indices">
/// Which vertex each triangle corner uses, or empty when the file states none.
/// </param>
/// <remarks>
/// Both are kept because the two things import does with a mesh want different
/// ones, and collapsing them into one was a mistake worth not repeating.
/// <para>
/// Reshaping a part matches vertex to pool slot by position in the list, so it
/// wants the vertices as the file orders them. Replacing a part's geometry cares
/// only about which corners make which triangle, so it wants
/// <see cref="Corners"/> — and for an indexed mesh those are different lengths,
/// which is how the confusion announced itself.
/// </para>
/// </remarks>
/// <param name="PoolSlots">
/// Which source pool entry each vertex reads, when the file states it.
/// </param>
/// <param name="Uv0">
/// Its <c>TEXCOORD_0</c> values, in the same order as <see cref="Positions"/>,
/// or empty where the file states none for every primitive.
/// </param>
/// <remarks>
/// The texture coordinates matter only for the parts that carry their own
/// rather than computing them from position — 14% of the corpus, and the ones an
/// imported mesh most wants to be, since a computed UV0 is a planar projection
/// that smears anything three-dimensional.
/// </remarks>
public sealed record GlbMesh(
    string Name,
    ImmutableArray<Vector3D> Positions,
    ImmutableArray<int> Indices,
    ImmutableArray<int> PoolSlots,
    ImmutableArray<Vector2D> Uv0 = default)
{
    /// <summary>The triangle corners in the order they are drawn.</summary>
    /// <remarks>
    /// The same as <see cref="Positions"/> for a mesh this tool exported from a
    /// direct record, whose indices are <c>0..n-1</c>. It differs the moment
    /// anything re-indexes the mesh, which is the point: following the indices
    /// means a Blender that reorders vertices moves nothing.
    /// </remarks>
    public ImmutableArray<Vector3D> Corners() =>
        Indices.IsDefaultOrEmpty ? Positions : [.. Indices.Select(i => Positions[i])];
}

/// <summary>
/// Reads the vertex positions out of a GLB.
/// </summary>
/// <remarks>
/// <para>
/// The import half of <see cref="GlbWriter"/>, and deliberately far smaller than
/// it. Import needs two things from a GLB — which mesh a set of positions belongs
/// to, and what those positions are — so that is all this reads. Materials,
/// images, animations, skins, cameras and every extension are ignored rather than
/// refused: a file carrying them is not malformed, it simply carries more than
/// this question needs.
/// </para>
/// <para>
/// It returns <see cref="Vector3D"/> and mesh names, not glTF terms, so the
/// caller never learns what a bufferView is. That is the same boundary the writer
/// keeps from the other side, and it is why the reader lives here rather than in
/// <c>Core</c>.
/// </para>
/// <para>
/// What it will not do is guess. A file whose accessor is the wrong type, whose
/// bufferView runs past the buffer, or which stores positions in a way this does
/// not implement, refuses and says which mesh. The alternative — skipping the
/// mesh, or filling in zeroes — would produce an import that looks like it worked
/// and moved a part to the origin.
/// </para>
/// </remarks>
public static class GlbReader
{
    private const uint Magic = 0x4654_6C67;          // "glTF", little-endian
    private const uint JsonChunkType = 0x4E4F_534A;  // "JSON"
    private const uint BinChunkType = 0x004E_4942;   // "BIN\0"
    private const int HeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const int ComponentTypeFloat = 5126;
    private const int ComponentTypeUnsignedByte = 5121;
    private const int ComponentTypeUnsignedShort = 5123;
    private const int ComponentTypeUnsignedInt = 5125;
    private const int PositionComponents = 3;
    private const int PositionStride = PositionComponents * sizeof(float);

    /// <summary>Reads every mesh that carries positions.</summary>
    /// <remarks>
    /// A mesh with several primitives contributes each of them in order, because
    /// a caller matching by name wants the whole mesh's vertices; the exporter
    /// writes one primitive per mesh, and a file that does not is still answered
    /// rather than refused.
    /// </remarks>
    public static Result<ImmutableArray<GlbMesh>> Read(ReadOnlyMemory<byte> file)
    {
        Result<Chunks> chunks = Split(file);
        if (!chunks.TryGetValue(out Chunks parts, out Refusal? refusal))
        {
            return refusal;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(parts.Json);
        }
        catch (JsonException error)
        {
            return Refusal.Malformed($"The GLB's JSON chunk is not valid JSON: {error.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return Refusal.Malformed("The GLB's JSON chunk is not an object.");
            }

            if (!root.TryGetProperty("meshes", out JsonElement meshes) ||
                meshes.ValueKind is not JsonValueKind.Array)
            {
                return Refusal.Unsupported("The GLB declares no meshes, so it carries no geometry to import.");
            }

            ImmutableArray<GlbMesh>.Builder built = ImmutableArray.CreateBuilder<GlbMesh>();
            int ordinal = 0;

            foreach (JsonElement mesh in meshes.EnumerateArray())
            {
                string name = mesh.TryGetProperty("name", out JsonElement named) &&
                    named.ValueKind is JsonValueKind.String
                        ? named.GetString() ?? string.Empty
                        : string.Empty;

                Result<GlbMesh> one = OneMesh(root, mesh, parts.Binary, name, ordinal);
                if (!one.TryGetValue(out GlbMesh? carried, out Refusal? meshRefusal))
                {
                    return meshRefusal;
                }

                if (carried.Positions.Length > 0)
                {
                    built.Add(carried);
                }

                ordinal++;
            }

            return Result.Ok(built.ToImmutable());
        }
    }

    private static Result<GlbMesh> OneMesh(
        JsonElement root, JsonElement mesh, ReadOnlyMemory<byte> binary, string name, int ordinal)
    {
        if (!mesh.TryGetProperty("primitives", out JsonElement primitives) ||
            primitives.ValueKind is not JsonValueKind.Array)
        {
            return Result.Ok(new GlbMesh(name, [], [], [], []));
        }

        ImmutableArray<int>.Builder order = ImmutableArray.CreateBuilder<int>();
        ImmutableArray<int>.Builder slots = ImmutableArray.CreateBuilder<int>();
        bool everySlotStated = true;

        ImmutableArray<Vector2D>.Builder uv0 = ImmutableArray.CreateBuilder<Vector2D>();
        bool everyUvStated = true;

        ImmutableArray<Vector3D>.Builder values = ImmutableArray.CreateBuilder<Vector3D>();
        foreach (JsonElement primitive in primitives.EnumerateArray())
        {
            if (!primitive.TryGetProperty("attributes", out JsonElement attributes) ||
                !attributes.TryGetProperty("POSITION", out JsonElement position) ||
                !position.TryGetInt32(out int accessorIndex))
            {
                continue;
            }

            Result<ImmutableArray<Vector3D>> read = Accessor(root, accessorIndex, binary, name, ordinal);
            if (!read.TryGetValue(out ImmutableArray<Vector3D> some, out Refusal? refusal))
            {
                return refusal;
            }

            // Resolved through the index buffer rather than taken in vertex
            // order, so what comes back is the sequence of triangle corners the
            // file actually describes.
            //
            // For an export of this tool's own the two are the same -- a direct
            // record's indices are 0..n-1 -- so nothing changes for the ordinary
            // case, and the identity round trip says so. What it buys is that a
            // Blender which reorders or re-welds vertices on the way out no
            // longer moves geometry into the wrong slots without saying anything.
            // Which pool entry each vertex reads, when this tool wrote the file
            // and whatever edited it kept the attribute. One primitive missing it
            // discards the lot: a partial mapping is worse than none, because the
            // half that has it would look authoritative.
            if (attributes.TryGetProperty("_POOLSLOT", out JsonElement carriedSlots) &&
                carriedSlots.TryGetInt32(out int slotAccessor))
            {
                Result<ImmutableArray<Vector3D>> slotValues = Accessor(root, slotAccessor, binary, name, ordinal, components: 1);
                if (!slotValues.TryGetValue(out ImmutableArray<Vector3D> asFloats, out Refusal? slotRefusal))
                {
                    return slotRefusal;
                }

                foreach (Vector3D slot in asFloats)
                {
                    slots.Add((int)slot.X);
                }
            }
            else
            {
                everySlotStated = false;
            }

            // The same all-or-nothing rule the pool slots use, for the same
            // reason: a mesh where half the primitives carry coordinates and
            // half do not would look as though it stated them.
            if (attributes.TryGetProperty("TEXCOORD_0", out JsonElement carriedUv) &&
                carriedUv.TryGetInt32(out int uvAccessor))
            {
                Result<ImmutableArray<Vector3D>> uvValues =
                    Accessor(root, uvAccessor, binary, name, ordinal, components: 2);
                if (!uvValues.TryGetValue(out ImmutableArray<Vector3D> asPairs, out Refusal? uvRefusal))
                {
                    return uvRefusal;
                }

                foreach (Vector3D pair in asPairs)
                {
                    uv0.Add(new Vector2D(pair.X, pair.Y));
                }
            }
            else
            {
                everyUvStated = false;
            }

            // Offset by what earlier primitives contributed, so a mesh with
            // several of them keeps one consistent numbering.
            int firstVertex = values.Count;
            values.AddRange(some);

            if (primitive.TryGetProperty("indices", out JsonElement indexed) &&
                indexed.TryGetInt32(out int indexAccessor))
            {
                Result<ImmutableArray<int>> statedOrder = Indices(root, indexAccessor, binary, name, ordinal, some.Length);
                if (!statedOrder.TryGetValue(out ImmutableArray<int> indices, out Refusal? orderRefusal))
                {
                    return orderRefusal;
                }

                foreach (int index in indices)
                {
                    order.Add(firstVertex + index);
                }
            }
            else
            {
                for (int i = 0; i < some.Length; i++)
                {
                    order.Add(firstVertex + i);
                }
            }
        }

        ImmutableArray<int> mapping = everySlotStated && slots.Count == values.Count
            ? slots.ToImmutable()
            : [];

        ImmutableArray<Vector2D> coordinates = everyUvStated && uv0.Count == values.Count
            ? uv0.ToImmutable()
            : [];

        return Result.Ok(new GlbMesh(
            name, values.ToImmutable(), order.ToImmutable(), mapping, coordinates));
    }

    /// <summary>The triangle corners a primitive draws, in order.</summary>
    private static Result<ImmutableArray<int>> Indices(
        JsonElement root, int index, ReadOnlyMemory<byte> binary, string name, int ordinal, int vertices)
    {
        if (!TryElement(root, "accessors", index, out JsonElement accessor))
        {
            return Malformed(name, ordinal, $"names index accessor {index}, which the file does not have");
        }

        if (!accessor.TryGetProperty("componentType", out JsonElement componentType) ||
            !componentType.TryGetInt32(out int component) ||
            component is not (ComponentTypeUnsignedByte or ComponentTypeUnsignedShort or ComponentTypeUnsignedInt))
        {
            return Unsupported(name, ordinal, "stores indices in a component type glTF does not allow for them");
        }

        if (accessor.TryGetProperty("sparse", out _))
        {
            return Unsupported(name, ordinal, "uses a sparse index accessor, which this reader does not implement");
        }

        if (!accessor.TryGetProperty("count", out JsonElement countElement) ||
            !countElement.TryGetInt32(out int count) || count < 0)
        {
            return Malformed(name, ordinal, "has an index accessor with no usable count");
        }

        if (!accessor.TryGetProperty("bufferView", out JsonElement viewElement) ||
            !viewElement.TryGetInt32(out int viewIndex) ||
            !TryElement(root, "bufferViews", viewIndex, out JsonElement view))
        {
            return Malformed(name, ordinal, "has indices backed by no bufferView");
        }

        int size = component switch
        {
            ComponentTypeUnsignedByte => 1,
            ComponentTypeUnsignedShort => 2,
            _ => 4,
        };

        int stride = OptionalInt(view, "byteStride");
        if (stride == 0)
        {
            stride = size;
        }

        long start = (long)OptionalInt(view, "byteOffset") + OptionalInt(accessor, "byteOffset");
        long needed = count == 0 ? 0 : ((long)(count - 1) * stride) + size;
        if (start < 0 || start + needed > binary.Length)
        {
            return Malformed(name, ordinal, "has indices that run past the end of the binary chunk");
        }

        ReadOnlySpan<byte> span = binary.Span;
        ImmutableArray<int>.Builder indices = ImmutableArray.CreateBuilder<int>(count);
        for (int i = 0; i < count; i++)
        {
            int at = (int)start + (i * stride);
            int value = size switch
            {
                1 => span[at],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(span[at..]),
                _ => (int)BinaryPrimitives.ReadUInt32LittleEndian(span[at..]),
            };

            // Checked rather than trusted: an index past the end would otherwise
            // throw where a refusal belongs, and one that is merely wrong would
            // silently duplicate a vertex.
            if (value < 0 || value >= vertices)
            {
                return Malformed(name, ordinal, $"has an index at position {i} naming vertex {value} of {vertices}");
            }

            indices.Add(value);
        }

        return Result.Ok(indices.MoveToImmutable());
    }

    private static Result<ImmutableArray<Vector3D>> Accessor(
        JsonElement root, int index, ReadOnlyMemory<byte> binary, string name, int ordinal, int components = PositionComponents)
    {
        if (!TryElement(root, "accessors", index, out JsonElement accessor))
        {
            return Malformed(name, ordinal, $"names accessor {index}, which the file does not have");
        }

        if (!accessor.TryGetProperty("componentType", out JsonElement componentType) ||
            !componentType.TryGetInt32(out int component) || component != ComponentTypeFloat)
        {
            return Unsupported(name, ordinal, "stores positions in a component type other than float");
        }

        string wanted = components switch { 1 => "SCALAR", 2 => "VEC2", _ => "VEC3" };
        if (!accessor.TryGetProperty("type", out JsonElement type) ||
            !string.Equals(type.GetString(), wanted, StringComparison.Ordinal))
        {
            return Unsupported(name, ordinal, $"stores a {wanted} attribute in another type");
        }

        // Sparse accessors substitute values at named indices. Nothing writes one
        // here, and reading the dense part while ignoring the substitutions would
        // import positions the file does not describe.
        if (accessor.TryGetProperty("sparse", out _))
        {
            return Unsupported(name, ordinal, "uses a sparse accessor, which this reader does not implement");
        }

        if (!accessor.TryGetProperty("count", out JsonElement countElement) ||
            !countElement.TryGetInt32(out int count) || count < 0)
        {
            return Malformed(name, ordinal, "has an accessor with no usable count");
        }

        // An accessor with no bufferView is defined to be all zeroes. That is a
        // legal file and a meaningless import, so it refuses rather than moving
        // every vertex of the part to the origin.
        if (!accessor.TryGetProperty("bufferView", out JsonElement viewElement) ||
            !viewElement.TryGetInt32(out int viewIndex))
        {
            return Unsupported(name, ordinal, "has positions backed by no bufferView, which would import as zeroes");
        }

        if (!TryElement(root, "bufferViews", viewIndex, out JsonElement view))
        {
            return Malformed(name, ordinal, $"names bufferView {viewIndex}, which the file does not have");
        }

        int viewOffset = OptionalInt(view, "byteOffset");
        int viewLength = OptionalInt(view, "byteLength");
        int accessorOffset = OptionalInt(accessor, "byteOffset");
        int width = components * sizeof(float);
        int stride = OptionalInt(view, "byteStride");
        if (stride == 0)
        {
            stride = width;
        }

        if (stride < width)
        {
            return Malformed(name, ordinal, $"has a byteStride of {stride}, which is shorter than the attribute it carries");
        }

        if (viewOffset < 0 || accessorOffset < 0 || viewLength < 0)
        {
            return Malformed(name, ordinal, "has a negative offset or length");
        }

        // Checked before anything is allocated, and against the bufferView as
        // well as the buffer: an accessor reaching past its view is a different
        // fault from one reaching past the file, and both produce silence if the
        // read is simply clamped.
        long start = (long)viewOffset + accessorOffset;
        long needed = count == 0 ? 0 : ((long)(count - 1) * stride) + width;
        if (start + needed > binary.Length || (long)accessorOffset + needed > viewLength)
        {
            return Malformed(name, ordinal, "has positions that run past the end of their bufferView or the binary chunk");
        }

        ReadOnlySpan<byte> span = binary.Span;
        ImmutableArray<Vector3D>.Builder positions = ImmutableArray.CreateBuilder<Vector3D>(count);
        for (int i = 0; i < count; i++)
        {
            int at = (int)start + (i * stride);
            float x = BinaryPrimitives.ReadSingleLittleEndian(span[at..]);
            float y = components > 1 ? BinaryPrimitives.ReadSingleLittleEndian(span[(at + sizeof(float))..]) : 0f;
            float z = components > 2 ? BinaryPrimitives.ReadSingleLittleEndian(span[(at + (2 * sizeof(float)))..]) : 0f;

            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                return Malformed(name, ordinal, $"has a position at vertex {i} that is not finite");
            }

            positions.Add(new Vector3D(x, y, z));
        }

        return Result.Ok(positions.MoveToImmutable());
    }

    private static Result<Chunks> Split(ReadOnlyMemory<byte> file)
    {
        ReadOnlySpan<byte> span = file.Span;
        if (span.Length < HeaderLength)
        {
            return Refusal.Malformed("The file is too short to be a GLB.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
        {
            return Refusal.Malformed("The file does not begin with the glTF magic, so it is not a GLB.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (version != 2)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"The GLB declares version {version}, and only 2 is implemented."));
        }

        ReadOnlyMemory<byte> json = default;
        ReadOnlyMemory<byte> binary = default;
        int at = HeaderLength;

        while (at + ChunkHeaderLength <= span.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 4)..]);
            long end = (long)at + ChunkHeaderLength + length;
            if (end > span.Length)
            {
                return Refusal.Malformed("A GLB chunk declares a length that runs past the end of the file.");
            }

            ReadOnlyMemory<byte> payload = file.Slice(at + ChunkHeaderLength, (int)length);

            // First of each kind wins, which is what the specification says and
            // is also the safe reading: a second JSON chunk is not a correction.
            if (kind == JsonChunkType && json.IsEmpty)
            {
                json = payload;
            }
            else if (kind == BinChunkType && binary.IsEmpty)
            {
                binary = payload;
            }

            at = (int)end;
        }

        return json.IsEmpty
            ? Refusal.Malformed("The GLB carries no JSON chunk.")
            : Result.Ok(new Chunks(json, binary));
    }

    private static bool TryElement(JsonElement root, string array, int index, out JsonElement element)
    {
        element = default;
        if (index < 0 ||
            !root.TryGetProperty(array, out JsonElement all) ||
            all.ValueKind is not JsonValueKind.Array ||
            index >= all.GetArrayLength())
        {
            return false;
        }

        element = all[index];
        return true;
    }

    private static int OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : 0;

    private static Refusal Malformed(string name, int ordinal, string what) =>
        Refusal.Malformed(Describe(name, ordinal, what));

    private static Refusal Unsupported(string name, int ordinal, string what) =>
        Refusal.Unsupported(Describe(name, ordinal, what));

    /// <summary>
    /// Names the mesh rather than the accessor, because that is what the author
    /// can find: an unnamed mesh is identified by its position in the file.
    /// </summary>
    private static string Describe(string name, int ordinal, string what) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"GLB mesh {(name.Length > 0 ? $"'{name}'" : $"at index {ordinal}")} {what}.");

    private readonly record struct Chunks(ReadOnlyMemory<byte> Json, ReadOnlyMemory<byte> Binary);
}

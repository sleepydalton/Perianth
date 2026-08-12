using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Perianth.Core.Geometry;
using Perianth.Core.Imaging;
using Perianth.Core.Materials;
using Perianth.Core.Pose;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;

namespace Perianth.Gltf;

/// <summary>
/// How to project a model into GLB.
/// </summary>
/// <remarks>
/// A reference type rather than a struct, because these defaults have to hold.
/// A record struct's <c>default</c> skips its primary constructor, which would
/// hand back a null scene name and quietly drop the presentation basis — an
/// output that is wrong in two ways and looks deliberate.
/// </remarks>
public sealed record GlbWriteOptions
{
    /// <summary>
    /// Whether to append the source-to-glTF presentation root. False corresponds
    /// to exporting in source space.
    /// </summary>
    public bool IncludePresentationBasis { get; init; } = true;

    /// <summary>
    /// The scene's name, which records whether the file is a pose or a part list.
    /// </summary>
    public string SceneName { get; init; } = GlbNames.UnposedScene;

    /// <summary>
    /// The posed node hierarchy, or null for the flat unposed part list.
    /// </summary>
    /// <remarks>
    /// When present, the nodes and roots come from it and each mesh hangs beneath
    /// its setup node; the model still supplies the meshes and accessors, in the
    /// same order the graph's mesh indices assume.
    /// </remarks>
    public SceneGraph? SceneGraph { get; init; }

    /// <summary>
    /// The animations to attach, empty for a still. Each one's tracks address the
    /// nodes of <see cref="SceneGraph"/>.
    /// </summary>
    /// <remarks>
    /// glTF's <c>animations</c> is an array and Blender imports each entry as its
    /// own Action, so several need no extension — only that each carries a name
    /// worth picking from a list. They are written in the order given, and each
    /// gets its own time accessor followed by its own outputs, so one animation
    /// lays out exactly as it did when only one was possible.
    /// </remarks>
    public ImmutableArray<Animation> Animations { get; init; } = [];
}

/// <summary>
/// Writes a deterministic GLB 2.0 file.
/// </summary>
/// <remarks>
/// <para>
/// This is the only project that knows what glTF is. Nothing upstream carries a
/// glTF enum, a channel path or a presentation flag, which is what keeps a
/// future importer from needing a second decode path.
/// </para>
/// <para>
/// The JSON is written with <see cref="Utf8JsonWriter"/> rather than serialized
/// from objects. Determinism is the product here, and writing the document
/// explicitly makes property order and omitted-when-identity fields visible
/// decisions instead of consequences of a serializer's configuration.
/// </para>
/// </remarks>
public static class GlbWriter
{
    private const uint Magic = 0x4654_6C67;          // "glTF", little-endian
    private const uint JsonChunkType = 0x4E4F_534A;  // "JSON"
    private const uint BinChunkType = 0x004E_4942;   // "BIN\0"
    private const uint Version = 2;
    private const int HeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const int Alignment = 4;

    private const int ComponentTypeFloat = 5126;
    private const int ComponentTypeUnsignedInt = 5125;
    private const int TargetArrayBuffer = 34962;
    private const int TargetElementArrayBuffer = 34963;

    private const int WrapRepeat = 10497;
    private const int WrapClampToEdge = 33071;
    private const int FilterLinear = 9729;
    private const int FilterLinearMipmapLinear = 9987;

    private const string TextureTransformExtension = "KHR_texture_transform";

    /// <summary>Projects an untextured <paramref name="model"/> into GLB bytes.</summary>
    public static Result<byte[]> Write(GeometryModel model, GlbWriteOptions options) =>
        Write(model, MaterialSet.Empty, options);

    /// <summary>Projects <paramref name="model"/> and its materials into GLB bytes.</summary>
    public static Result<byte[]> Write(GeometryModel model, MaterialSet materials, GlbWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(options);

        Result<Layout> layout = Plan(model, materials, options.Animations);
        if (!layout.IsSuccess)
        {
            return layout.Refusal;
        }

        byte[] bin = new byte[layout.Value.BinLength];
        Result<byte[]> packed = Pack(model, materials, layout.Value, bin);
        if (!packed.IsSuccess)
        {
            return packed.Refusal;
        }

        byte[] json = BuildJson(model, materials, layout.Value, options);
        return Assemble(json, bin);
    }

    /// <summary>
    /// Works out every accessor and buffer view before a byte is written, so a
    /// file too large to describe is refused rather than half built.
    /// </summary>
    private static Result<Layout> Plan(GeometryModel model, MaterialSet materials, ImmutableArray<Animation> animations)
    {
        List<AccessorPlan> accessors = [];
        List<int> firstAccessorOfPart = [];
        List<ImagePlan> images = [];
        List<AnimAccessor> animAccessors = [];
        List<int> firstAccessorOfAnimation = [];
        long offset = 0;

        // Images are laid down first, matching the reference, and are the only
        // views that can need padding: a PNG is any length, while every element
        // of every accessor is four bytes wide.
        foreach (TextureImage image in materials.Images)
        {
            images.Add(new ImagePlan(offset, image.Png.Length));

            // A view must start on a four-byte boundary, so the next one begins
            // at the aligned end of this one and the gap stays zero-filled.
            offset += image.Png.Length;
            offset += Padding(image.Png.Length);

            if (offset > uint.MaxValue)
            {
                return TooLarge();
            }
        }

        foreach (GeometryPart part in model.Parts)
        {
            firstAccessorOfPart.Add(accessors.Count);
            // Attribute order is POSITION, NORMAL, then TEXCOORD_0 where the
            // part has one; the index accessor follows its own primitive.
            accessors.Add(new AccessorPlan(AccessorKind.Position, part, offset, part.Positions.Length * 3 * sizeof(float)));
            offset += accessors[^1].ByteLength;

            accessors.Add(new AccessorPlan(AccessorKind.Normal, part, offset, part.Normals.Length * 3 * sizeof(float)));
            offset += accessors[^1].ByteLength;

            if (part.HasUv0)
            {
                accessors.Add(new AccessorPlan(AccessorKind.TexCoord0, part, offset, part.Uv0.Length * 2 * sizeof(float)));
                offset += accessors[^1].ByteLength;
            }

            accessors.Add(new AccessorPlan(AccessorKind.Indices, part, offset, part.Indices.Length * sizeof(uint)));
            offset += accessors[^1].ByteLength;

            if (offset > uint.MaxValue)
            {
                return TooLarge();
            }
        }

        // Each animation's time and output buffers follow the geometry, still four
        // bytes wide, so they abut without padding. The time accessor carries its
        // bounds; the outputs need none. Several animations simply repeat the
        // block, each recording where its own accessors begin.
        foreach (Animation clip in animations)
        {
            firstAccessorOfAnimation.Add(accessors.Count + animAccessors.Count);
            float[] times = [.. clip.Times];
            float min = times.Length == 0 ? 0f : times[0];
            float max = times.Length == 0 ? 0f : times[0];
            foreach (float time in times)
            {
                min = Math.Min(min, time);
                max = Math.Max(max, time);
            }

            animAccessors.Add(new AnimAccessor(offset, times.Length, 1, times, HasBounds: true, min, max));
            offset += (long)times.Length * sizeof(float);
            if (offset > uint.MaxValue)
            {
                return TooLarge();
            }

            foreach (AnimationTrack track in clip.Tracks)
            {
                float[] values = new float[track.Values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = (float)track.Values[i];
                }

                animAccessors.Add(new AnimAccessor(offset, track.Count, track.Width, values, HasBounds: false, 0f, 0f));
                offset += (long)values.Length * sizeof(float);
                if (offset > uint.MaxValue)
                {
                    return TooLarge();
                }
            }
        }

        // Every accessor element is four bytes wide, so accessors abut exactly
        // once the image block before them has been aligned.
        return Result.Ok(new Layout(accessors, firstAccessorOfPart, images, animAccessors, firstAccessorOfAnimation, (int)offset));
    }

    private static Result<byte[]> Pack(GeometryModel model, MaterialSet materials, Layout layout, byte[] bin)
    {
        for (int i = 0; i < layout.Images.Count; i++)
        {
            materials.Images[i].Png.CopyTo(bin, layout.Images[i].ByteOffset);
        }

        foreach (AccessorPlan plan in layout.Accessors)
        {
            Span<byte> target = bin.AsSpan(plan.ByteOffset, plan.ByteLength);
            GeometryPart part = plan.Part;

            switch (plan.Kind)
            {
                case AccessorKind.Position:
                    if (!WriteVector3(target, part.Positions))
                    {
                        return NotFinite(part, "position");
                    }

                    break;

                case AccessorKind.Normal:
                    if (!WriteVector3(target, part.Normals))
                    {
                        return NotFinite(part, "normal");
                    }

                    break;

                case AccessorKind.TexCoord0:
                    for (int i = 0; i < part.Uv0.Length; i++)
                    {
                        // Section 7.4: binary64 throughout the core, narrowing to
                        // binary32 exactly here, round to nearest ties to even,
                        // which is what the conversion already does.
                        float u = (float)part.Uv0[i].X;
                        float v = (float)part.Uv0[i].Y;
                        if (!float.IsFinite(u) || !float.IsFinite(v))
                        {
                            return NotFinite(part, "texture coordinate");
                        }

                        BinaryPrimitives.WriteSingleLittleEndian(target[(i * 8)..], u);
                        BinaryPrimitives.WriteSingleLittleEndian(target[((i * 8) + 4)..], v);
                    }

                    break;

                case AccessorKind.Indices:
                    for (int i = 0; i < part.Indices.Length; i++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(target[(i * 4)..], (uint)part.Indices[i]);
                    }

                    break;

                default:
                    throw new InvalidOperationException("Unknown accessor kind.");
            }
        }

        foreach (AnimAccessor anim in layout.AnimAccessors)
        {
            for (int i = 0; i < anim.Floats.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(anim.ByteOffset + (i * sizeof(float))), anim.Floats[i]);
            }
        }

        return Result.Ok(bin);
    }

    private static bool WriteVector3(Span<byte> target, ImmutableArray<Vector3D> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            // Section 7.4: the core works in binary64 and this is the one place
            // it narrows, by round to nearest ties to even, which is what the
            // conversion already does.
            Span<float> components = [(float)values[i].X, (float)values[i].Y, (float)values[i].Z];
            for (int axis = 0; axis < 3; axis++)
            {
                if (!float.IsFinite(components[axis]))
                {
                    return false;
                }

                BinaryPrimitives.WriteSingleLittleEndian(target[((i * 12) + (axis * 4))..], components[axis]);
            }
        }

        return true;
    }

    private static byte[] BuildJson(GeometryModel model, MaterialSet materials, Layout layout, GlbWriteOptions options)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("asset");
            writer.WriteString("version", "2.0");
            writer.WriteString("generator", GlbNames.Generator);
            writer.WriteEndObject();

            writer.WriteNumber("scene", 0);
            (List<EmittedNode> nodes, List<int> roots) = BuildNodes(model, options);

            // Every assembled Camel plane is emitted double-sided, which glTF
            // carries on the material. Not only a posed one: the presentation
            // basis mirrors X, so a negative determinant reverses effective
            // winding for every export, and a prop with no setup ANIM in the
            // archives — prp_aframe_sign_citywok is one — cannot be posed at
            // all. Seventeen of its twenty-five planes faced away and were
            // culled. The reference tied this to setup assembly; the
            // specification's own list of deliberate approximations says "all
            // assembled Camel planes", and the geometry does not become
            // mirrored only when a setup file happens to exist.
            const bool doubleSided = true;
            bool needDefaultMaterial = options.SceneGraph is not null
                && HasUntexturedDrawnPart(model, materials);
            int defaultMaterialIndex = needDefaultMaterial ? materials.Materials.Length : -1;

            WriteScenes(writer, roots, options.SceneName);
            WriteNodes(writer, nodes);
            WriteMeshes(writer, model, materials, layout, defaultMaterialIndex);

            writer.WriteStartArray("buffers");
            writer.WriteStartObject();
            writer.WriteNumber("byteLength", layout.BinLength);
            writer.WriteEndObject();
            writer.WriteEndArray();

            WriteBufferViews(writer, layout);
            WriteAccessors(writer, layout);
            WriteImages(writer, materials, layout);
            WriteTextures(writer, materials);
            WriteSamplers(writer, materials);
            WriteMaterials(writer, materials, doubleSided, needDefaultMaterial);
            WriteExtensionsUsed(writer, materials);
            WriteAnimations(writer, options.Animations, layout);

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>One glTF node, with identity transforms already elided.</summary>
    private readonly record struct EmittedNode(
        string Name,
        IReadOnlyList<int> Children,
        int? Mesh,
        double[]? Translation,
        double[]? Rotation,
        double[]? Scale);

    /// <summary>
    /// Builds the node list and the scene's roots, from the posed hierarchy when
    /// one was given and a flat mesh-node list otherwise, then wraps the roots in
    /// the presentation basis when it is included.
    /// </summary>
    private static (List<EmittedNode> Nodes, List<int> Roots) BuildNodes(GeometryModel model, GlbWriteOptions options)
    {
        List<EmittedNode> nodes = [];
        List<int> roots = [];

        if (options.SceneGraph is { } graph)
        {
            foreach (SceneNode node in graph.Nodes)
            {
                nodes.Add(new EmittedNode(
                    node.Name,
                    node.Children,
                    node.Mesh,
                    node.Translation == AnimVec3.Zero ? null : [node.Translation.X, node.Translation.Y, node.Translation.Z],
                    node.Rotation == AnimQuat.Identity ? null : [node.Rotation.X, node.Rotation.Y, node.Rotation.Z, node.Rotation.W],
                    node.Scale == AnimVec3.One ? null : [node.Scale.X, node.Scale.Y, node.Scale.Z]));
            }

            roots.AddRange(graph.Roots);
        }
        else
        {
            for (int i = 0; i < model.Parts.Length; i++)
            {
                // A local identity transform is omitted rather than written out.
                nodes.Add(new EmittedNode(model.Parts[i].Name + GlbNames.NodeSuffix, [], i, null, null, null));
                roots.Add(i);
            }
        }

        if (options.IncludePresentationBasis)
        {
            // The presentation root is appended last and becomes the scene's only
            // root; every prior root hangs beneath it and is reflected in X.
            nodes.Add(new EmittedNode(GlbNames.PresentationBasisNode, [.. roots], null, null, null, [-1.0, 1.0, 1.0]));
            roots = [nodes.Count - 1];
        }

        return (nodes, roots);
    }

    private static void WriteScenes(Utf8JsonWriter writer, List<int> roots, string sceneName)
    {
        writer.WriteStartArray("scenes");
        writer.WriteStartObject();
        writer.WriteStartArray("nodes");
        foreach (int root in roots)
        {
            writer.WriteNumberValue(root);
        }

        writer.WriteEndArray();
        writer.WriteString("name", sceneName);
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private static void WriteNodes(Utf8JsonWriter writer, List<EmittedNode> nodes)
    {
        writer.WriteStartArray("nodes");

        foreach (EmittedNode node in nodes)
        {
            writer.WriteStartObject();
            writer.WriteString("name", node.Name);

            if (node.Children.Count > 0)
            {
                writer.WriteStartArray("children");
                foreach (int child in node.Children)
                {
                    writer.WriteNumberValue(child);
                }

                writer.WriteEndArray();
            }

            if (node.Mesh is int mesh)
            {
                writer.WriteNumber("mesh", mesh);
            }

            WriteVectorProperty(writer, "translation", node.Translation);
            WriteVectorProperty(writer, "rotation", node.Rotation);
            WriteVectorProperty(writer, "scale", node.Scale);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteVectorProperty(Utf8JsonWriter writer, string name, double[]? values)
    {
        if (values is null)
        {
            return;
        }

        writer.WriteStartArray(name);
        foreach (double value in values)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }

    private static bool HasUntexturedDrawnPart(GeometryModel model, MaterialSet materials)
    {
        for (int part = 0; part < model.Parts.Length; part++)
        {
            if (materials.MaterialOfPart.IsDefaultOrEmpty || materials.MaterialOfPart[part] < 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteMeshes(Utf8JsonWriter writer, GeometryModel model, MaterialSet materials, Layout layout, int defaultMaterialIndex)
    {
        writer.WriteStartArray("meshes");

        for (int part = 0; part < model.Parts.Length; part++)
        {
            writer.WriteStartObject();
            writer.WriteString("name", model.Parts[part].Name);
            writer.WriteStartArray("primitives");
            writer.WriteStartObject();

            writer.WriteStartObject("attributes");
            writer.WriteNumber("POSITION", layout.IndexOf(part, AccessorKind.Position));
            writer.WriteNumber("NORMAL", layout.IndexOf(part, AccessorKind.Normal));
            if (model.Parts[part].HasUv0)
            {
                writer.WriteNumber("TEXCOORD_0", layout.IndexOf(part, AccessorKind.TexCoord0));
            }

            writer.WriteEndObject();

            writer.WriteNumber("indices", layout.IndexOf(part, AccessorKind.Indices));

            // Triangle list. Winding is preserved; the presentation root changes
            // handedness without rewriting a single index.
            writer.WriteNumber("mode", 4);

            int material = !materials.MaterialOfPart.IsDefaultOrEmpty && materials.MaterialOfPart[part] >= 0
                ? materials.MaterialOfPart[part]
                : defaultMaterialIndex;
            if (material >= 0)
            {
                writer.WriteNumber("material", material);
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAccessors(Utf8JsonWriter writer, Layout layout)
    {
        writer.WriteStartArray("accessors");

        for (int i = 0; i < layout.Accessors.Count; i++)
        {
            AccessorPlan plan = layout.Accessors[i];
            writer.WriteStartObject();

            // Image buffer views come first in the array, so an accessor's view
            // is its own index shifted past them.
            writer.WriteNumber("bufferView", layout.Images.Count + i);
            writer.WriteNumber("byteOffset", 0);
            writer.WriteNumber("componentType", plan.Kind == AccessorKind.Indices
                ? ComponentTypeUnsignedInt
                : ComponentTypeFloat);
            writer.WriteNumber("count", plan.Count);
            writer.WriteString("type", plan.Kind switch
            {
                AccessorKind.Indices => "SCALAR",
                AccessorKind.TexCoord0 => "VEC2",
                _ => "VEC3",
            });

            if (plan.Kind == AccessorKind.Position)
            {
                WriteBounds(writer, plan.Part.Positions);
            }

            writer.WriteEndObject();
        }

        for (int i = 0; i < layout.AnimAccessors.Count; i++)
        {
            AnimAccessor anim = layout.AnimAccessors[i];
            writer.WriteStartObject();
            writer.WriteNumber("bufferView", layout.Images.Count + layout.Accessors.Count + i);
            writer.WriteNumber("byteOffset", 0);
            writer.WriteNumber("componentType", ComponentTypeFloat);
            writer.WriteNumber("count", anim.Count);
            writer.WriteString("type", anim.Type);

            if (anim.HasBounds)
            {
                // Widened to double, as the reference does: the bound is the
                // float32 key value promoted, not its own shortest float text.
                writer.WriteStartArray("min");
                writer.WriteNumberValue((double)anim.Min);
                writer.WriteEndArray();
                writer.WriteStartArray("max");
                writer.WriteNumberValue((double)anim.Max);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteBounds(
        Utf8JsonWriter writer,
        ImmutableArray<Vector3D> positions)
    {
        Span<double> minimum = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        Span<double> maximum = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];

        foreach (Vector3D position in positions)
        {
            Span<double> components = [position.X, position.Y, position.Z];
            for (int axis = 0; axis < 3; axis++)
            {
                minimum[axis] = Math.Min(minimum[axis], components[axis]);
                maximum[axis] = Math.Max(maximum[axis], components[axis]);
            }
        }

        writer.WriteStartArray("min");
        foreach (double value in minimum)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("max");
        foreach (double value in maximum)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteImages(Utf8JsonWriter writer, MaterialSet materials, Layout layout)
    {
        if (materials.Images.IsDefaultOrEmpty)
        {
            return;
        }

        writer.WriteStartArray("images");

        for (int i = 0; i < materials.Images.Length; i++)
        {
            writer.WriteStartObject();

            // The name says which source texture, or which combination of
            // them, the bytes came from. The harness compares it, so it is
            // content rather than decoration.
            writer.WriteString("name", materials.Images[i].Name);
            writer.WriteNumber("bufferView", i);
            writer.WriteString("mimeType", "image/png");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTextures(Utf8JsonWriter writer, MaterialSet materials)
    {
        if (materials.Images.IsDefaultOrEmpty)
        {
            return;
        }

        writer.WriteStartArray("textures");

        for (int i = 0; i < materials.Images.Length; i++)
        {
            writer.WriteStartObject();
            writer.WriteNumber("source", i);
            writer.WriteNumber("sampler", SamplerIndex(materials, i));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes the distinct sampler states the materials asked for.
    /// </summary>
    /// <remarks>
    /// Sampler state is explicit rather than defaulted, because a glTF default
    /// sampler is undefined-filtering and would leave the wrap mode to the
    /// viewer — and wrap is exactly what distinguishes a tiled diffuse from a
    /// clamped alpha here.
    /// </remarks>
    private static void WriteSamplers(Utf8JsonWriter writer, MaterialSet materials)
    {
        if (materials.Images.IsDefaultOrEmpty)
        {
            return;
        }

        writer.WriteStartArray("samplers");

        foreach (TextureWrap wrap in DistinctWraps(materials))
        {
            int mode = wrap == TextureWrap.Repeat ? WrapRepeat : WrapClampToEdge;
            writer.WriteStartObject();
            writer.WriteNumber("wrapS", mode);
            writer.WriteNumber("wrapT", mode);
            writer.WriteNumber("magFilter", FilterLinear);
            writer.WriteNumber("minFilter", FilterLinearMipmapLinear);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteMaterials(Utf8JsonWriter writer, MaterialSet materials, bool doubleSided, bool needDefaultMaterial)
    {
        if (materials.Materials.IsDefaultOrEmpty && !needDefaultMaterial)
        {
            return;
        }

        writer.WriteStartArray("materials");

        foreach (SurfaceMaterial material in materials.Materials)
        {
            writer.WriteStartObject();
            writer.WriteString("name", material.Name);

            writer.WriteStartObject("pbrMetallicRoughness");
            writer.WriteStartArray("baseColorFactor");
            writer.WriteNumberValue(material.BaseColorFactor.R);
            writer.WriteNumberValue(material.BaseColorFactor.G);
            writer.WriteNumberValue(material.BaseColorFactor.B);
            writer.WriteNumberValue(material.BaseColorFactor.A);
            writer.WriteEndArray();

            if (material.ImageIndex is int image)
            {
                writer.WriteStartObject("baseColorTexture");
                writer.WriteNumber("index", image);

                // The engine samples at uv * repeat. Only the scale is carried:
                // the V orientation belongs to the engine's own shader, so no
                // offset accompanies it. A zero or negative repeat is emitted
                // verbatim, because an opaque surface's colour depends on it
                // independently of the coordinates.
                if (!material.Scale.IsIdentity)
                {
                    writer.WriteStartObject("extensions");
                    writer.WriteStartObject(TextureTransformExtension);
                    writer.WriteStartArray("scale");
                    writer.WriteNumberValue(material.Scale.U);
                    writer.WriteNumberValue(material.Scale.V);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            // The source has no metallic or roughness input. These are the
            // values that make the physically-based model reproduce a plain
            // textured surface rather than a shiny one.
            writer.WriteNumber("metallicFactor", 0);
            writer.WriteNumber("roughnessFactor", 1);
            writer.WriteEndObject();

            // A merged emissive companion rides on this material as a self-lit
            // texture, between the pbr block and the alpha mode. No texture
            // transform accompanies it: the emissive shader samples the raw UV.
            if (material is { EmissiveImageIndex: int emissive, EmissiveFactor: { } factor })
            {
                writer.WriteStartObject("emissiveTexture");
                writer.WriteNumber("index", emissive);
                writer.WriteEndObject();

                writer.WriteStartArray("emissiveFactor");
                writer.WriteNumberValue(factor.R);
                writer.WriteNumberValue(factor.G);
                writer.WriteNumberValue(factor.B);
                writer.WriteEndArray();
            }

            writer.WriteString("alphaMode", material.IsTransparent ? "BLEND" : "OPAQUE");

            // A posed part is emitted double-sided, which glTF carries here.
            if (doubleSided)
            {
                writer.WriteBoolean("doubleSided", true);
            }

            writer.WriteEndObject();
        }

        // The one default material every untextured posed part shares.
        if (needDefaultMaterial)
        {
            writer.WriteStartObject();
            writer.WriteString("name", GlbNames.PlanarDefaultMaterial);
            writer.WriteBoolean("doubleSided", true);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Declares the glTF extensions the document relies on.
    /// </summary>
    /// <remarks>
    /// Only written when something uses one, and it lists exactly what appears,
    /// so a reader that must understand an extension to load the file is told
    /// which. Emitted last, matching the reference document order.
    /// </remarks>
    private static void WriteExtensionsUsed(Utf8JsonWriter writer, MaterialSet materials)
    {
        bool usesTransform = false;
        if (!materials.Materials.IsDefaultOrEmpty)
        {
            foreach (SurfaceMaterial material in materials.Materials)
            {
                if (material.ImageIndex is not null && !material.Scale.IsIdentity)
                {
                    usesTransform = true;
                    break;
                }
            }
        }

        if (!usesTransform)
        {
            return;
        }

        writer.WriteStartArray("extensionsUsed");
        writer.WriteStringValue(TextureTransformExtension);
        writer.WriteEndArray();
    }

    /// <summary>The distinct sampler states, in first-use order.</summary>
    private static List<TextureWrap> DistinctWraps(MaterialSet materials)
    {
        List<TextureWrap> wraps = [];
        foreach (SurfaceMaterial material in materials.Materials)
        {
            if (!wraps.Contains(material.Wrap))
            {
                wraps.Add(material.Wrap);
            }
        }

        // An emissive image is always sampled repeated, so a model whose base
        // materials are all clamped still needs the repeat sampler for it.
        if (!wraps.Contains(TextureWrap.Repeat) && HasEmissiveImage(materials))
        {
            wraps.Add(TextureWrap.Repeat);
        }

        if (wraps.Count == 0)
        {
            wraps.Add(TextureWrap.Repeat);
        }

        return wraps;
    }

    private static int SamplerIndex(MaterialSet materials, int imageIndex)
    {
        List<TextureWrap> wraps = DistinctWraps(materials);

        foreach (SurfaceMaterial material in materials.Materials)
        {
            if (material.ImageIndex == imageIndex)
            {
                return wraps.IndexOf(material.Wrap);
            }
        }

        // Not a base image, so it is a merged emissive companion's raw texture,
        // which the engine samples repeated regardless of the base's wrap.
        return Math.Max(0, wraps.IndexOf(TextureWrap.Repeat));
    }

    private static void WriteAnimations(Utf8JsonWriter writer, ImmutableArray<Animation> animations, Layout layout)
    {
        if (animations.IsDefaultOrEmpty)
        {
            return;
        }

        writer.WriteStartArray("animations");
        for (int i = 0; i < animations.Length; i++)
        {
            Animation clip = animations[i];
            int first = layout.FirstAccessorOfAnimation(i);

            writer.WriteStartObject();
            writer.WriteString("name", clip.Name);

            // One time accessor per animation, shared by its own samplers; each
            // track has its own output accessor, laid out immediately after it.
            writer.WriteStartArray("samplers");
            for (int j = 0; j < clip.Tracks.Length; j++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("input", first);
                writer.WriteNumber("output", first + 1 + j);
                writer.WriteString("interpolation", clip.Tracks[j].Interpolation == TrackInterpolation.Step ? "STEP" : "LINEAR");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            // Sampler indices are per animation, so they restart at zero here
            // rather than continuing across the array.
            writer.WriteStartArray("channels");
            for (int j = 0; j < clip.Tracks.Length; j++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sampler", j);
                writer.WriteStartObject("target");
                writer.WriteNumber("node", clip.Tracks[j].Node);
                writer.WriteString("path", clip.Tracks[j].Path switch
                {
                    TrackPath.Translation => "translation",
                    TrackPath.Rotation => "rotation",
                    _ => "scale",
                });
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static bool HasEmissiveImage(MaterialSet materials)
    {
        foreach (SurfaceMaterial material in materials.Materials)
        {
            if (material.EmissiveImageIndex is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteBufferViews(Utf8JsonWriter writer, Layout layout)
    {
        writer.WriteStartArray("bufferViews");

        // An image view carries no target: it is not vertex or index data, and
        // declaring one would tell a loader to bind a PNG to a buffer binding.
        foreach (ImagePlan image in layout.Images)
        {
            writer.WriteStartObject();
            writer.WriteNumber("buffer", 0);
            writer.WriteNumber("byteOffset", image.ByteOffset);
            writer.WriteNumber("byteLength", image.ByteLength);
            writer.WriteEndObject();
        }

        foreach (AccessorPlan plan in layout.Accessors)
        {
            writer.WriteStartObject();
            writer.WriteNumber("buffer", 0);
            writer.WriteNumber("byteOffset", plan.ByteOffset);
            writer.WriteNumber("byteLength", plan.ByteLength);
            writer.WriteNumber("target", plan.Kind == AccessorKind.Indices
                ? TargetElementArrayBuffer
                : TargetArrayBuffer);
            writer.WriteEndObject();
        }

        // Animation buffers carry no target: they feed samplers, not a vertex or
        // index binding.
        foreach (AnimAccessor anim in layout.AnimAccessors)
        {
            writer.WriteStartObject();
            writer.WriteNumber("buffer", 0);
            writer.WriteNumber("byteOffset", anim.ByteOffset);
            writer.WriteNumber("byteLength", anim.ByteLength);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Wraps the two chunks in the GLB container.
    /// </summary>
    /// <remarks>
    /// Internal so the padding rules can be tested directly. Through the public
    /// path the binary chunk never needs padding — every element this writer
    /// emits is four bytes wide — so its filler byte would otherwise go
    /// unverified until images make the case reachable.
    /// </remarks>
    internal static Result<byte[]> Assemble(byte[] json, byte[] bin)
    {
        int jsonPadding = Padding(json.Length);
        int binPadding = Padding(bin.Length);

        long total = HeaderLength +
            ChunkHeaderLength + json.Length + jsonPadding +
            ChunkHeaderLength + bin.Length + binPadding;

        if (total > uint.MaxValue)
        {
            return TooLarge();
        }

        byte[] glb = new byte[total];
        Span<byte> span = glb;

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);

        int at = HeaderLength;
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)(json.Length + jsonPadding));
        BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], JsonChunkType);
        json.CopyTo(span[(at + ChunkHeaderLength)..]);

        // The JSON chunk pads with spaces and the binary chunk with zeroes.
        span.Slice(at + ChunkHeaderLength + json.Length, jsonPadding).Fill((byte)' ');

        at += ChunkHeaderLength + json.Length + jsonPadding;
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)(bin.Length + binPadding));
        BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], BinChunkType);
        bin.CopyTo(span[(at + ChunkHeaderLength)..]);
        span.Slice(at + ChunkHeaderLength + bin.Length, binPadding).Clear();

        return Result.Ok(glb);
    }

    internal static int Padding(int length) => (Alignment - (length % Alignment)) % Alignment;

    private static Refusal TooLarge() => Refusal.Unsupported(
        "The scene does not fit in a GLB, whose counts and offsets are UInt32.");

    private static Refusal NotFinite(GeometryPart part, string what) => Refusal.Malformed(
        string.Create(CultureInfo.InvariantCulture, $"Model part {part.SourceOrdinal} has a {what} that is not finite once narrowed to binary32."));

    private enum AccessorKind
    {
        Position,
        Normal,
        TexCoord0,
        Indices,
    }

    private readonly record struct ImagePlan(long Offset, int ByteLength)
    {
        public int ByteOffset => (int)Offset;
    }

    private readonly record struct AnimAccessor(
        long Offset, int Count, int Width, float[] Floats, bool HasBounds, float Min, float Max)
    {
        public int ByteOffset => (int)Offset;

        public int ByteLength => Floats.Length * sizeof(float);

        public string Type => Width switch { 1 => "SCALAR", 3 => "VEC3", _ => "VEC4" };
    }

    private readonly record struct AccessorPlan(AccessorKind Kind, GeometryPart Part, long Offset, int ByteLength)
    {
        public int ByteOffset => (int)Offset;

        public int Count => Kind switch
        {
            AccessorKind.Indices => Part.Indices.Length,
            AccessorKind.TexCoord0 => Part.Uv0.Length,
            AccessorKind.Normal => Part.Normals.Length,
            _ => Part.Positions.Length,
        };
    }

    private sealed class Layout(
        List<AccessorPlan> accessors,
        List<int> firstAccessorOfPart,
        List<ImagePlan> images,
        List<AnimAccessor> animAccessors,
        List<int> firstAccessorOfAnimation,
        int binLength)
    {
        public List<AccessorPlan> Accessors { get; } = accessors;

        public List<ImagePlan> Images { get; } = images;

        public List<AnimAccessor> AnimAccessors { get; } = animAccessors;

        public int BinLength { get; } = binLength;

        /// <summary>
        /// The accessor array index of one animation's time input. Its output
        /// accessors follow immediately, one per track, so the whole block is
        /// addressable from this single number.
        /// </summary>
        public int FirstAccessorOfAnimation(int animation) => firstAccessorOfAnimation[animation];

        /// <summary>
        /// Where a part's accessors begin. Recorded during planning rather than
        /// searched for: a scan per lookup would be quadratic in the accessor
        /// count, and a real model reaches eleven thousand of them.
        /// </summary>
        public int IndexOf(int partOrdinal, AccessorKind kind)
        {
            int first = firstAccessorOfPart[partOrdinal];
            bool hasUv0 = Accessors[first].Part.HasUv0;

            return kind switch
            {
                AccessorKind.Position => first,
                AccessorKind.Normal => first + 1,
                AccessorKind.TexCoord0 => first + 2,
                _ => first + (hasUv0 ? 3 : 2),
            };
        }
    }
}

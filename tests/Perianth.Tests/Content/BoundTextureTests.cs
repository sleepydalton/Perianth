using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Perianth.Tests.Editordata;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// The textures an extraction takes alongside a character.
/// </summary>
/// <remarks>
/// A texture is not named after the model it paints — it is named inside the
/// editordata's material bindings, in a shared tree — so no naming rule reaches
/// it and the file has to be read. Leaving them out made an extracted character
/// export as geometry and animation and then refuse on its first texture, which
/// reads as a broken export rather than an incomplete extraction.
/// </remarks>
public sealed class BoundTextureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-bound-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_textures_a_models_materials_bind_are_found()
    {
        byte[] editordata = new EditordataBuilder()
            .Section(MaterialSpec.Standard(diffuse: "camel/shared/tex_skin_d.dds"))
            .Section(MaterialSpec.Standard(diffuse: "camel/shared/tex_cloth_d.dds"))
            .Build();

        ImmutableArray<string> bound = Bound(
            editordata,
            held: ["camel/shared/tex_skin_d.dds", "camel/shared/tex_cloth_d.dds"]);

        Assert.Equal(
            ["camel/shared/tex_cloth_d.dds", "camel/shared/tex_skin_d.dds"],
            bound.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_binding_the_archives_do_not_hold_is_skipped_rather_than_refused_over()
    {
        // A model is not less extractable because one of its eighty textures is
        // missing. The export refuses over that same path later, naming the
        // texture — which is where the refusal belongs, because that is where it
        // stops somebody.
        byte[] editordata = new EditordataBuilder()
            .Section(MaterialSpec.Standard(diffuse: "camel/shared/tex_here_d.dds"))
            .Section(MaterialSpec.Standard(diffuse: "camel/shared/tex_gone_d.dds"))
            .Build();

        ImmutableArray<string> bound = Bound(editordata, held: ["camel/shared/tex_here_d.dds"]);

        Assert.Equal(["camel/shared/tex_here_d.dds"], bound);
    }

    [Fact]
    public void A_model_with_no_editordata_binds_nothing()
    {
        CharacterAssets assets = Assets(editordata: null);
        using ContentSources content = new(_root, sdfRoot: null);

        Assert.Empty(ArchiveExtraction.BoundTextures([], content, assets).Value);
    }

    private ImmutableArray<string> Bound(byte[] editordata, string[] held)
    {
        Write("camel/model.editordata", editordata);

        ImmutableArray<SdfPathEntry> paths =
            [.. held.Select((path, i) => new SdfPathEntry(path, i, IsDirectory: false))];

        using ContentSources content = new(_root, sdfRoot: null);
        Result<ImmutableArray<string>> found =
            ArchiveExtraction.BoundTextures(paths, content, Assets("camel/model.editordata"));

        Assert.True(found.IsSuccess, found.IsSuccess ? "" : found.Refusal!.Message);
        return found.Value;
    }

    private static CharacterAssets Assets(string? editordata) => new(
        Name: "model",
        Model: "camel/model.mmb",
        Cameldata: "camel/model.cameldata",
        Editordata: editordata,
        Setup: null,
        Mouth: null,
        Eyes: null,
        Pupils: null,
        Eyebrows: null,
        Clips: [],
        LipsyncDatabase: null,
        Unresolved: []);

    private void Write(string relative, byte[] bytes)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }
}

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Perianth.Cli;
using Perianth.Core.Geometry;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Perianth.Gltf;
using Perianth.Tests.Cameldata;
using Perianth.Tests.Mmb;
using Xunit;

namespace Perianth.Tests.Cli;

/// <summary>
/// The <c>geometry</c> verb, end to end over a synthetic model.
/// </summary>
/// <remarks>
/// The grammar and the reporting rather than the edit, which
/// <c>GeometryEditTests</c> covers. The reporting earns its own tests for the
/// same reason the material verb's does: the count is what stands between an
/// author and a model reshaped throughout, and <c>--dry-run</c> is only useful
/// if the number it prints is the number that would be written.
/// </remarks>
public sealed class GeometryCommandTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-geometry-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_dry_run_says_what_would_move_and_writes_nothing()
    {
        Model model = Build();
        string edited = Glb(model, moveBy: 5f);

        (int code, string output, _) = Run(
            "--mmb", model.Mmb, "--cameldata", model.Cameldata, "--from", edited, "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("Reshaped 1 parts", output, StringComparison.Ordinal);
        Assert.Contains("3 vertex positions moved", output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(_directory.FullName));
    }

    [Fact]
    public void Writing_a_mod_carries_the_model_beside_the_edited_file()
    {
        // The two are a matched pair, and whether the loader would accept a lone
        // cameldata against the archived model is unmeasured. The MMB is copied
        // rather than written, so carrying it costs nothing.
        Model model = Build();
        string edited = Glb(model, moveBy: 5f);
        string destination = Path.Combine(_directory.FullName, "mods");

        (int code, string output, string error) = Run(
            "--mmb", model.Mmb, "--cameldata", model.Cameldata, "--from", edited,
            "--out", destination, "--name", "probe");

        Assert.Equal(0, code);
        Assert.Empty(error);

        string[] written = [.. Directory
            .EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)!];

        Assert.Contains("chr_test.cameldata", written, StringComparer.Ordinal);
        Assert.Contains("chr_test.mmb", written, StringComparer.Ordinal);
        Assert.Contains("manifest.ini", written, StringComparer.Ordinal);
        Assert.Contains("the edited cameldata and the model beside it", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unedited_GLB_refuses_rather_than_writing_a_mod_that_changes_nothing()
    {
        // It would install, load, and do nothing -- indistinguishable from a mod
        // that failed, with no message to tell the author which.
        Model model = Build();
        string untouched = Glb(model, moveBy: 0f);

        (int code, _, string error) = Run(
            "--mmb", model.Mmb, "--cameldata", model.Cameldata, "--from", untouched, "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("Nothing moved", error, StringComparison.Ordinal);
        Assert.Contains("Edit Mode", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_no_model_says_which_options_are_needed()
    {
        (int code, _, string error) = Run("--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("--mmb", error, StringComparison.Ordinal);
        Assert.Contains("--from", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_without_a_destination_says_so_rather_than_guessing_one()
    {
        Model model = Build();
        string edited = Glb(model, moveBy: 5f);

        (int code, _, string error) = Run(
            "--mmb", model.Mmb, "--cameldata", model.Cameldata, "--from", edited);

        Assert.NotEqual(0, code);
        Assert.Contains("--out", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_the_verb_does_not_accept_refuses_rather_than_being_ignored()
    {
        (int code, _, string error) = Run("--repoint", "a=b");

        Assert.NotEqual(0, code);
        Assert.Contains("--repoint", error, StringComparison.Ordinal);
    }

    /// <summary>A one-part model on disk, with the provenance a mod write needs.</summary>
    private Model Build()
    {
        string mmb = Path.Combine(_directory.FullName, "chr_test.mmb");
        File.WriteAllBytes(mmb, new MmbFileBuilder
        {
            VertexCount = 3,
            PositionEntries = [0, 1, 2],
            EntrySize = 2,
        }.Build());

        string cameldata = Path.Combine(_directory.FullName, "chr_test.cameldata");
        File.WriteAllBytes(cameldata, new CameldataBuilder
        {
            Mode = 3,
            Xy = [new(1, 2), new(3, 4), new(5, 6)],
            Z = [7f],
            PackedZ = [0u],
        }.Build());

        // The mod write reads where a file came from rather than inferring it, so
        // an extraction manifest has to be here for the verb to run at all. The
        // digests are the files' own, which is what the lookup matches on.
        File.WriteAllText(
            Path.Combine(_directory.FullName, "perianth-extraction.json"),
            $$"""
            {
              "extracted": [
                {
                  "path": "camel/baked/assets/chr_test.mmb",
                  "output": "chr_test.mmb",
                  "sha256": "{{Digest(mmb)}}"
                },
                {
                  "path": "camel/baked/assets/chr_test.cameldata",
                  "output": "chr_test.cameldata",
                  "sha256": "{{Digest(cameldata)}}"
                }
              ]
            }
            """);

        return new Model(mmb, cameldata);
    }

    private static string Digest(string path) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>The model exported to a GLB, with its one part slid along X.</summary>
    private string Glb(Model model, float moveBy)
    {
        MmbModel mmb = MmbReader.Read(SourceFileReader.Read(model.Mmb).Value).Value;
        CameldataFile cameldata = CameldataReader.Read(SourceFileReader.Read(model.Cameldata).Value).Value;
        GeometryModel assembled = GeometryAssembler.Assemble(mmb, cameldata).Value;

        GeometryPart part = assembled.Parts[0];
        GeometryPart moved = new(
            part.SourceOrdinal, part.Name, part.SourceLabel, part.HierarchyBindingName,
            [.. part.Positions.Select(p => new Vector3D(p.X + moveBy, p.Y, p.Z))],
            part.Indices, part.Uv0, part.Normals);

        byte[] glb = GlbWriter.Write(
            new GeometryModel(assembled.Mode, [moved], assembled.SurfaceUv0Unavailable),
            new GlbWriteOptions()).Value;

        string path = Path.Combine(_directory.FullName, $"edited-{moveBy.ToString(CultureInfo.InvariantCulture)}.glb");
        File.WriteAllBytes(path, glb);
        return path;
    }

    private static (int Code, string Out, string Error) Run(params string[] arguments)
    {
        StringWriter output = new();
        StringWriter error = new();
        int code = Program.Run(["geometry", .. arguments], output, error);
        return (code, output.ToString(), error.ToString());
    }

    private readonly record struct Model(string Mmb, string Cameldata);
}

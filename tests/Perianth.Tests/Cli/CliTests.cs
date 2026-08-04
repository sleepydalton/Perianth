using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Perianth.Pipeline;
using Perianth.Cli;
using Perianth.Formats.Diagnostics;
using Perianth.Tests.Cameldata;
using Perianth.Tests.Mmb;
using Xunit;

namespace Perianth.Tests.Cli;

public sealed class CliTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-cli-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void No_arguments_prints_usage_and_succeeds()
    {
        StringWriter output = new();

        int code = Program.Run([], output, new StringWriter());

        Assert.Equal(0, code);
        Assert.Contains("perianth export", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_command_is_refused()
    {
        StringWriter error = new();

        int code = Program.Run(["convert"], new StringWriter(), error);

        Assert.Equal(2, code);
        Assert.Contains("Export refused (unsupported)", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_export_writes_a_GLB_and_reports_its_counts()
    {
        Inputs inputs = Write();
        StringWriter output = new();
        StringWriter error = new();

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            output, error);

        Assert.Equal(0, code);
        Assert.True(File.Exists(inputs.Out));
        Assert.Contains("2 meshes", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("6 vertices", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("2 triangles", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unposed_multi_part_export_warns_in_the_words_the_reference_uses()
    {
        // The wording is compared verbatim against the frozen reference, so this
        // is not a message to improve, and the trailing count is part of it.
        Inputs inputs = Write();
        StringWriter error = new();

        Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), error);

        string warning = error.ToString();
        Assert.StartsWith("no --setup-anim was given, so this export is the model's complete part list", warning, StringComparison.Ordinal);
        Assert.Contains("'unposed-all-parts' so the file says so on its own.", warning, StringComparison.Ordinal);
        Assert.Contains("Parts emitted: 2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_part_export_has_nothing_to_warn_about()
    {
        // One part cannot overlay alternate states, so the file is not
        // misleading without a hierarchy.
        Inputs inputs = Write(parts: 1);
        StringWriter error = new();

        Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), error);

        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void The_JSON_result_follows_the_recommended_schema()
    {
        Inputs inputs = Write();
        StringWriter output = new();

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out, "--json"],
            output, new StringWriter());

        Assert.Equal(0, code);

        JsonElement result = JsonDocument.Parse(output.ToString()).RootElement;
        Assert.Equal("perianth-export-result.v1", result.GetProperty("schema_version").GetString());
        Assert.Equal("exported", result.GetProperty("status").GetString());
        Assert.Equal(inputs.Out, result.GetProperty("output").GetString());
        Assert.False(result.GetProperty("partial_export").GetBoolean());
        Assert.Equal(2, result.GetProperty("counts").GetProperty("meshes").GetInt32());

        JsonElement diagnostic = Assert.Single(result.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("export_unposed", diagnostic.GetProperty("id").GetString());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("audio").ValueKind);
    }

    [Fact]
    public void A_refusal_exits_two_and_names_its_kind_on_standard_error()
    {
        Inputs inputs = Write();
        StringWriter error = new();

        int code = Program.Run(
            ["export", "--mmb", Path.Combine(_directory.FullName, "absent.mmb"),
             "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), error);

        Assert.Equal(2, code);
        Assert.Contains("Export refused (resource):", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(inputs.Out));
    }

    [Fact]
    public void A_refusal_in_JSON_carries_the_kind_a_caller_branches_on()
    {
        Inputs inputs = Write();
        StringWriter output = new();

        int code = Program.Run(
            ["export", "--mmb", Path.Combine(_directory.FullName, "absent.mmb"),
             "--cameldata", inputs.Cameldata, "--out", inputs.Out, "--json"],
            output, new StringWriter());

        Assert.Equal(2, code);

        JsonElement result = JsonDocument.Parse(output.ToString()).RootElement;
        Assert.Equal("refused", result.GetProperty("status").GetString());
        Assert.Equal(0, result.GetProperty("counts").GetProperty("meshes").GetInt32());

        JsonElement diagnostic = Assert.Single(result.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("resource", diagnostic.GetProperty("refusal_kind").GetString());
        Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
    }

    [Fact]
    public void An_existing_output_survives_a_refusal_untouched()
    {
        Inputs inputs = Write();
        File.WriteAllBytes(inputs.Out, [7, 7, 7]);

        Program.Run(
            ["export", "--mmb", Path.Combine(_directory.FullName, "absent.mmb"),
             "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), new StringWriter());

        Assert.Equal<byte[]>([7, 7, 7], File.ReadAllBytes(inputs.Out));
    }

    [Theory]
    [InlineData("--editordata", "PATH")]
    [InlineData("--model", "PATH")]
    public void An_option_this_build_cannot_honour_is_rejected_rather_than_ignored(string option, string value)
    {
        // Accepting and ignoring it would produce an export that is quietly not
        // what was asked for.
        Inputs inputs = Write();
        StringWriter error = new();

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out, option, value],
            new StringWriter(), error);

        Assert.Equal(2, code);
        Assert.Contains(option, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Source_space_and_the_default_differ_in_the_file_they_write()
    {
        Inputs plain = Write();
        Program.Run(["export", "--mmb", plain.Mmb, "--cameldata", plain.Cameldata, "--out", plain.Out],
            new StringWriter(), new StringWriter());
        byte[] withBasis = File.ReadAllBytes(plain.Out);

        string sourceSpaceOut = Path.Combine(_directory.FullName, "source-space.glb");
        Program.Run(
            ["export", "--mmb", plain.Mmb, "--cameldata", plain.Cameldata, "--out", sourceSpaceOut, "--source-space"],
            new StringWriter(), new StringWriter());

        Assert.NotEqual(withBasis, File.ReadAllBytes(sourceSpaceOut));
    }

    [Fact]
    public void Writing_the_output_over_an_input_is_refused()
    {
        Inputs inputs = Write();
        Result<ExportRequest> parsed = ExportArguments.Parse(
            ["--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Mmb]);

        Assert.True(parsed.IsRefused);
        Assert.Contains("must not overwrite what it reads", parsed.Refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--cameldata c --out o")]
    [InlineData("--mmb m --out o")]
    [InlineData("--mmb m --cameldata c")]
    public void Every_required_option_is_required(string arguments)
    {
        Result<ExportRequest> parsed = ExportArguments.Parse(arguments.Split(' '));

        Assert.True(parsed.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, parsed.Refusal.Kind);
    }

    [Fact]
    public void An_option_missing_its_value_is_refused()
    {
        Result<ExportRequest> parsed = ExportArguments.Parse(["--mmb"]);

        Assert.True(parsed.IsRefused);
        Assert.Contains("--mmb needs a value", parsed.Refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--mmb m --cameldata c --out o --time 2.5")]                         // nonzero time needs a setup
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --animate")]           // animate needs a clip
    [InlineData("--mmb m --cameldata c --out o --clip-anim k")]                      // clip needs a setup
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --clip-anim k --animate --time 2.5")] // animate excludes time
    [InlineData("--mmb m --cameldata c --out o --mouth-anim a --mouth-state 5")]                    // facial needs a setup
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --mouth-anim a")]                     // atlas needs its state
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --mouth-state 5")]                    // state needs its atlas
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --mouth-anim a --mouth-state 25")]    // state out of range
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --mouth-anim a --mouth-state 0")]     // state out of range
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --eyes-anim a --eye-state 12")]       // eyes 1..11
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --pupils-anim a --pupil-state 14")]   // pupils 1..13
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --eyebrows-anim a --eyebrow-state 7")]// eyebrows 1..6
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --pupils-anim a")]                    // atlas needs its state
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --pupil-state 3")]                    // state needs its atlas
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --pupils-anim a --pupil-state 3 --pupil-position sideways")] // bad mode
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --pupil-position mesh-neutral")]      // mesh-neutral needs the atlas
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --mouth-anim a --lipsync-database d")]             // lip sync needs a speech id
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --lipsync-database d --speech-id 5")]             // lip sync needs the mouth atlas
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --mouth-anim a --mouth-state 3 --lipsync-database d --speech-id 5")] // schedule excludes a fixed state
    [InlineData("--mmb m --cameldata c --out o --wem-root w")]                                      // audio needs a speech id
    [InlineData("--mmb m --cameldata c --out o --speech-id 5")]                                     // speech id needs a database or wem root
    [InlineData("--mmb m --cameldata c --out o --vgmstream-cli v")]                                 // vgmstream needs a wem root
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --eyes-anim a --blink-at 0.5")]       // blink needs animate
    [InlineData("--mmb m --cameldata c --out o --setup-anim s --clip-anim k --animate --blink-at 0.5")] // blink needs an eye atlas
    public void The_pose_options_reject_incoherent_combinations(string arguments)
    {
        Result<ExportRequest> parsed = ExportArguments.Parse(arguments.Split(' '));

        Assert.True(parsed.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, parsed.Refusal.Kind);
    }

    private Inputs Write(int parts = 2)
    {
        MmbFileBuilder mmb = new()
        {
            VertexCount = 3,
            PositionEntries = [0, 1, 2],
            EntrySize = 2,
            Repeat = parts,
        };

        CameldataBuilder cameldata = new()
        {
            Mode = 3,
            ConstantCount = parts,
            Xy = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)],
            Z = [0f],
        };

        string mmbPath = Path.Combine(_directory.FullName, "model.mmb");
        string cameldataPath = Path.Combine(_directory.FullName, "model.cameldata");
        File.WriteAllBytes(mmbPath, mmb.Build());
        File.WriteAllBytes(cameldataPath, cameldata.Build());

        return new Inputs(mmbPath, cameldataPath, Path.Combine(_directory.FullName, "model.glb"));
    }

    private sealed record Inputs(string Mmb, string Cameldata, string Out);
}

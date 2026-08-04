using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using Perianth.Cli;
using Perianth.Tests.Cameldata;
using Perianth.Tests.Mmb;
using Xunit;

namespace Perianth.Tests.Cli;

/// <summary>
/// An unposed multi-part export refuses when this model's own setup ANIM was
/// sitting beside the inputs and simply not passed. The decision is the real
/// association rule against the candidate, not its filename; the name only bounds
/// which files are opened, by requiring "setup" in the stem.
/// </summary>
public sealed class ForgottenSetupTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-forgotten-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_matching_setup_beside_the_inputs_refuses_and_names_the_file()
    {
        Inputs inputs = WritePair(parts: 2);
        WriteAnim("anm_model_setup.anim", names: ["part"], parents: [Root]);
        StringWriter error = new();

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), error);

        Assert.Equal(2, code);
        Assert.Contains("Export refused (unsupported)", error.ToString(), StringComparison.Ordinal);
        // The message must name the file, or the caller has to guess which of a
        // directory of animations it meant, and offer the override.
        Assert.Contains("anm_model_setup.anim", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--allow-unposed", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(inputs.Out));
    }

    [Fact]
    public void Allow_unposed_exports_it_anyway_and_still_says_what_it_is()
    {
        // Overridden, not silenced: the scene keeps the name that discloses the
        // overlaid part list, and the unposed warning still fires.
        Inputs inputs = WritePair(parts: 2);
        WriteAnim("anm_model_setup.anim", names: ["part"], parents: [Root]);
        StringWriter error = new();

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out, "--allow-unposed"],
            new StringWriter(), error);

        Assert.Equal(0, code);
        Assert.True(File.Exists(inputs.Out));
        Assert.Contains("no --setup-anim was given", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Another_models_setup_beside_the_inputs_is_ignored()
    {
        // A directory holding several characters is common. A setup that does not
        // rig this model is not this model's forgotten argument.
        Inputs inputs = WritePair(parts: 2);
        WriteAnim("anm_other_setup.anim", names: ["elsewhere"], parents: [Root]);

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), new StringWriter());

        Assert.Equal(0, code);
        Assert.True(File.Exists(inputs.Out));
    }

    [Fact]
    public void The_matching_setup_is_found_among_unrelated_ones()
    {
        Inputs inputs = WritePair(parts: 2);
        WriteAnim("anm_other_setup.anim", names: ["elsewhere"], parents: [Root]);
        WriteAnim("anm_model_setup.anim", names: ["part"], parents: [Root]);
        StringWriter error = new();

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), error);

        Assert.Equal(2, code);
        Assert.Contains("anm_model_setup.anim", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_animation_whose_stem_is_not_a_setup_is_never_opened()
    {
        // The name filter keeps a directory of hundreds of clips cheap. Even a
        // file that would rig the model never triggers the refusal if its stem
        // does not mark it a setup.
        Inputs inputs = WritePair(parts: 2);
        WriteAnim("anm_model_base_idle_front.anim", names: ["part"], parents: [Root]);

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), new StringWriter());

        Assert.Equal(0, code);
        Assert.True(File.Exists(inputs.Out));
    }

    [Fact]
    public void An_unreadable_neighbour_does_not_break_the_export()
    {
        Inputs inputs = WritePair(parts: 2);
        File.WriteAllBytes(Path.Combine(_directory.FullName, "anm_model_setup.anim"), Encoding.ASCII.GetBytes("not an ANIM file"));

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), new StringWriter());

        Assert.Equal(0, code);
        Assert.True(File.Exists(inputs.Out));
    }

    [Fact]
    public void A_single_part_model_is_never_refused()
    {
        // One part cannot overlay alternate states, so an unposed export of it is
        // the legitimate case and is exempt for the same reason as the warning.
        Inputs inputs = WritePair(parts: 1);
        WriteAnim("anm_model_setup.anim", names: ["part"], parents: [Root]);

        int code = Program.Run(
            ["export", "--mmb", inputs.Mmb, "--cameldata", inputs.Cameldata, "--out", inputs.Out],
            new StringWriter(), new StringWriter());

        Assert.Equal(0, code);
        Assert.True(File.Exists(inputs.Out));
    }

    // --- fixtures ------------------------------------------------------------

    private const int Root = -1;
    private const int Active = 0xFFFF;

    private Inputs WritePair(int parts)
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

    private void WriteAnim(string fileName, string[] names, int[] parents)
    {
        int[] scai = new int[names.Length];
        Array.Fill(scai, Active);

        List<byte> bytes = [.. new byte[0x3C]];
        "ANIM"u8.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..4]);
        Write32(bytes, 0x1C, 5);              // rotation layout selector
        Write32(bytes, 0x24, names.Length);   // node count

        bytes.AddRange(Chunk("SCAI", U16(scai)));
        bytes.AddRange(Chunk("NAME", Names(names)));
        bytes.AddRange(Chunk("PRNT", U16([.. Array.ConvertAll(parents, p => p < 0 ? 0xFFFF : p)])));

        File.WriteAllBytes(Path.Combine(_directory.FullName, fileName), [.. bytes]);
    }

    private static byte[] Chunk(string tag, byte[] payload) => [.. Encoding.ASCII.GetBytes(tag), .. payload];

    private static byte[] U16(int[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)(values[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)((values[i] >> 8) & 0xFF);
        }

        return bytes;
    }

    private static byte[] Names(string[] names)
    {
        List<byte> bytes = [];
        foreach (string name in names)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes(name));
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static void Write32(List<byte> bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private sealed record Inputs(string Mmb, string Cameldata, string Out);
}

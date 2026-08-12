using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Reading the costume palette, against invented colours.
/// </summary>
/// <remarks>
/// The names here are made up. The real table is the game's own data and a
/// fixture cut from it would bring that data into this repository; what these
/// exercise is the pairing of a colour record with a paper scan, which does not
/// care what either is called.
/// </remarks>
public sealed class PaperPaletteTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-palette-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_colour_is_paired_with_its_paper_and_its_swatch()
    {
        ImmutableArray<PaperSwatch> palette = Palette(
            Table(("SP_moss", "ff2E7D32")),
            Paper("moss"));

        PaperSwatch swatch = Assert.Single(palette);
        Assert.Equal("moss", swatch.Name);
        Assert.Equal(PaperPalette.LibraryFolder + "tex_moss_abc123_d.dds", swatch.TexturePath);
        Assert.Equal("#2E7D32", swatch.Hex);
        Assert.Equal(0x2E, swatch.Red);
        Assert.Equal(0x7D, swatch.Green);
        Assert.Equal(0x32, swatch.Blue);
    }

    [Fact]
    public void Only_the_paper_backed_colours_are_offered()
    {
        // The table also holds a generic spectrum and a few specials. They are
        // colours the picker's grid does not show, and none has a paper, so
        // offering them would be offering a choice that cannot be applied.
        ImmutableArray<PaperSwatch> palette = Palette(
            Table(("SP_moss", "ff2E7D32"), ("BlueMid", "ff526EFF"), ("NoTint", "ffBBBBBB")),
            Paper("moss"));

        Assert.Equal(["moss"], palette.Select(s => s.Name));
    }

    [Fact]
    public void A_colour_whose_paper_is_missing_is_left_out()
    {
        // Better absent than present and unusable: a grid entry that fails only
        // once chosen is worse than one that was never shown.
        ImmutableArray<PaperSwatch> palette = Palette(
            Table(("SP_moss", "ff2E7D32"), ("SP_gone", "ff112233")),
            Paper("moss"));

        Assert.Equal(["moss"], palette.Select(s => s.Name));
    }

    [Fact]
    public void The_artists_copy_of_a_paper_is_not_offered()
    {
        // The scans exist twice. Only the library copy is bound by anything, so
        // a mod naming the other would point at a file the game holds and never
        // reads — which looks exactly like a mod that works.
        ImmutableArray<PaperSwatch> palette = Palette(
            Table(("SP_moss", "ff2E7D32")),
            "camel/baked/assets/textures/southpark/user_data/maya/reference/textures/paperscans512/tex_moss_zzz_d.dds");

        Assert.Empty(palette);
    }

    [Fact]
    public void Two_papers_of_one_name_resolve_the_same_way_every_time()
    {
        // The index's order is not something to rely on, and a palette that
        // depended on it would offer a different texture for the same colour
        // between runs for no visible reason.
        string first = PaperPalette.LibraryFolder + "tex_moss_aaa_d.dds";
        string second = PaperPalette.LibraryFolder + "tex_moss_zzz_d.dds";

        Assert.Equal(first, Palette(Table(("SP_moss", "ff2E7D32")), first, second)[0].TexturePath);
        Assert.Equal(first, Palette(Table(("SP_moss", "ff2E7D32")), second, first)[0].TexturePath);
    }

    [Fact]
    public void A_texture_that_is_not_a_paper_is_not_a_colour()
    {
        ImmutableArray<PaperSwatch> palette = Palette(Table(("SP_moss", "ff2E7D32")), Paper("moss"));

        Assert.NotNull(PaperPalette.Match(palette, palette[0].TexturePath));
        Assert.Null(PaperPalette.Match(palette, "camel/baked/assets/textures/tex_white16_d.dds"));
    }

    [Fact]
    public void No_colour_table_is_refused_rather_than_answered_with_nothing()
    {
        // An empty palette and an absent one look identical to a caller, and
        // they call for different things to be said to the user.
        using ContentSources content = new(_root, sdfRoot: null);

        Result<ImmutableArray<PaperSwatch>> read = PaperPalette.Read(content, []);

        Assert.False(read.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, read.Refusal!.Kind);
    }

    // --- fixtures ------------------------------------------------------------

    private static string Paper(string name) =>
        PaperPalette.LibraryFolder + $"tex_{name}_abc123_d.dds";

    private static string Table(params (string Name, string Argb)[] colours) =>
        string.Concat(colours.Select((c, i) => string.Concat(
            $"TintColor TintColor_{c.Name} < uid={i:X32} >\n",
            "{\n",
            $"\tmyColor 0x{c.Argb}\n",
            "}\n\n")));

    private ImmutableArray<PaperSwatch> Palette(string table, params string[] paths)
    {
        string file = Path.Combine(_root, PaperPalette.ColourTable.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, table);

        using ContentSources content = new(_root, sdfRoot: null);
        Result<ImmutableArray<PaperSwatch>> read = PaperPalette.Read(
            content, [.. paths.Select((p, i) => new SdfPathEntry(p, i, IsDirectory: false))]);

        Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Refusal!.Message);
        return read.Value;
    }
}

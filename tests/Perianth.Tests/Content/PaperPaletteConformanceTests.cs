using System;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// The costume palette, against the game's own data.
/// </summary>
/// <remarks>
/// Skips without the archives, as the other asset-backed suites do.
/// </remarks>
public sealed class PaperPaletteConformanceTests
{
    private const string RootVariable = "PERIANTH_SDF_ROOT";

    /// <summary>
    /// The picker's grid is five rows of sixteen.
    /// </summary>
    /// <remarks>
    /// Pinned as a number because it is the claim everything else rests on. The
    /// palette was first reported as 84, which was the raw count of paper scans
    /// offered as if it were the set of choices; the four extra are plain whites
    /// with no colour record, and are base stock. If this ever reads 84 again,
    /// the whites have crept back in.
    /// </remarks>
    private const int GridSize = 5 * 16;

    [Fact]
    public void The_palette_is_the_eighty_colours_the_game_offers()
    {
        if (!Archives(out string root))
        {
            Assert.Skip($"set {RootVariable} to run the palette against the archives");
            return;
        }

        using SdfContentSource source = new(root);
        ImmutableArray<SdfPathEntry> paths = source.Paths().Value;
        using ContentSources content = new(contentRoot: null, sdfRoot: root);

        Result<ImmutableArray<PaperSwatch>> read = PaperPalette.Read(content, paths);
        Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Refusal!.Message);

        ImmutableArray<PaperSwatch> palette = read.Value;
        Assert.Equal(GridSize, palette.Length);

        // Every colour must be applicable, which means naming a paper the
        // archives hold. A swatch that cannot be applied fails only after
        // somebody has chosen it.
        Assert.All(palette, swatch =>
        {
            Assert.StartsWith(PaperPalette.LibraryFolder, swatch.TexturePath, StringComparison.Ordinal);
            Assert.Contains(paths, entry =>
                string.Equals(SdfIndex.NormalizePath(entry.Path), swatch.TexturePath, StringComparison.Ordinal));
        });

        // Names are unique, or two grid cells would look like the same colour.
        Assert.Equal(palette.Length, palette.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());

        // And they are not all one shade, which a broken ARGB parse would give.
        Assert.True(palette.Select(s => s.Hex).Distinct(StringComparer.Ordinal).Count() > 40);
    }

    [Fact]
    public void A_bound_paper_is_recognised_as_a_colour_and_other_art_is_not()
    {
        if (!Archives(out string root))
        {
            Assert.Skip($"set {RootVariable} to run the palette against the archives");
            return;
        }

        using SdfContentSource source = new(root);
        using ContentSources content = new(contentRoot: null, sdfRoot: root);
        ImmutableArray<PaperSwatch> palette = PaperPalette.Read(content, source.Paths().Value).Value;

        Assert.NotNull(PaperPalette.Match(palette, palette[0].TexturePath));

        // The rule a front end depends on: a material binding something that is
        // not a paper is not a colour, and must not be offered a picker.
        Assert.Null(PaperPalette.Match(palette, "camel/baked/assets/textures/tex_white16_d.dds"));
    }

    private static bool Archives(out string root)
    {
        root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        return root.Length > 0;
    }
}

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;
using Perianth.Core.Imaging;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// An item's own colour, applied to the sheet it is drawn on.
/// </summary>
/// <remarks>
/// A hairstyle's paper is near-white — measured at rgb(222,218,219) — and its
/// colour comes entirely from the item's <c>myDefaultTint1</c>, which resolves
/// to a brown. Without it the hair exports almost white.
/// </remarks>
public sealed class CostumeTintConformanceTests
{
    private const string RootVariable = "PERIANTH_SDF_ROOT";

    [Fact]
    public void A_hairstyle_ships_with_a_colour_and_a_costume_does_not()
    {
        string root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {RootVariable} to run the tint table against the archives");
            return;
        }

        using Perianth.Formats.Sdf.SdfContentSource source = new(root);
        ImmutableArray<Perianth.Formats.Sdf.SdfPathEntry> paths = source.Paths().Value;
        using ContentSources content = new(contentRoot: null, sdfRoot: root);

        ImmutableArray<CostumeItem> items = CostumeCatalogue.Read(content, paths).Value;
        ImmutableArray<TintColour> tints = PaperPalette.Tints(content).Value;

        // 117 records, against the 80 the picker's grid shows.
        Assert.InRange(tints.Length, 100, 200);

        // NoTint is a perfectly ordinary grey behind an alpha of nothing, and
        // it is what 585 of the 970 item tints name. Reading the colour and
        // ignoring the alpha paints most of the wardrobe grey.
        TintColour none = tints.First(t => t.Name.EndsWith("NoTint", StringComparison.Ordinal));
        Assert.False(none.Applies);
        Assert.Null(PaperPalette.Tint(tints, none.Uid));

        // Every hairstyle ships with a colour; almost no costume piece does.
        CostumeItem[] hair = [.. items.Where(i => i.Slot == "Hair")];
        int coloured = hair.Count(i => i.Tints.Any(u => PaperPalette.Tint(tints, u) is not null));
        Assert.True(coloured > hair.Length / 2, $"only {coloured} of {hair.Length} hairstyles ship with a colour");

        CostumeItem[] suits = [.. items.Where(i => i.Slot == "Clothes")];
        int tinted = suits.Count(i => i.Tints.Any(u => PaperPalette.Tint(tints, u) is not null));
        Assert.True(tinted < suits.Length / 4, $"{tinted} of {suits.Length} costume bodies ship with a colour");
    }

    [Fact]
    public void The_colour_reaches_the_sheet_and_leaves_the_line_work_alone()
    {
        string root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {RootVariable} to run the tint table against the archives");
            return;
        }

        using Perianth.Formats.Sdf.SdfContentSource source = new(root);
        ImmutableArray<Perianth.Formats.Sdf.SdfPathEntry> paths = source.Paths().Value;
        using ContentSources content = new(contentRoot: null, sdfRoot: root);

        ImmutableArray<CostumeItem> items = CostumeCatalogue.Read(content, paths).Value;
        ImmutableArray<TintColour> tints = PaperPalette.Tints(content).Value;

        CostumeItem hair = items.First(i =>
            i.Slot == "Hair" && i.Tints.Any(u => PaperPalette.Tint(tints, u) is not null));
        TintColour colour = hair.Tints.Select(u => PaperPalette.Tint(tints, u)).First(t => t is not null)!;

        string editordata = Path.ChangeExtension(hair.Default.ModelPath, ".editordata");
        byte[] bytes = content.Read(editordata).Value!;
        EditordataFile file = EditordataReader.Read(SourceFile.FromMemory(editordata, bytes)).Value;

        Rgb white = new(1.0, 1.0, 1.0);
        Rgb wanted = new(colour.Red / 255.0, colour.Green / 255.0, colour.Blue / 255.0);

        int repainted = 0, refused = 0;
        foreach (string texture in MaterialTextures.List(file, hair.Name).Select(t => t.Path).Distinct(StringComparer.Ordinal))
        {
            Result<MaterialEditOutcome> edit = MaterialEdit.Retint(file, texture, white, wanted);
            if (edit.TryGetValue(out MaterialEditOutcome? outcome, out Refusal? refusal))
            {
                repainted += outcome!.Sections;
            }
            else
            {
                // The ordinary outcome for a sheet this variant does not draw,
                // and the caller must be able to tell it from a mistyped path.
                Assert.Equal(DiagnosticIds.MaterialEditMatchedNothing, refusal.DiagnosticId);
                refused++;
            }
        }

        Assert.True(repainted > 0, "the hair colour reached nothing");
        Assert.True(refused > 0, "no sheet was left alone, so the black line work was repainted too");
    }
}

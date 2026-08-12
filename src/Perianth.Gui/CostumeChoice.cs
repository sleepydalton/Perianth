using System.Collections.ObjectModel;
using Perianth.Core.Content;

namespace Perianth.Gui;

/// <summary>
/// One thing that can be worn, as a row in a list.
/// </summary>
/// <remarks>
/// A thin wrapper so a list can show the game's name while the export gets the
/// path. <see cref="Item"/> is null for the "nothing" row, which every slot
/// needs and which is not the absence of a choice but a choice.
/// </remarks>
public sealed class CostumeChoice(CostumeItem? item)
{
    /// <summary>The catalogue entry, or null for "nothing in this slot".</summary>
    public CostumeItem? Item { get; } = item;

    /// <summary>What the list shows.</summary>
    public string Label { get; } = item?.Name ?? "— nothing —";

    public override string ToString() => Label;
}

/// <summary>
/// One colour a chosen piece can have changed.
/// </summary>
/// <remarks>
/// A piece has as many of these as it has papers — six on one costume body,
/// three on a mask — which is more than the game exposes, and they are found by
/// reading the piece rather than configured anywhere.
/// </remarks>
public sealed class CostumeColour
{
    public CostumeColour(string texturePath, int sections, PaperSwatch current)
    {
        System.ArgumentNullException.ThrowIfNull(current);

        TexturePath = texturePath;
        Sections = sections;
        Original = current;
        Swatch = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.FromRgb(current.Red, current.Green, current.Blue));
    }

    /// <summary>The paper this row recolours.</summary>
    public string TexturePath { get; }

    /// <summary>
    /// How much of the piece wears it.
    /// </summary>
    /// <remarks>
    /// Shown, and used to order the rows. 244 sections against 1 is the
    /// difference between the garment and a scrap of trim, and a list ordered
    /// any other way buries the row somebody wants.
    /// </remarks>
    public int Sections { get; }

    /// <summary>The colour the piece ships with.</summary>
    public PaperSwatch Original { get; }

    /// <summary>Every colour on offer for this row.</summary>
    public ObservableCollection<SwatchChoice> Swatches { get; } = [];

    /// <summary>The colour chosen, or null to leave it as it ships.</summary>
    public SwatchChoice? Chosen { get; set; }

    /// <summary>What the row is called.</summary>
    /// <remarks>
    /// The share is said as well as the count, because a count alone does not
    /// tell you which row is the garment. "most of it" and "a few pieces" is
    /// what somebody is actually looking for when they cannot see the model.
    /// </remarks>
    public string Label => $"{Original.Name} — {Share}";

    /// <summary>How much of the piece this colour covers, in words.</summary>
    public string Share
    {
        get
        {
            int share = Total <= 0 ? 0 : (int)System.Math.Round(100.0 * Sections / Total);
            return share >= 60 ? $"most of it ({Sections} parts)"
                : share >= 25 ? $"much of it ({Sections} parts)"
                : Sections == 1 ? "one part"
                : $"{Sections} parts";
        }
    }

    /// <summary>How many coloured parts the whole piece has, for the share.</summary>
    public int Total { get; set; }

    /// <summary>The colour as it ships, for the square beside the row.</summary>
    public Avalonia.Media.IBrush Swatch { get; }
}

/// <summary>
/// One way a piece can be drawn, as a row in a list.
/// </summary>
/// <remarks>
/// A hairstyle ships six models — cut for a bare head, for a cap, for a hat
/// that comes down low — and exactly one is drawn. Which one the game picks is
/// not recorded in its item files, so the choice is offered here rather than
/// guessed at. The labels are the record types' own words, because there is
/// nothing better to call them and inventing names would suggest a meaning
/// nobody has measured.
/// </remarks>
public sealed class VariantChoice(CostumePiece piece)
{
    /// <summary>The model this row draws.</summary>
    public CostumePiece Piece { get; } = piece;

    /// <summary>What the list shows.</summary>
    public string Label { get; } = Name(piece.Kind);

    public override string ToString() => Label;

    /// <summary>The trailing word of the record type, which is what varies.</summary>
    private static string Name(string kind)
    {
        const string Hair = "StreetHair";
        return kind.Length > Hair.Length && kind.StartsWith(Hair, System.StringComparison.Ordinal)
            ? kind[Hair.Length..]
            : kind;
    }
}

using Avalonia.Media;
using Perianth.Core.Content;

namespace Perianth.Gui;

/// <summary>
/// One costume colour, ready to be shown in a list.
/// </summary>
/// <remarks>
/// <para>
/// The colour is drawn rather than described. A list of eighty names is a list
/// nobody can read — "cinnabark" and "chefBrown" mean nothing until you see them
/// — and the game's own picker is a grid of colours for exactly that reason.
/// </para>
/// <para>
/// The brush comes from the game's <c>TintColor</c> record, not from sampling
/// the paper. Those are two different things and the swatch is the right one:
/// it is what the game shows for this colour, so a grid built from it matches
/// the one a player already knows.
/// </para>
/// </remarks>
public sealed class SwatchChoice
{
    public SwatchChoice(PaperSwatch swatch, bool current)
    {
        System.ArgumentNullException.ThrowIfNull(swatch);

        Swatch = swatch;
        Name = swatch.Name;
        Current = current;
        Colour = new SolidColorBrush(Color.FromRgb(swatch.Red, swatch.Green, swatch.Blue));

        // Said rather than only shown, because the one already in use is the
        // thing a person looks for first and colour alone is a poor way to find
        // it among eighty.
        Label = current ? $"{swatch.Name} — in use now" : swatch.Name;
    }

    /// <summary>The palette entry this row came from.</summary>
    public PaperSwatch Swatch { get; }

    /// <summary>The colour's own name, as the game spells it.</summary>
    public string Name { get; }

    /// <summary>What the list shows.</summary>
    public string Label { get; }

    /// <summary>The swatch, for the square beside the name.</summary>
    public IBrush Colour { get; }

    /// <summary>Whether this is the colour the selected material already binds.</summary>
    public bool Current { get; }

    /// <summary>What a dropdown falls back to without a template.</summary>
    public override string ToString() => Label;
}

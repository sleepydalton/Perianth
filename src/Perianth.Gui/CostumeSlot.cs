using System;
using System.Collections.ObjectModel;
using System.Linq;
using Perianth.Core.Content;

namespace Perianth.Gui;

/// <summary>
/// One place something is worn, and what is worn there.
/// </summary>
/// <remarks>
/// The slots are the eight the game's own character screen offers, named as it
/// names them. They come from what the item records hold rather than from a
/// list written here, so a kind this build has never heard of still appears —
/// under its record type, which is a worse name than a menu's and far better
/// than being dropped. Each slot carries its own items and, once one is
/// chosen, that piece's colours.
/// </remarks>
public sealed class CostumeSlot : ViewModelBase
{
    private CostumeChoice? _chosen;
    private VariantChoice? _variant;

    public CostumeSlot(string name)
    {
        Name = name;
        Nothing = new CostumeChoice(null);
        Items.Add(Nothing);
        _chosen = Nothing;
    }

    /// <summary>The slot's name, as the game's own menu names it.</summary>
    public string Name { get; }

    /// <summary>The "nothing here" row, which is a choice rather than its absence.</summary>
    public CostumeChoice Nothing { get; }

    /// <summary>What can be worn here.</summary>
    public ObservableCollection<CostumeChoice> Items { get; } = [];

    /// <summary>The colours of whatever is worn here, once one is chosen.</summary>
    public ObservableCollection<CostumeColour> Colours { get; } = [];

    /// <summary>
    /// The ways the chosen piece can be drawn, when there is more than one.
    /// </summary>
    /// <remarks>
    /// A hairstyle has up to six, cut for different amounts of headwear, and
    /// which one the game draws is not recorded in its files — so it is offered
    /// rather than guessed. Everything else has exactly one and shows nothing.
    /// </remarks>
    public ObservableCollection<VariantChoice> Variants { get; } = [];

    /// <summary>Which way the chosen piece is drawn.</summary>
    public VariantChoice? Variant
    {
        get => _variant;
        set => Set(ref _variant, value);
    }

    /// <summary>Whether this piece can be drawn more than one way.</summary>
    public bool HasVariants => Variants.Count > 1;

    /// <summary>Raised when the choice changes, so the pane can read the piece.</summary>
    public event Action<CostumeSlot>? Changed;

    /// <summary>What is worn here.</summary>
    public CostumeChoice? Chosen
    {
        get => _chosen;
        set
        {
            if (Set(ref _chosen, value))
            {
                Colours.Clear();
                Variants.Clear();
                _variant = null;

                foreach (CostumePiece piece in value?.Item?.Variants ?? [])
                {
                    Variants.Add(new VariantChoice(piece));
                }

                _variant = Variants.FirstOrDefault(
                    v => ReferenceEquals(v.Piece, value?.Item?.Default));

                Raise(nameof(Variants));
                Raise(nameof(Variant));
                Raise(nameof(HasVariants));
                Raise(nameof(HasColours));
                Raise(nameof(IsWorn));
                Changed?.Invoke(this);
            }
        }
    }

    /// <summary>Whether something is worn here.</summary>
    public bool IsWorn => _chosen?.Item is not null;

    /// <summary>Whether this piece has colours to offer.</summary>
    public bool HasColours => Colours.Count > 0;

    /// <summary>Says the colours have arrived, since they are read after the choice.</summary>
    public void ColoursArrived() => Raise(nameof(HasColours));
}

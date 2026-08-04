using System;
using System.Collections.Immutable;
using System.Globalization;

namespace Perianth.Gui;

/// <summary>
/// One facial system, and which of its authored states to show.
/// </summary>
/// <remarks>
/// Four of these rather than four sets of near-identical properties. Each atlas
/// holds a fixed vocabulary — 24 mouths, 11 eyes, 13 pupils, 6 eyebrows — and
/// the state is one-based because that is how the atlas numbers them; only the
/// count differs, so only the count is a parameter.
/// </remarks>
public sealed class FacialChoice : ViewModelBase
{
    private bool _available;
    private bool _busy;
    private int _index;

    public FacialChoice(string label, int states)
    {
        Label = label;
        SurveyCommand = new RelayCommand(() => SurveyRequested?.Invoke(this), () => _available && !_busy);

        // "None" first, so an absent selection is the zero index rather than a
        // separate flag, and so the default is to leave the face alone.
        ImmutableArray<string>.Builder options = ImmutableArray.CreateBuilder<string>(states + 1);
        options.Add("None");
        for (int state = 1; state <= states; state++)
        {
            options.Add(state.ToString(CultureInfo.InvariantCulture));
        }

        Options = options.ToImmutable();
    }

    /// <summary>Raised when the chosen state changes, so the pane can re-check itself.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when every state of this system was asked for.
    /// </summary>
    /// <remarks>
    /// The states are bare numbers and nothing in the data names them, so the
    /// only way to learn what "mouth 12" is for a given character is to look at
    /// it. Exporting the lot is that, once.
    /// </remarks>
    public event Action<FacialChoice>? SurveyRequested;

    /// <summary>Exports one file per state of this system.</summary>
    public RelayCommand SurveyCommand { get; }

    /// <summary>How many states this atlas holds, excluding "None".</summary>
    public int States => Options.Length - 1;

    /// <summary>True while any export is running, so the button waits its turn.</summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (Set(ref _busy, value))
            {
                SurveyCommand.Reconsider();
            }
        }
    }

    public string Label { get; }

    public ImmutableArray<string> Options { get; }

    /// <summary>Whether this model has the atlas at all.</summary>
    public bool Available
    {
        get => _available;
        set
        {
            if (Set(ref _available, value))
            {
                SurveyCommand.Reconsider();
                if (!value)
                {
                    Index = 0;
                }
            }
        }
    }

    /// <summary>The chosen option; zero is "None".</summary>
    public int Index
    {
        get => _index;
        set
        {
            if (Set(ref _index, value))
            {
                Raise(nameof(State));
                Changed?.Invoke();
            }
        }
    }

    /// <summary>The one-based state, or none.</summary>
    public int? State => _index <= 0 ? null : _index;

    /// <summary>Forgets the selection, for when a different model is chosen.</summary>
    public void Clear() => Index = 0;
}

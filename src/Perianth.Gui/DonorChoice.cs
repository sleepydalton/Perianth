using System.Globalization;
using Perianth.Core.Pose;

namespace Perianth.Gui;

/// <summary>
/// One hierarchy offered for a model that has no setup of its own.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are shown rather than only ranked on, because the failure they
/// guard against is invisible in a finished export until someone opens it. The
/// hierarchy naming the most of one model's head was a crowd rig — it poses
/// several characters spread across a scene — and it threw the parts tens of
/// units apart while looking, in a bare list of names, like the obvious choice.
/// </para>
/// <para>
/// So a row says what it draws and whether it agrees with the pose it is being
/// added to. Ordering already puts the disagreeing ones last; saying so is what
/// lets a user understand the order rather than merely follow it.
/// </para>
/// </remarks>
public sealed class DonorChoice
{
    public DonorChoice(DonorCandidate candidate, bool isGapFiller)
    {
        System.ArgumentNullException.ThrowIfNull(candidate);

        Candidate = candidate;
        VirtualPath = candidate.VirtualPath;

        string file = candidate.VirtualPath[(candidate.VirtualPath.LastIndexOf('/') + 1)..];
        Name = file.StartsWith("anm_", System.StringComparison.Ordinal) ? file[4..] : file;
        if (Name.EndsWith(".anim", System.StringComparison.Ordinal))
        {
            Name = Name[..^5];
        }

        if (Name.EndsWith("_setup", System.StringComparison.Ordinal))
        {
            Name = Name[..^6];
        }

        string parts = isGapFiller
            ? string.Create(CultureInfo.InvariantCulture, $"adds {candidate.Adds} part{(candidate.Adds == 1 ? "" : "s")}")
            : string.Create(CultureInfo.InvariantCulture, $"poses {candidate.Poses} part{(candidate.Poses == 1 ? "" : "s")}");

        // Worth saying, and worth saying in the row rather than only at the top:
        // it is the difference between "this one is a guess that scored well"
        // and "the game says this character uses this". It does not replace the
        // warning below, because the game's record has been measured against
        // and is sometimes a developer's placeholder.
        Detail = candidate.Declared
            ? string.Create(CultureInfo.InvariantCulture, $"{parts} — the game names this one")
            : parts;

        // Said of a primary too. An earlier version withheld it there, reasoning
        // that a primary has nothing to agree with -- but it has its rivals, and
        // without the warning a crowd rig sat at the top of the list looking like
        // the best answer because it draws the most.
        Warning = candidate.Disagreement is double apart && apart > DonorSearch.Agreeing
            ? string.Create(CultureInfo.InvariantCulture, $"places shared parts {apart:0.#} away — likely wrong")
            : string.Empty;
    }

    /// <summary>The ranking this row came from.</summary>
    public DonorCandidate Candidate { get; }

    /// <summary>The archive path of the ANIM.</summary>
    public string VirtualPath { get; }

    /// <summary>The character this hierarchy belongs to.</summary>
    public string Name { get; }

    /// <summary>What it does for this model, in parts.</summary>
    public string Detail { get; }

    /// <summary>Why not to pick it, or empty.</summary>
    public string Warning { get; }

    /// <summary>Whether there is a warning to show.</summary>
    public bool HasWarning => Warning.Length > 0;

    /// <summary>What a dropdown shows for this row.</summary>
    public override string ToString() =>
        HasWarning ? $"{Name} — {Detail} — {Warning}" : $"{Name} — {Detail}";
}

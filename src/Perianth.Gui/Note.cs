using System;
using Perianth.Core;
using Perianth.Formats.Diagnostics;

namespace Perianth.Gui;

/// <summary>What a line in the message list is.</summary>
public enum NoteKind
{
    /// <summary>Something finished.</summary>
    Done,

    /// <summary>Something the export does not reproduce, worth knowing before judging the result.</summary>
    Caveat,

    /// <summary>Advice about what you are about to look at.</summary>
    Tip,

    /// <summary>It did not happen.</summary>
    Problem,
}

/// <summary>
/// One thing worth telling the user, at the length they will read.
/// </summary>
/// <remarks>
/// The tool's own diagnostics are written to be complete rather than brief —
/// the material disclosure names every engine input the recovered shader does
/// not reproduce, which is exactly right in a report and a wall of text in a
/// pane. So each one is shown as a sentence with the rest behind it: nothing is
/// abbreviated away, and nothing is shouted either.
/// </remarks>
/// <param name="Kind">Which sort of line this is.</param>
/// <param name="Summary">One sentence.</param>
/// <param name="Detail">The rest, or none.</param>
public sealed record Note(NoteKind Kind, string Summary, string? Detail)
{
    /// <summary>Whether there is more to show.</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>The kind, as the four flags a style can select on.</summary>
    public bool IsDone => Kind == NoteKind.Done;

    public bool IsCaveat => Kind == NoteKind.Caveat;

    public bool IsTip => Kind == NoteKind.Tip;

    public bool IsProblem => Kind == NoteKind.Problem;

    /// <summary>
    /// Turns one of the tool's diagnostics into a line and its detail.
    /// </summary>
    /// <remarks>
    /// The summaries are keyed by the stable diagnostic identifier rather than
    /// by matching words in the message, because the identifier is the part the
    /// specification promises not to change.
    /// </remarks>
    public static Note From(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        string? summary = diagnostic.Id switch
        {
            DiagnosticIds.MaterialApproximated =>
                "Surfaces are a reconstruction of the game's shader, not a copy of it.",
            DiagnosticIds.TransparentMaterialApproximated =>
                "Transparent surfaces are approximated further still.",
            DiagnosticIds.ExportUnposed =>
                "This is the model's whole part list, not a pose — every alternate state is present at once.",
            DiagnosticIds.PrimitiveOmittedBakeTooLarge =>
                "One part was left out: reconciling its texture repeat would have baked an image past the size cap.",
            DiagnosticIds.ExtractionPathNotPortable =>
                "Some extracted paths are longer than Windows accepts by default.",
            DiagnosticIds.ClipHasNoMotion =>
                "The animation chosen holds a pose rather than moving, so the export is a still model.",
            DiagnosticIds.ExtractionCancelled =>
                "Stopped partway. What was written is complete and listed in the manifest.",
            _ => null,
        };

        NoteKind kind = diagnostic.Severity == DiagnosticSeverity.Error ? NoteKind.Problem : NoteKind.Caveat;

        return summary is null
            ? Split(kind, diagnostic.Message)
            : new Note(kind, summary, diagnostic.Message);
    }

    /// <summary>Uses the first sentence as the summary when nothing better is known.</summary>
    public static Note Split(NoteKind kind, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        int stop = message.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 || stop > 160
            ? new Note(kind, message, null)
            : new Note(kind, message[..(stop + 1)], message[(stop + 2)..]);
    }
}

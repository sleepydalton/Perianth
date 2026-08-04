using System.Collections.Immutable;

namespace Perianth.Core;

/// <summary>How much a diagnostic matters.</summary>
public enum DiagnosticSeverity
{
    /// <summary>The export continued.</summary>
    Warning,

    /// <summary>The export did not happen.</summary>
    Error,
}

/// <summary>
/// One structured thing worth telling the caller.
/// </summary>
/// <remarks>
/// Specification section 12.1 asks for stable lower-snake-case identifiers that
/// never embed a path, an ordinal or prose, so that a caller can branch on the
/// identifier while a person reads the message. Adding context to a diagnostic
/// must not change its identifier.
/// </remarks>
/// <param name="Id">A stable identifier.</param>
/// <param name="Severity">Whether the export continued.</param>
/// <param name="Message">Prose for a person.</param>
public sealed record Diagnostic(string Id, DiagnosticSeverity Severity, string Message);

/// <summary>What was published, counted.</summary>
/// <param name="Meshes">Meshes in the output.</param>
/// <param name="Vertices">Vertices across every mesh.</param>
/// <param name="Triangles">Triangles across every mesh.</param>
public readonly record struct ExportCounts(int Meshes, int Vertices, int Triangles);

/// <summary>
/// What a published audio sidecar reports.
/// </summary>
/// <param name="Output">The WAV path written beside the GLB.</param>
/// <param name="Source">The WEM file the audio came from.</param>
/// <param name="Locale">The voice locale, or empty.</param>
/// <param name="Channels">Interleaved channel count.</param>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="SampleCount">Frames.</param>
/// <param name="DurationSeconds">Clip length.</param>
/// <param name="LipsyncEndSeconds">The schedule's final key time in seconds, when a schedule drove the mouth.</param>
/// <param name="LipsyncDeltaSeconds">Audio duration minus the lip-sync endpoint, when both are known.</param>
public sealed record AudioReport(
    string Output,
    string Source,
    string Locale,
    int Channels,
    int SampleRate,
    int SampleCount,
    double DurationSeconds,
    double? LipsyncEndSeconds,
    double? LipsyncDeltaSeconds);

/// <summary>
/// The result of a successful export, which both front ends consume.
/// </summary>
/// <param name="Counts">What was published.</param>
/// <param name="Diagnostics">Everything worth reporting, in the order it arose.</param>
/// <param name="PartialExport">
/// True only when publication succeeded with at least one explicit primitive
/// omission. Approximations and ordinary warnings alone do not make an export
/// partial.
/// </param>
/// <param name="Audio">The audio sidecar written, or none.</param>
public sealed record ExportOutcome(
    ExportCounts Counts,
    ImmutableArray<Diagnostic> Diagnostics,
    bool PartialExport,
    AudioReport? Audio = null);

using System;
using System.Runtime.CompilerServices;

namespace Perianth.Formats.Diagnostics;

/// <summary>
/// A refusal to export, carried as a value rather than thrown. Exceptions in
/// this codebase mean a fault — a bug, or an I/O failure the process cannot
/// proceed through. "This input cannot be exported" is a normal, expected,
/// documented outcome and one of the tool's main products, so it travels in the
/// return type where a caller cannot overlook it and a user interface can
/// render it rather than catch it.
/// </summary>
/// <param name="Kind">Whether the file, the request, or capacity is at fault.</param>
/// <param name="DiagnosticId">A stable identifier from <see cref="DiagnosticIds"/>.</param>
/// <param name="Message">Prose for a human. Never the only channel: the kind and identifier carry the meaning.</param>
public sealed record Refusal(RefusalKind Kind, string DiagnosticId, string Message)
{
    /// <summary>A stable identifier from <see cref="DiagnosticIds"/>.</summary>
    public string DiagnosticId { get; } = Required(DiagnosticId);

    /// <summary>Prose for a human.</summary>
    public string Message { get; } = Required(Message);

    /// <summary>The bytes contradict a grammar.</summary>
    public static Refusal Malformed(string message, string diagnosticId = DiagnosticIds.InputMalformed) =>
        new(RefusalKind.Malformed, diagnosticId, message);

    /// <summary>A coherent but unimplemented mode, or a request the data cannot satisfy.</summary>
    public static Refusal Unsupported(string message, string diagnosticId = DiagnosticIds.FormatUnsupported) =>
        new(RefusalKind.Unsupported, diagnosticId, message);

    /// <summary>Capacity was exhausted.</summary>
    public static Refusal Resource(string message, string diagnosticId = DiagnosticIds.ResourceInsufficient) =>
        new(RefusalKind.Resource, diagnosticId, message);

    // A refusal that reaches a user with nothing to read is a defect in this
    // code, not a property of their file, so it is a fault and it throws.
    private static string Required(string value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}

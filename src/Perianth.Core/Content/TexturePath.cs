using System;
using System.Collections.Generic;
using System.Globalization;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Content;

/// <summary>
/// Canonicalizes and validates one serialized texture path.
/// </summary>
/// <remarks>
/// Shared by every content source, so a path is judged the same way whether the
/// bytes come from a directory tree or an archive. This is the resolution
/// layer's rule, distinct from the SDF index's own case-insensitive comparison:
/// the archive folds case because the container does, and that is not
/// permission to fold a loose filesystem path.
/// </remarks>
public static class TexturePath
{
    private const string DdsSuffix = ".dds";

    /// <summary>
    /// Normalizes <paramref name="serialized"/> and refuses anything the
    /// resolution rule does not accept.
    /// </summary>
    /// <remarks>
    /// Separators become forward slashes and <c>.dds</c> is appended only when
    /// the path carries no suffix at all. A path with some other suffix is
    /// refused rather than corrected: it names something this build does not
    /// read, and appending to it would invent a file.
    /// </remarks>
    /// <param name="serialized">The path exactly as the editordata spelled it.</param>
    /// <param name="channel">The channel it came from, for the diagnostic.</param>
    public static Result<string> Normalize(string serialized, string channel)
    {
        ArgumentNullException.ThrowIfNull(serialized);
        ArgumentNullException.ThrowIfNull(channel);

        string normalized = serialized.Replace('\\', '/');

        int lastSlash = normalized.LastIndexOf('/');
        string last = lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
        int dot = last.LastIndexOf('.');

        if (dot < 0)
        {
            normalized += DdsSuffix;
        }
        else if (!last[dot..].Equals(DdsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The {channel} texture {normalized} is not a DDS image."));
        }

        if (normalized.Length == 0 || normalized.StartsWith('/'))
        {
            return Invalid(channel, normalized);
        }

        foreach (string component in normalized.Split('/'))
        {
            // An empty component is a doubled separator or a trailing one; the
            // two dot forms are traversal; a colon is a drive or stream
            // specifier. None can name a file beneath a root.
            if (component.Length == 0 ||
                component == "." ||
                component == ".." ||
                component.Contains(':', StringComparison.Ordinal))
            {
                return Invalid(channel, normalized);
            }
        }

        return Result.Ok(normalized);
    }

    /// <summary>Splits a normalized path into its components.</summary>
    internal static IReadOnlyList<string> Components(string normalized) =>
        normalized.Split('/');

    private static Refusal Invalid(string channel, string normalized) =>
        Refusal.Malformed(string.Create(
            CultureInfo.InvariantCulture,
            $"The {channel} texture path '{normalized}' is invalid."));
}

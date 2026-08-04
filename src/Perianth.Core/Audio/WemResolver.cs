using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Audio;

/// <summary>
/// Resolves one speech WEM by numeric ID, confirming its embedded Oasis label.
/// </summary>
/// <remarks>
/// A numeric speech ID names a file <c>&lt;id&gt;.wem</c> somewhere under an
/// already-extracted tree, but a bare name match is not enough: the bytes must
/// carry the NUL-terminated <c>OasisID&lt;id&gt;</c> label, so an unrelated file
/// that happens to share the number is rejected. When several confirmed files
/// remain, the unique <c>voice/windows/english(us)</c> variant is preferred, and
/// anything still ambiguous refuses rather than guessing. The tool never searches
/// or unpacks Wwise banks — the tree is expected already extracted.
/// </remarks>
public static class WemResolver
{
    /// <summary>Resolves the WEM for <paramref name="speechId"/> under <paramref name="root"/>.</summary>
    public static Result<WemSelection> Resolve(string root, string speechId)
    {
        System.ArgumentNullException.ThrowIfNull(root);
        System.ArgumentNullException.ThrowIfNull(speechId);

        if (!IsNumericOasisId(speechId))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"Speech ID '{speechId}' is not a numeric Oasis ID."));
        }

        if (!Directory.Exists(root))
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture, $"The WEM root {root} is not a directory."));
        }

        List<string> candidates;
        try
        {
            candidates =
            [
                .. Directory.EnumerateFiles(root, "*.wem", SearchOption.AllDirectories)
                    .Where(path => string.Equals(
                        Path.GetFileNameWithoutExtension(path), speechId, System.StringComparison.Ordinal))
                    .OrderBy(path => path, System.StringComparer.Ordinal),
            ];
        }
        catch (System.Exception ex) when (ex is IOException or System.UnauthorizedAccessException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture, $"Cannot scan WEM root {root}: {ex.Message}"));
        }

        if (candidates.Count == 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"No extracted WEM matches speech ID '{speechId}'."));
        }

        byte[] label = System.Text.Encoding.ASCII.GetBytes($"OasisID{speechId}\0");
        List<string> confirmed = [];
        foreach (string path in candidates)
        {
            Result<bool> carries = CarriesLabel(path, label);
            if (!carries.TryGetValue(out bool ok, out Refusal? refusal))
            {
                return refusal;
            }

            if (ok)
            {
                confirmed.Add(path);
            }
        }

        if (confirmed.Count == 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Numeric WEM candidates for speech ID '{speechId}' lack its embedded Oasis label."));
        }

        List<string> english = [.. confirmed.Where(path => string.Equals(
            Locale(path), "english(us)", System.StringComparison.OrdinalIgnoreCase))];
        List<string> choices = english.Count > 0 ? english : confirmed;

        if (choices.Count != 1)
        {
            string locales = string.Join(", ", choices
                .Select(path => Locale(path) is { Length: > 0 } value ? value : "unlabelled")
                .Distinct()
                .OrderBy(value => value, System.StringComparer.Ordinal));
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Speech ID '{speechId}' has ambiguous confirmed WEM variants: {locales}."));
        }

        return Result.Ok(new WemSelection(choices[0], Locale(choices[0])));
    }

    /// <summary>The voice locale segment of a path: the folder two below a <c>voice/windows</c> pair.</summary>
    public static string Locale(string path)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], "voice", System.StringComparison.OrdinalIgnoreCase)
                && i + 2 < parts.Length
                && string.Equals(parts[i + 1], "windows", System.StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 2];
            }
        }

        return string.Empty;
    }

    private static bool IsNumericOasisId(string speechId) =>
        speechId.Length > 0 && speechId.All(c => c is >= '0' and <= '9');

    private static Result<bool> CarriesLabel(string path, byte[] label)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Result.Ok(Contains(bytes, label));
        }
        catch (System.Exception ex) when (ex is IOException or System.UnauthorizedAccessException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture, $"Cannot read WEM candidate {path}: {ex.Message}"));
        }
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        needle.Length > 0 && haystack.AsSpan().IndexOf(needle.AsSpan()) >= 0;
}

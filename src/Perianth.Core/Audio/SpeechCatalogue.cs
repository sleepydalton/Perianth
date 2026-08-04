using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Core.Audio;

/// <summary>What the archives hold for one speech ID.</summary>
/// <param name="SpeechId">The numeric ID asked for.</param>
/// <param name="Wem">The voice file in the chosen locale, or none.</param>
/// <param name="Locales">Every locale that carries this line, ordered.</param>
public sealed record SpeechAudio(string SpeechId, string? Wem, ImmutableArray<string> Locales);

/// <summary>
/// Finds the voice file for a speech ID.
/// </summary>
/// <remarks>
/// <para>
/// The archives hold 245,896 <c>.wem</c> files addressed by numeric ID alone,
/// spread across seven full locales of about 34,467 lines each and a dozen
/// smaller variant sets. Nothing in a character's files names an ID, so the
/// caller supplies one; this only answers whether it exists and where.
/// </para>
/// <para>
/// The locale is the caller's choice rather than a guess. A line missing from
/// the chosen locale but present in others says so, because "no audio" and
/// "not in that language" are different answers.
/// </para>
/// </remarks>
public static class SpeechCatalogue
{
    private const string VoiceRoot = "camel/voice/";

    /// <summary>The locale most callers want, when they express no preference.</summary>
    public const string DefaultLocale = "english(us)";

    /// <summary>
    /// The locales that carry a full set of lines.
    /// </summary>
    /// <remarks>
    /// Measured: seven folders hold about 34,467 files each, and the rest hold
    /// a few hundred — those are variant reads (female, neutral) rather than
    /// languages, and offering them as if they were complete would mislead.
    /// </remarks>
    public static ImmutableArray<string> Locales { get; } =
    [
        "english(us)",
        "french(france)",
        "german",
        "italian",
        "portuguese(brazil)",
        "spanish(mexico)",
        "spanish(spain)",
    ];

    /// <summary>
    /// Finds <paramref name="speechId"/>'s voice file in <paramref name="locale"/>.
    /// </summary>
    public static Result<SpeechAudio> Find(
        ImmutableArray<SdfPathEntry> paths,
        string speechId,
        string locale)
    {
        ArgumentNullException.ThrowIfNull(speechId);
        ArgumentNullException.ThrowIfNull(locale);

        string wanted = speechId.Trim();
        if (wanted.Length == 0 || !IsNumeric(wanted))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A speech ID is a number, and '{speechId}' is not one."));
        }

        string suffix = "/" + wanted + ".wem";
        SortedSet<string> locales = new(StringComparer.Ordinal);
        string? chosen = null;

        foreach (SdfPathEntry entry in paths)
        {
            string path = entry.Path;
            if (!path.EndsWith(suffix, StringComparison.Ordinal) ||
                !path.StartsWith(VoiceRoot, StringComparison.Ordinal))
            {
                continue;
            }

            string spoken = LocaleOf(path);
            if (spoken.Length > 0)
            {
                locales.Add(spoken);
            }

            if (string.Equals(spoken, locale, StringComparison.Ordinal))
            {
                chosen = path;
            }
        }

        return Result.Ok(new SpeechAudio(wanted, chosen, [.. locales]));
    }

    /// <summary>The locale folder a voice path sits under, or empty.</summary>
    private static string LocaleOf(string path)
    {
        // camel/voice/<platform>/<locale>/...
        string[] parts = path.Split('/');
        return parts.Length > 3 ? parts[3] : string.Empty;
    }

    private static bool IsNumeric(string text)
    {
        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}

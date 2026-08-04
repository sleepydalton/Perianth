using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Audio;

/// <summary>One spoken line: what is said, and the ID that plays it.</summary>
/// <param name="SpeechId">The numeric ID the exporter takes.</param>
/// <param name="Text">The subtitle, with its timing markup removed.</param>
public sealed record SpokenLine(string SpeechId, string Text);

/// <summary>
/// Finding a voice line by what it says.
/// </summary>
/// <remarks>
/// <para>
/// Roadmap §6.7 measured two forms of the character-to-speech link and
/// recommended building neither, which left a speech ID as a number to guess.
/// It is the wrong question: nothing names who speaks a line, but the
/// localization packages hold <em>what</em> every line says, keyed by the same
/// Oasis GUID the identifier table maps to a speech ID.
/// </para>
/// <para>
/// Joined over the shipped archive: 28,888 subtitles and 5,585 barks, every one
/// of them resolving to a speech ID, and 31,865 of those carrying a lip-sync
/// schedule — the whole usable population. So a line is found by typing what is
/// said, which is how a person would look for one anyway.
/// </para>
/// </remarks>
public sealed class SubtitleCatalogue
{
    private readonly ImmutableArray<SpokenLine> _lines;
    private readonly string[] _folded;

    private SubtitleCatalogue(ImmutableArray<SpokenLine> lines)
    {
        _lines = lines;
        _folded = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            _folded[i] = lines[i].Text.ToLowerInvariant();
        }
    }

    /// <summary>How many lines can be searched.</summary>
    public int Count => _lines.Length;

    /// <summary>
    /// Builds a catalogue from the identifier table and one or more subtitle
    /// packages.
    /// </summary>
    /// <param name="oasisIds">The <c>oasisids.txt</c> bytes: GUID to speech ID.</param>
    /// <param name="packages">The <c>.locpack</c> bytes: GUID to text.</param>
    public static Result<SubtitleCatalogue> Read(
        ReadOnlyMemory<byte> oasisIds,
        ImmutableArray<ReadOnlyMemory<byte>> packages)
    {
        Dictionary<string, string> byGuid = new(StringComparer.Ordinal);

        foreach (string[] row in Rows(oasisIds))
        {
            if (row.Length >= 2 && IsGuid(row[0]) && row[1].Length > 0 && IsDigits(row[1]))
            {
                byGuid[row[0].ToLowerInvariant()] = row[1];
            }
        }

        if (byGuid.Count == 0)
        {
            return Refusal.Malformed("The Oasis identifier table holds no usable rows.");
        }

        // Ordered by speech ID so the same corpus always searches the same way,
        // and deduplicated because a line can appear in more than one package.
        SortedDictionary<int, SpokenLine> found = [];

        foreach (ReadOnlyMemory<byte> package in packages)
        {
            foreach (string[] row in Rows(package))
            {
                if (row.Length < 3 || !IsGuid(row[0]))
                {
                    continue;
                }

                if (!byGuid.TryGetValue(row[0].ToLowerInvariant(), out string? speechId))
                {
                    continue;
                }

                string text = Clean(row[2]);
                if (text.Length > 0 && int.TryParse(speechId, out int ordinal))
                {
                    found[ordinal] = new SpokenLine(speechId, text);
                }
            }
        }

        return Result.Ok(new SubtitleCatalogue([.. found.Values]));
    }

    /// <summary>
    /// The lines containing <paramref name="text"/>, best first.
    /// </summary>
    /// <remarks>
    /// A line that starts with what was typed comes before one that merely
    /// contains it, and a shorter line before a longer one: someone searching
    /// "screw you" wants the line that is that, not the speech it appears in.
    /// </remarks>
    public Result<ImmutableArray<SpokenLine>> Search(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);

        string wanted = text.Trim().ToLowerInvariant();
        if (wanted.Length == 0)
        {
            return Refusal.Unsupported("A search needs some words to look for.");
        }

        List<SpokenLine> hits = [];
        List<int> ranks = [];

        for (int i = 0; i < _folded.Length; i++)
        {
            int at = _folded[i].IndexOf(wanted, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            hits.Add(_lines[i]);
            ranks.Add(at == 0 ? _folded[i].Length : _folded[i].Length + 100_000);
        }

        int[] order = [.. Enumerable(hits.Count)];
        System.Array.Sort(order, (left, right) =>
            ranks[left] != ranks[right]
                ? ranks[left].CompareTo(ranks[right])
                : string.CompareOrdinal(hits[left].SpeechId, hits[right].SpeechId));

        ImmutableArray<SpokenLine>.Builder best = ImmutableArray.CreateBuilder<SpokenLine>();
        foreach (int index in order)
        {
            if (limit > 0 && best.Count == limit)
            {
                break;
            }

            best.Add(hits[index]);
        }

        return Result.Ok(best.ToImmutable());
    }

    /// <summary>The line for one speech ID, if the packages carry it.</summary>
    public SpokenLine? Line(string speechId)
    {
        foreach (SpokenLine line in _lines)
        {
            if (string.Equals(line.SpeechId, speechId, StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }

    private static IEnumerable<int> Enumerable(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return i;
        }
    }

    /// <summary>
    /// Splits the comma-separated rows, honouring quoted fields.
    /// </summary>
    /// <remarks>
    /// The subtitle text contains commas and doubled quotes of its own — the
    /// timing markup is written <c>&lt;split time=""0.61""&gt;</c> — so the rows
    /// cannot be split on commas alone.
    /// </remarks>
    private static IEnumerable<string[]> Rows(ReadOnlyMemory<byte> bytes)
    {
        string text = Encoding.UTF8.GetString(bytes.Span);
        List<string> fields = [];
        StringBuilder field = new();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;

                case '\n':
                    fields.Add(field.ToString().TrimEnd('\r'));
                    field.Clear();
                    yield return [.. fields];
                    fields.Clear();
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString().TrimEnd('\r'));
            yield return [.. fields];
        }
    }

    /// <summary>Removes the timing markup a subtitle carries for the game's own use.</summary>
    private static string Clean(string text)
    {
        StringBuilder clean = new(text.Length);
        bool inTag = false;

        foreach (char c in text)
        {
            if (c == '<')
            {
                inTag = true;
            }
            else if (c == '>')
            {
                inTag = false;
            }
            else if (!inTag)
            {
                clean.Append(c);
            }
        }

        return clean.ToString().Trim();
    }

    private static bool IsGuid(string text)
    {
        if (text.Length != 32)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigits(string text)
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

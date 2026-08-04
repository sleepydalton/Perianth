using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Core.Content;

/// <summary>
/// The archive's paths, prepared for being searched over and over.
/// </summary>
/// <remarks>
/// <para>
/// A search typed one letter at a time asks the same question of the same
/// half-million paths on every keystroke, and two thirds of the cost of
/// answering it is work that need not be repeated. Measured over the shipped
/// index, 486,543 paths:
/// </para>
/// <list type="bullet">
/// <item>normalizing every path per query — <b>289 ms</b>, against 12 ms when
/// it is done once up front;</item>
/// <item>sorting every match to show a few — <b>~1,800 ms</b> for a query
/// matching 486,408 of them.</item>
/// </list>
/// <para>
/// Hence this: normalize once at construction, and keep only as many results as
/// were asked for rather than ordering all of them. A one-shot caller wanting
/// everything still has <see cref="ArchiveExtraction.Find"/>, which is this with
/// no limit.
/// </para>
/// </remarks>
public sealed class ArchiveSearch
{
    private readonly ImmutableArray<SdfPathEntry> _entries;
    private readonly string[] _normalized;

    /// <summary>Prepares <paramref name="paths"/> for repeated searching.</summary>
    public ArchiveSearch(ImmutableArray<SdfPathEntry> paths)
    {
        _entries = paths.IsDefault ? [] : paths;
        _normalized = new string[_entries.Length];

        for (int i = 0; i < _entries.Length; i++)
        {
            _normalized[i] = SdfIndex.NormalizePath(_entries[i].Path);
        }
    }

    /// <summary>How many paths are searched.</summary>
    public int Count => _entries.Length;

    /// <summary>
    /// The best <paramref name="limit"/> matches for <paramref name="text"/>,
    /// and how many matched in total.
    /// </summary>
    /// <param name="limit">How many to return; zero or less returns all of them.</param>
    /// <remarks>
    /// Ordered by what the caller likely meant rather than alphabetically — see
    /// <see cref="ArchiveExtraction.Find"/> for why that distinction is not
    /// cosmetic. The total is reported separately because a caller showing the
    /// best few has to be able to say how many it is not showing.
    /// </remarks>
    public Result<(ImmutableArray<SdfPathEntry> Best, int Total)> Best(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);

        string wanted = SdfIndex.NormalizePath(text);
        if (wanted.Length == 0)
        {
            return Refusal.Unsupported("A search needs some text to look for.");
        }

        // Keeping the worst of the best at the front, so the one to drop when a
        // better match arrives is the one already in hand. O(n log limit)
        // instead of ordering every match to discard nearly all of them.
        PriorityQueue<SdfPathEntry, SdfPathEntry> kept = new(new Worst(wanted));
        List<SdfPathEntry>? everything = limit > 0 ? null : [];
        int total = 0;

        for (int i = 0; i < _normalized.Length; i++)
        {
            if (!_normalized[i].Contains(wanted, StringComparison.Ordinal))
            {
                continue;
            }

            total++;
            SdfPathEntry entry = _entries[i];

            if (everything is not null)
            {
                everything.Add(entry);
                continue;
            }

            if (kept.Count < limit)
            {
                kept.Enqueue(entry, entry);
            }
            else if (Rank.Compare(entry.Path, kept.Peek().Path, wanted) < 0)
            {
                kept.EnqueueDequeue(entry, entry);
            }
        }

        List<SdfPathEntry> best = everything ?? [];
        while (kept.Count > 0)
        {
            best.Add(kept.Dequeue());
        }

        best.Sort((left, right) => Rank.Compare(left.Path, right.Path, wanted));
        return Result.Ok((ImmutableArray.CreateRange(best), total));
    }

    /// <summary>Orders the least wanted first, so it is the one dropped.</summary>
    private sealed class Worst(string wanted) : IComparer<SdfPathEntry>
    {
        public int Compare(SdfPathEntry x, SdfPathEntry y) => Rank.Compare(y.Path, x.Path, wanted);
    }
}

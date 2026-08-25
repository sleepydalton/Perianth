using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Perianth.Formats.Bvm;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Core.Content;

/// <summary>One asset a graph object names, and how many times it names it.</summary>
/// <param name="Value">The string table entry.</param>
/// <param name="Uses">How many values in the graph reference it.</param>
public readonly record struct GraphString(string Value, int Uses);

/// <summary>A graph object with some of the assets it names changed.</summary>
/// <param name="Bytes">The edited container, ready to write.</param>
/// <param name="Repointed">Each old path and what it became.</param>
public sealed record GraphEdited(
    ReadOnlyMemory<byte> Bytes, ImmutableArray<(string From, string To)> Repointed);

/// <summary>
/// Points a graph object at different assets.
/// </summary>
/// <remarks>
/// <para>
/// A graph object is what stands between a thing in the world and the files it
/// draws: an actor's names its model, its animation system, its shader and its
/// extra data, and a prop's names the same kinds of thing (Roadmap §10.87,
/// §10.97). So <b>making a new one is a string-table substitution</b> — copy a
/// shipped graph object and change the paths — and the graph itself is not
/// touched at all, because a string's <em>content</em> changes while its index
/// does not.
/// </para>
/// <para>
/// That is why this is small and why it needed <c>BvmWriter</c>: nothing else
/// could put the container back. It is also why it works for actors and props
/// alike, and why there is one operation here rather than one per kind of thing
/// a graph object might describe.
/// </para>
/// <para>
/// <b>Repointing matches a whole entry, not a substring.</b> A table holds bare
/// names as well as paths — an actor's <c>assetname</c> sits beside its
/// <c>.mmb</c> — and a substring rule would rewrite a name while meaning to
/// rewrite a path. It also refuses when a move matched nothing, because a
/// mistyped path that quietly wrote an unchanged file produces a mod
/// indistinguishable from a working one.
/// </para>
/// </remarks>
public static class GraphEdit
{
    /// <summary>Everything a graph object names, with how often each is used.</summary>
    /// <remarks>
    /// Listed rather than guessed at. A shipped actor names 78 strings, of which
    /// perhaps five are the assets somebody wants to change and the rest are node
    /// types, pin names and editor bookkeeping — so choosing what to repoint
    /// needs the list in front of it.
    /// </remarks>
    public static Result<ImmutableArray<GraphString>> List(SourceFile graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        Result<BvmDocument> read = BvmReader.ReadDocument(graph);
        if (!read.TryGetValue(out BvmDocument? document, out Refusal? refusal))
        {
            return refusal;
        }

        int[] uses = new int[document.Strings.Length];
        Count(document.Graph, uses);

        ImmutableArray<GraphString>.Builder found =
            ImmutableArray.CreateBuilder<GraphString>(document.Strings.Length);

        for (int i = 0; i < document.Strings.Length; i++)
        {
            found.Add(new GraphString(document.Strings[i], uses[i]));
        }

        return Result.Ok(found.MoveToImmutable());
    }

    /// <summary>
    /// The one entry ending in a given extension, or a refusal saying why there
    /// is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What lets a caller say "point this at my model" instead of naming the
    /// path it is replacing. It is safe because the corpus says so: of 1,250
    /// actor graph objects, <b>1,236 name exactly one <c>.mmb</c></b> and 1,229
    /// name exactly one <c>.manimsys</c>.
    /// </para>
    /// <para>
    /// The rest refuse rather than guess. Eleven actors name two models, and
    /// picking either would be a coin toss that produces a character drawing
    /// half of what was meant — so those are told to name the path outright,
    /// which the general operation has always allowed.
    /// </para>
    /// </remarks>
    public static Result<string> Sole(SourceFile graph, string extension)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(extension);

        Result<ImmutableArray<GraphString>> listed = List(graph);
        if (!listed.TryGetValue(out ImmutableArray<GraphString> strings, out Refusal? refusal))
        {
            return refusal;
        }

        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (GraphString entry in strings)
        {
            if (entry.Value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                named.Add(entry.Value);
            }
        }

        if (named.Count == 1)
        {
            foreach (string only in named)
            {
                return Result.Ok(only);
            }
        }

        return Refusal.Unsupported(named.Count == 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"The graph object names no {extension} at all, so there is none to replace. Name the entry to move outright.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"The graph object names {named.Count} different {extension} entries, and choosing one would be a guess. Name the entry to move outright."));
    }

    /// <summary>Rewrites the string table so the graph names different assets.</summary>
    /// <param name="graph">A <c>.mgraphobject</c> to copy.</param>
    /// <param name="moves">Each entry to replace, and what to replace it with.</param>
    public static Result<GraphEdited> Repoint(
        SourceFile graph, IReadOnlyList<(string From, string To)> moves)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(moves);

        if (moves.Count == 0)
        {
            return Refusal.Unsupported("Repointing needs at least one path to move.");
        }

        Result<BvmDocument> read = BvmReader.ReadDocument(graph);
        if (!read.TryGetValue(out BvmDocument? document, out Refusal? refusal))
        {
            return refusal;
        }

        string[] table = [.. document.Strings];
        ImmutableArray<(string, string)>.Builder done =
            ImmutableArray.CreateBuilder<(string, string)>(moves.Count);

        foreach ((string from, string to) in moves)
        {
            if (from is null || to is null)
            {
                return Refusal.Unsupported("A move needs both a path to replace and one to replace it with.");
            }

            if (from.Length == 0)
            {
                return Refusal.Unsupported("An empty entry names nothing, so it cannot be moved.");
            }

            int matched = 0;
            for (int i = 0; i < table.Length; i++)
            {
                // Ordinal, and the whole entry. The archives are lower-case and
                // shipped tables are not, so a case-insensitive match would let
                // two entries differing only in case collapse onto one path.
                if (table[i].Equals(from, StringComparison.Ordinal))
                {
                    table[i] = to;
                    matched++;
                }
            }

            if (matched == 0)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The graph object names nothing called '{from}', so repointing it would write an unchanged file."));
            }

            done.Add((from, to));
        }

        Result<byte[]> written = BvmWriter.Write(new BvmDocument([.. table], document.Graph));
        return written.TryGetValue(out byte[]? bytes, out Refusal? failed)
            ? Result.Ok(new GraphEdited(bytes, done.MoveToImmutable()))
            : failed;
    }

    private static void Count(BvmValue value, int[] uses)
    {
        switch (value)
        {
            case BvmString reference:
                if (reference.Index >= 0 && reference.Index < uses.Length)
                {
                    uses[reference.Index]++;
                }

                break;

            case BvmContainer container:
                foreach (BvmValue item in container.Items)
                {
                    Count(item, uses);
                }

                foreach (BvmPair pair in container.Entries)
                {
                    Count(pair.Key, uses);
                    Count(pair.Value, uses);
                }

                break;

            default:
                break;
        }
    }
}

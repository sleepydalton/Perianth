using System;
using System.Collections.Immutable;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Mmb;

/// <summary>
/// Everything an MMB file said: its header, its node table, and its model parts.
/// </summary>
/// <remarks>
/// A transcript rather than a summary, which is what makes a writer possible.
/// The file's whole extent is accounted for — measured over 2,283 files, the
/// payloads begin exactly where the part table ends, follow part order, are
/// gapless and non-overlapping, and end exactly at the file end — so nothing is
/// carried as an unmodelled remainder.
/// </remarks>
public sealed class MmbModel
{
    internal MmbModel(
        string path,
        int version,
        int headerFlags,
        uint declaredLength,
        ImmutableArray<MmbNode> nodes,
        ImmutableArray<MmbModelPart> parts)
    {
        Path = path;
        Version = version;
        HeaderFlags = headerFlags;
        DeclaredLength = declaredLength;
        Nodes = nodes;
        Parts = parts;
    }

    /// <summary>The path as the caller supplied it.</summary>
    public string Path { get; }

    /// <summary>The magic's low six bits. Versions 9 and 11 ship.</summary>
    /// <remarks>
    /// It gates the part grammar throughout, which is why a writer proven on
    /// one version proves nothing about another.
    /// </remarks>
    public int Version { get; }

    /// <summary>The magic's top two bits, uninterpreted. 0, 2 and 3 are seen.</summary>
    public int HeaderFlags { get; }

    /// <summary>
    /// The word after the magic, which is the file's own length on every file
    /// measured.
    /// </summary>
    /// <remarks>
    /// Kept as read rather than recomputed on the way in. A writer sets it from
    /// the length it actually produced, so a file whose header disagreed with
    /// itself would be corrected rather than reproduced — and the round-trip
    /// oracle would say so.
    /// </remarks>
    public uint DeclaredLength { get; }

    /// <summary>The node table, in file order.</summary>
    public ImmutableArray<MmbNode> Nodes { get; }

    /// <summary>The model parts, in the order they appear in the file.</summary>
    public ImmutableArray<MmbModelPart> Parts { get; }

    /// <summary>The same model with its parts replaced, one for one.</summary>
    /// <remarks>
    /// The count must match, because a part's ordinal is what pairs it with a
    /// cameldata constant — Roadmap §5.1 — and a model that gained or lost one
    /// would pair every later part with the wrong constant.
    /// </remarks>
    public Result<MmbModel> WithParts(ImmutableArray<MmbModelPart> parts)
    {
        if (parts.Length != Parts.Length)
        {
            return Refusal.Unsupported(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"A replacement supplied {parts.Length} parts for a model of {Parts.Length}. A part's ordinal pairs it with a constant, so the count cannot change."));
        }

        return Result.Ok(new MmbModel(
            Path, Version, HeaderFlags, DeclaredLength, Nodes, parts));
    }

    /// <summary>The same model with one more part, appended after the last.</summary>
    /// <remarks>
    /// <para>
    /// The other half of a paired insertion: the cameldata gains a constant
    /// through <c>Mode3Cameldata.WithAppendedRecord</c> and the model gains a
    /// part here, and the two are only correct together. Appending rather than
    /// inserting for the same reason — a part's ordinal pairs it with a constant,
    /// so a part added anywhere but the end re-pairs every part after it.
    /// </para>
    /// <para>
    /// <see cref="WithParts"/> still refuses a changed count, and deliberately.
    /// Growing a model is a different operation with a different precondition,
    /// not a relaxation of that one, and every existing caller of
    /// <see cref="WithParts"/> means the one-for-one replacement it asks for.
    /// </para>
    /// <para>
    /// The part's binding node is checked here rather than trusted. A label is a
    /// Maya path and its first segment names a node in <b>this model's own</b>
    /// table — 64,103 of 64,103 parts, Roadmap §10.74 — so a part naming a node
    /// the model does not declare would hang off nothing. That is a request the
    /// data cannot satisfy, and it refuses.
    /// </para>
    /// </remarks>
    public Result<MmbModel> WithAppendedPart(MmbModelPart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        string binding = part.BindingNode;
        if (!DeclaresNode(binding))
        {
            return Refusal.Unsupported(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"A new part binds to node '{binding}', which this model's table of {Nodes.Length} does not declare. A part's label names a node of its own model, so add the node first."));
        }

        return Result.Ok(new MmbModel(
            Path, Version, HeaderFlags, DeclaredLength, Nodes,
            Parts.Add(part.AsAppended(Parts.Length))));
    }

    /// <summary>The same model with one more node, appended after the last.</summary>
    /// <remarks>
    /// <para>
    /// The model carries its own hierarchy: every part's binding name is in this
    /// table on 441,865 of 441,865, and each node's trailing <c>u16</c> is a
    /// parent index, in range on all 6.58 million (Roadmap §10.61). So adding a
    /// joint is appending a triple, not writing an ANIM.
    /// </para>
    /// <para>
    /// <b>The matrix defaults to identity, measured rather than assumed.</b>
    /// 98.8% of shipped nodes are within 1e-6 of it and only 1.1% carry a real
    /// transform, because the hierarchy is in the parent index and the pose comes
    /// from the setup. None is bit-exact identity, which is an artefact of their
    /// exporter and not something to reproduce. A caller wanting one of the 1.1%
    /// supplies its own sixty-four bytes.
    /// </para>
    /// <para>
    /// Both defaults are the loader's own: it initialises the matrix to identity
    /// and the parent to <see cref="MmbNode.NoParent"/> before reading over
    /// them, and it hashes each node's name as it reads it — a hash stored beside
    /// a string exists to be looked up, so this table is live at runtime rather
    /// than a transcript the engine discards (Roadmap §10.81).
    /// </para>
    /// <para>
    /// A repeated name refuses: a part binds to a node <em>by name</em>, so two
    /// nodes called the same thing make the binding ambiguous, and every one of
    /// 2,283 models has unique names.
    /// </para>
    /// <para>
    /// <b>What stays open, and it is inference rather than proof.</b> For a joint
    /// the game has never seen, whether it resolves through this table or through
    /// the setup's was never settled: the binding site was not found in the
    /// executable, and both data tests that looked decisive turned out to measure
    /// something else (Roadmap §10.81 to §10.84, and the specification's §15 row).
    /// </para>
    /// <para>
    /// The account that fits every measurement is that <em>a part is placed by
    /// its own model's node table, and the setup adds animation for the names it
    /// also declares</em> — most models have no setup at all, and 90.3% of those
    /// spread their parts over several nodes of a table that is the only one they
    /// have, while 96.6% of the nodes a part binds to are identity, so the node
    /// is a target rather than a position. On that reading a new node draws its
    /// part where the geometry says and simply never animates unless a setup
    /// names it too.
    /// </para>
    /// <para>
    /// <b>Do not write that down as settled and do not build on it as though it
    /// were.</b> It wants one in-game probe, batched with the depth-resolution
    /// question because a loader use is scarce. Nothing here is blocked in the
    /// meantime: the names agree either way for a joint that already exists,
    /// which is why adding a part to one needs none of this and is the path to
    /// prefer until the row closes.
    /// </para>
    /// </remarks>
    /// <param name="name">The joint's name, which a part's label will match.</param>
    /// <param name="parent">Its parent's index, or negative for a root.</param>
    /// <param name="matrix">Sixty-four bytes, or empty for identity.</param>
    public Result<MmbModel> WithAppendedNode(
        string name, int parent, ReadOnlyMemory<byte> matrix = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0)
        {
            return Refusal.Unsupported("A node's name is what a part binds to, so it cannot be empty.");
        }

        foreach (char character in name)
        {
            if (character is < ' ' or > '~')
            {
                return Refusal.Unsupported(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"A node's name is stored as ASCII and this one contains U+{(int)character:X4}."));
            }
        }

        if (DeclaresNode(name))
        {
            return Refusal.Unsupported(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"This model already declares a node called '{name}'. A part binds to a node by name, so two of them would be ambiguous."));
        }

        if (parent >= Nodes.Length)
        {
            return Refusal.Unsupported(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"A new node named parent {parent} and this model has {Nodes.Length} nodes. A parent is an index into the table, and the new node cannot be its own."));
        }

        if (!matrix.IsEmpty && matrix.Length != MmbNode.MatrixByteCount)
        {
            return Refusal.Unsupported(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"A node's matrix is {MmbNode.MatrixByteCount} bytes and this one is {matrix.Length}."));
        }

        MmbNode node = new(
            System.Text.Encoding.ASCII.GetBytes(name),
            matrix.IsEmpty ? MmbNode.Identity : matrix,
            // Spelled out rather than left to the cast. Unchecked C# narrows -1
            // to 0xFFFF, which is the right answer by arithmetic accident, and a
            // reader should not have to know that to see that a negative parent
            // means a root.
            parent < 0 ? MmbNode.NoParent : (ushort)parent);

        return Result.Ok(new MmbModel(
            Path, Version, HeaderFlags, DeclaredLength, Nodes.Add(node), Parts));
    }

    private bool DeclaresNode(string name)
    {
        foreach (MmbNode node in Nodes)
        {
            if (System.Text.Encoding.ASCII.GetString(node.NameBytes.Span).Equals(
                name, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;

namespace Perianth.Core.Content;

/// <summary>One texture a model's materials bind, and where it is bound.</summary>
/// <param name="Channel">The engine channel name, as the editordata spelled it.</param>
/// <param name="Path">The normalized path, ready for a content source.</param>
/// <param name="Bindings">How many material records bind it.</param>
/// <param name="Own">
/// Whether the path carries the model's own name, rather than naming something
/// from the shared library.
/// </param>
public readonly record struct TextureReference(string Channel, string Path, int Bindings, bool Own);

/// <summary>
/// The distinct textures a model's materials bind, in the order worth showing
/// them.
/// </summary>
/// <remarks>
/// <para>
/// A character binds far more textures than it has interesting ones. Cartman's
/// materials make 1,610 channel bindings naming 80 distinct files: 44
/// <c>DiffuseColor</c>, 36 <c>TransparentColor</c> — which are alpha masks, not
/// pictures — and one each of <c>NormalMap</c>, <c>SpecularColor</c> and
/// <c>EmissiveColor</c>. Listing them in source order puts the shared
/// paper-scan library ahead of the two textures that actually say who this is,
/// so the ordering here is the point of the type, not an incidental detail.
/// </para>
/// <para>
/// Deduplication is by path, not by binding: the same file bound by ninety
/// material records is one texture. The count is kept because it distinguishes
/// the body texture from a detail used once.
/// </para>
/// </remarks>
public static class MaterialTextures
{
    /// <summary>
    /// Lists the distinct textures <paramref name="editordata"/> binds.
    /// </summary>
    /// <param name="editordata">The parsed editordata for one model.</param>
    /// <param name="name">
    /// The model's own name, used only to order its own textures first. An empty
    /// name orders by path alone.
    /// </param>
    /// <remarks>
    /// A path the resolution rule refuses is skipped rather than refused over.
    /// This is a listing for someone to look at, and one unreadable binding
    /// among 1,610 is no reason to show them nothing; the export path judges the
    /// same paths strictly, and is where a refusal belongs.
    /// </remarks>
    public static ImmutableArray<TextureReference> List(EditordataFile editordata, string name)
    {
        ArgumentNullException.ThrowIfNull(editordata);
        ArgumentNullException.ThrowIfNull(name);

        Dictionary<string, TextureReference> distinct = new(StringComparer.Ordinal);
        List<string> order = [];

        foreach (EditordataSection section in editordata.Sections)
        {
            foreach (EditordataMaterial material in section.Materials)
            {
                foreach (EditordataChannel bound in material.Channels)
                {
                    if (bound.TexturePath.Length == 0)
                    {
                        continue;
                    }

                    if (!TexturePath.Normalize(bound.TexturePath, bound.Channel)
                        .TryGetValue(out string? path, out _))
                    {
                        continue;
                    }

                    string key = path.ToLowerInvariant();

                    if (distinct.TryGetValue(key, out TextureReference seen))
                    {
                        distinct[key] = seen with { Bindings = seen.Bindings + 1 };
                        continue;
                    }

                    distinct[key] = new TextureReference(bound.Channel, path, 1, Carries(key, name));
                    order.Add(key);
                }
            }
        }

        order.Sort((left, right) => Compare(distinct[left], distinct[right]));

        ImmutableArray<TextureReference>.Builder listed =
            ImmutableArray.CreateBuilder<TextureReference>(order.Count);

        foreach (string key in order)
        {
            listed.Add(distinct[key]);
        }

        return listed.MoveToImmutable();
    }

    /// <summary>
    /// The model's own textures first, then by channel, then by how much of the
    /// model each one paints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first screenful should identify the asset, and sorting by path does
    /// the opposite. Every character binds a facial sheet per mouth shape —
    /// 34 of one measured model's 51 pictures — and those sheets sort under
    /// <c>newfemalemouths</c>, ahead of the <c>paperscans512</c> colours the
    /// character is actually dressed in.
    /// </para>
    /// <para>
    /// Binding count separates them without guessing at names: a mouth shape is
    /// bound once because one part wears it, while the skin and clothing
    /// colours are bound by a hundred parts each. That is a property of the
    /// model rather than of the library's folder layout, so it holds for a
    /// character assembled entirely from shared parts — which one measured
    /// character is.
    /// </para>
    /// </remarks>
    private static int Compare(TextureReference left, TextureReference right)
    {
        if (left.Own != right.Own)
        {
            return left.Own ? -1 : 1;
        }

        int channel = string.CompareOrdinal(left.Channel, right.Channel);
        if (channel != 0)
        {
            return channel;
        }

        // Path last, so that two textures painting equal shares of the model
        // still come out in the same order every time.
        int bindings = right.Bindings.CompareTo(left.Bindings);
        return bindings != 0 ? bindings : string.CompareOrdinal(left.Path, right.Path);
    }

    /// <summary>
    /// Whether a lowercased path names the model.
    /// </summary>
    /// <remarks>
    /// The name is matched whole and in its variant-stripped form, because
    /// <c>chr_cartman_var_hero</c>'s own textures are spelled with the base
    /// name. Anything shorter would match half the library: matching on
    /// <c>cat</c> would claim every path containing "catalogue".
    /// </remarks>
    private static bool Carries(string lowercasedPath, string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        string wanted = name.ToLowerInvariant();

        if (lowercasedPath.Contains(wanted, StringComparison.Ordinal))
        {
            return true;
        }

        int variant = wanted.IndexOf("_var_", StringComparison.Ordinal);
        return variant > 0
            && lowercasedPath.Contains(wanted[..variant], StringComparison.Ordinal);
    }
}

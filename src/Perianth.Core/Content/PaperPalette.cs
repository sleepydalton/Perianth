using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Core.Content;

/// <summary>
/// One colour a costume piece can be, as the game itself offers it.
/// </summary>
/// <param name="Name">The colour's own name, as the game spells it.</param>
/// <param name="TexturePath">The paper scan that is this colour.</param>
/// <param name="Red">The swatch red channel, 0-255.</param>
/// <param name="Green">The swatch green channel, 0-255.</param>
/// <param name="Blue">The swatch blue channel, 0-255.</param>
/// <remarks>
/// Two halves of one colour: the paper is the art that appears on the model, and
/// the channels are what the game's own picker draws in its grid. A front end
/// shows the second and applies the first.
/// </remarks>
public sealed record PaperSwatch(string Name, string TexturePath, byte Red, byte Green, byte Blue)
{
    /// <summary>The swatch as <c>#RRGGBB</c>, for a front end that wants one string.</summary>
    public string Hex => string.Create(
        CultureInfo.InvariantCulture, $"#{Red:X2}{Green:X2}{Blue:X2}");
}

/// <summary>
/// The colours the game's costume picker offers, read from the game's own data.
/// </summary>
/// <remarks>
/// <para>
/// The picker's grid is 5 x 16. There are exactly <b>80</b> colours, and each is
/// two records that agree: a <c>TintColor</c> named <c>SP_&lt;name&gt;</c> in
/// <c>tintcolors.juice</c>, carrying the swatch's ARGB, and a paper scan called
/// <c>tex_&lt;name&gt;_*_d.dds</c> in the shared library, carrying the art. All
/// 80 match; the only paper scans without a colour record are four plain whites,
/// which are base stock rather than choices.
/// </para>
/// <para>
/// <b>Recolouring is repointing.</b> A costume's colour is the paper its
/// materials bind — coloured paper carries a <c>(1,1,1)</c> tint by construction,
/// so multiplying it does nothing — which means changing the colour means
/// changing which paper is bound. That is <see cref="MaterialEdit"/>'s existing
/// repoint, and this only says which paths to offer.
/// </para>
/// <para>
/// The tint record is deliberately not applied as a tint. What an engine does
/// with it at runtime is not established, and the paper is what is drawn either
/// way; the ARGB is read only so a picker can show the same swatch the game
/// shows, rather than a colour this project invented.
/// </para>
/// </remarks>
public static partial class PaperPalette
{
    /// <summary>Where the game keeps its costume colours.</summary>
    public const string ColourTable = "camel/game system data/juice/auto_load/tintcolors.juice";

    /// <summary>
    /// The paper library the materials actually bind.
    /// </summary>
    /// <remarks>
    /// There are two copies of the scans in the archives. This one is the
    /// library; the other lives under <c>user_data/maya/reference</c> and is an
    /// artist's copy, bound by nothing. Offering a path from the wrong one would
    /// write a mod that names a file the game holds and never reads.
    /// </remarks>
    public const string LibraryFolder =
        "camel/baked/assets/textures/southpark/library/texture/paperscans512/";

    /// <summary>The prefix marking a colour record as one of the paper swatches.</summary>
    private const string SwatchPrefix = "TintColor_SP_";

    [GeneratedRegex(
        @"TintColor\s+(?<name>\w+)\s*<[^>]*>\s*\{\s*myColor\s+0x(?<argb>[0-9A-Fa-f]{8})",
        RegexOptions.CultureInvariant)]
    private static partial Regex ColourRecord { get; }

    /// <summary>The same records, keyed by the id an item refers to them by.</summary>
    [GeneratedRegex(
        @"TintColor\s+(?<name>\w+)\s*<[^>]*uid=(?<uid>[0-9A-Fa-f]+)[^>]*>\s*\{\s*myColor\s+0x(?<argb>[0-9A-Fa-f]{8})",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdentifiedColour { get; }

    /// <summary>
    /// Every colour in the table, by the id items name it with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the same question as <see cref="Read"/>, which offers the 80 papers
    /// the picker's grid shows. This is the whole table of 117, because an item
    /// may name any of them — and the ones it usually names are exactly the
    /// ones the grid leaves out.
    /// </para>
    /// <para>
    /// <b>Alpha is whether the colour applies at all.</b> <c>NoTint</c> is
    /// <c>0x00BBBBBB</c>: a grey with nothing switched on, and 585 of the 970
    /// item tints are it. Reading the RGB and ignoring the alpha would paint
    /// most of the game's wardrobe grey.
    /// </para>
    /// </remarks>
    public static Result<ImmutableArray<TintColour>> Tints(ContentSources content)
    {
        ArgumentNullException.ThrowIfNull(content);

        Result<byte[]?> read = content.Read(ColourTable);
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The costume colours are not here — {ColourTable} is absent."));
        }

        List<TintColour> tints = [];
        foreach (Match record in IdentifiedColour.Matches(Encoding.UTF8.GetString(bytes)))
        {
            string argb = record.Groups["argb"].Value;
            byte Component(int at) => byte.Parse(
                argb.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            tints.Add(new TintColour(
                record.Groups["uid"].Value.ToUpperInvariant(),
                record.Groups["name"].Value,
                Component(2), Component(4), Component(6),
                Applies: Component(0) != 0));
        }

        return Result.Ok<ImmutableArray<TintColour>>([.. tints]);
    }

    /// <summary>The colour an item names, or null where it names none that applies.</summary>
    public static TintColour? Tint(ImmutableArray<TintColour> tints, string uid)
    {
        if (tints.IsDefaultOrEmpty || string.IsNullOrEmpty(uid))
        {
            return null;
        }

        foreach (TintColour tint in tints)
        {
            if (string.Equals(tint.Uid, uid, StringComparison.OrdinalIgnoreCase))
            {
                return tint.Applies ? tint : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The colours on offer, ordered by name, each paired with its paper.
    /// </summary>
    /// <remarks>
    /// A colour whose paper the archives do not hold is left out rather than
    /// offered: a picker entry that cannot be applied is worse than one absent,
    /// because it fails only after someone has chosen it.
    /// </remarks>
    public static Result<ImmutableArray<PaperSwatch>> Read(
        ContentSources content, ImmutableArray<SdfPathEntry> paths)
    {
        ArgumentNullException.ThrowIfNull(content);

        Result<byte[]?> read = content.Read(ColourTable);
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The costume colours are not here — {ColourTable} is absent, so there is no palette to offer."));
        }

        Dictionary<string, string> papers = Papers(paths);
        List<PaperSwatch> swatches = [];

        foreach (Match record in ColourRecord.Matches(Encoding.UTF8.GetString(bytes)))
        {
            string declared = record.Groups["name"].Value;
            if (!declared.StartsWith(SwatchPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string name = declared[SwatchPrefix.Length..];
            if (!papers.TryGetValue(name.ToLowerInvariant(), out string? texture))
            {
                continue;
            }

            // 0xAARRGGBB. The alpha is not carried: a swatch is a colour to draw
            // in a grid, and every one of them is opaque.
            uint argb = uint.Parse(record.Groups["argb"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            swatches.Add(new PaperSwatch(
                name, texture, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }

        swatches.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return Result.Ok<ImmutableArray<PaperSwatch>>([.. swatches]);
    }

    /// <summary>
    /// Whether a bound texture is one of the palette's papers, and which.
    /// </summary>
    /// <remarks>
    /// How a front end decides which of a model's textures are colours to offer a
    /// choice for. A material binding something else — a printed logo, a face —
    /// is not a colour and must not be presented as one.
    /// </remarks>
    public static PaperSwatch? Match(ImmutableArray<PaperSwatch> palette, string texturePath)
    {
        ArgumentNullException.ThrowIfNull(texturePath);

        string wanted = SdfIndex.NormalizePath(texturePath);
        foreach (PaperSwatch swatch in palette)
        {
            if (string.Equals(swatch.TexturePath, wanted, StringComparison.Ordinal))
            {
                return swatch;
            }
        }

        return null;
    }

    /// <summary>Colour name to library path, for the scans the archives hold.</summary>
    private static Dictionary<string, string> Papers(ImmutableArray<SdfPathEntry> paths)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        if (paths.IsDefaultOrEmpty)
        {
            return found;
        }

        foreach (SdfPathEntry entry in paths)
        {
            string path = SdfIndex.NormalizePath(entry.Path);
            if (!path.StartsWith(LibraryFolder, StringComparison.Ordinal))
            {
                continue;
            }

            // tex_<name>_<whatever>_d.dds. The middle is a per-texture suffix that
            // varies and carries no meaning here, so the name is what sits between
            // the prefix and the first underscore after it.
            string file = path[LibraryFolder.Length..];
            if (!file.StartsWith("tex_", StringComparison.Ordinal) ||
                !file.EndsWith("_d.dds", StringComparison.Ordinal))
            {
                continue;
            }

            int end = file.IndexOf('_', 4);
            if (end < 0)
            {
                continue;
            }

            string name = file[4..end];

            // The smallest path wins, not the first seen. The index's order is
            // not something this can rely on, and a palette that depends on it
            // would offer a different texture for the same colour between runs
            // for no visible reason.
            if (!found.TryGetValue(name, out string? already) ||
                string.CompareOrdinal(path, already) < 0)
            {
                found[name] = path;
            }
        }

        return found;
    }
}

/// <summary>
/// One colour in the game's tint table, as an item names it.
/// </summary>
/// <param name="Uid">The id an item's <c>myDefaultTint</c> refers to.</param>
/// <param name="Name">The table's own name for it.</param>
/// <param name="Applies">
/// Whether the colour is switched on. The table's <c>NoTint</c> carries a
/// perfectly ordinary grey behind an alpha of zero, and it is what most items
/// name, so this is the difference between tinting what the game tints and
/// painting the wardrobe grey.
/// </param>
public sealed record TintColour(string Uid, string Name, byte Red, byte Green, byte Blue, bool Applies);

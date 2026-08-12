using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Perianth.Core.Imaging;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;

namespace Perianth.Core.Content;

/// <summary>One channel binding, and the tint the part carrying it is drawn with.</summary>
/// <param name="Section">The section's ordinal, which is also the model-part ordinal.</param>
/// <param name="Channel">The engine channel name, as the editordata spelled it.</param>
/// <param name="Path">The path as serialized, in the file's own spelling.</param>
/// <param name="Tint">
/// The albedo tint of the section's first custom record, or null when the file
/// carries no custom data at all.
/// </param>
public readonly record struct MaterialBinding(int Section, string Channel, string Path, Rgb? Tint);

/// <summary>What an edit produced, and how much of the file it touched.</summary>
/// <param name="File">The edited file. The original is unchanged.</param>
/// <param name="Sections">How many sections were altered.</param>
/// <param name="Bindings">How many individual channel bindings were altered.</param>
public sealed record MaterialEditOutcome(EditordataFile File, int Sections, int Bindings);

/// <summary>
/// Changes what a model's parts are painted with, without touching an image.
/// </summary>
/// <remarks>
/// <para>
/// Two operations, and which one is useful depends on the part — Roadmap §6.11.
/// The corpus divides almost in half. A **paper part** binds a scanned sheet of
/// coloured paper and its tint is <c>(1,1,1)</c> in all 60,365 of them, so its
/// colour lives entirely in the texture and <see cref="Repoint"/> is the
/// operation. A **white part** binds <c>tex_white16_d.dds</c>, a blank sheet,
/// and carries its colour entirely in the tint across 51,293 sections, so
/// <see cref="Retint"/> is the operation and repointing it would swap one blank
/// sheet for another.
/// </para>
/// <para>
/// Each is therefore inert on the other's population, which is why a caller
/// should offer the one matching what was selected rather than both. Nothing
/// here decides that; <see cref="Bindings"/> reports the tint alongside the
/// path so a front end can.
/// </para>
/// <para>
/// Every operation refuses when it matched nothing. A texture path is long,
/// case-varying and separator-varying, and an edit that quietly changed zero
/// sections would write a mod indistinguishable from a working one.
/// </para>
/// </remarks>
public static class MaterialEdit
{
    /// <summary>
    /// The channel the albedo is sampled from, and the only one a tint
    /// multiplies. Present in 439,013 of 2,177,048 corpus bindings, one per
    /// material, alongside NormalMap, SpecularColor, TransparentColor and
    /// EmissiveColor.
    /// </summary>
    private const string DiffuseChannel = "DiffuseColor";

    /// <summary>
    /// Every channel binding in the file, in section order.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="MaterialTextures.List"/>, which deduplicates by
    /// path to answer "what is this model painted with". This does not
    /// deduplicate, because an edit acts on bindings and the same texture bound
    /// by two parts with different tints is two different things to change.
    /// </remarks>
    public static ImmutableArray<MaterialBinding> Bindings(EditordataFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        ImmutableArray<MaterialBinding>.Builder bindings =
            ImmutableArray.CreateBuilder<MaterialBinding>();

        foreach (EditordataSection section in file.Sections)
        {
            Rgb? tint = Tint(section);

            foreach (EditordataMaterial material in section.Materials)
            {
                foreach (EditordataChannel channel in material.Channels)
                {
                    if (channel.TexturePath.Length != 0)
                    {
                        bindings.Add(new MaterialBinding(
                            section.Ordinal, channel.Channel, channel.TexturePath, tint));
                    }
                }
            }
        }

        return bindings.ToImmutable();
    }

    /// <summary>
    /// Points every binding of <paramref name="from"/> at <paramref name="to"/>.
    /// </summary>
    /// <param name="file">The file to edit. It is not modified.</param>
    /// <param name="from">The path to replace, in any spelling.</param>
    /// <param name="to">The path to bind instead.</param>
    /// <param name="sections">
    /// The section ordinals to restrict the edit to, or null for every section.
    /// Aiming at one part is possible but hard to aim: material names identify
    /// the art supply rather than the anatomy, so a caller usually wants the
    /// whole texture.
    /// </param>
    public static Result<MaterialEditOutcome> Repoint(
        EditordataFile file,
        string from,
        string to,
        IReadOnlyCollection<int>? sections = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (to.Length == 0)
        {
            return Refusal.Unsupported(
                "A repointed texture needs a path to bind. Unbinding a channel is not an edit this makes.");
        }

        if (!Comparable(from, out string wanted))
        {
            return Refusal.Unsupported("The texture to repoint has no path.");
        }

        Result<Refusal?> scope = Scope(file, sections);
        if (scope.IsRefused)
        {
            return scope.Refusal;
        }

        ImmutableArray<EditordataSection>.Builder edited =
            ImmutableArray.CreateBuilder<EditordataSection>(file.Sections.Length);
        int changedSections = 0;
        int changedBindings = 0;

        foreach (EditordataSection section in file.Sections)
        {
            if (sections is not null && !sections.Contains(section.Ordinal))
            {
                edited.Add(section);
                continue;
            }

            int before = changedBindings;
            ImmutableArray<EditordataMaterial>.Builder materials =
                ImmutableArray.CreateBuilder<EditordataMaterial>(section.Materials.Length);

            foreach (EditordataMaterial material in section.Materials)
            {
                ImmutableArray<EditordataChannel>.Builder channels =
                    ImmutableArray.CreateBuilder<EditordataChannel>(material.Channels.Length);

                foreach (EditordataChannel channel in material.Channels)
                {
                    if (Comparable(channel.TexturePath, out string held) &&
                        string.Equals(held, wanted, StringComparison.Ordinal))
                    {
                        channels.Add(channel with { TexturePath = Spell(to, channel.TexturePath) });
                        changedBindings++;
                    }
                    else
                    {
                        channels.Add(channel);
                    }
                }

                materials.Add(material with { Channels = channels.MoveToImmutable() });
            }

            edited.Add(section with { Materials = materials.MoveToImmutable() });

            if (changedBindings != before)
            {
                changedSections++;
            }
        }

        if (changedBindings == 0)
        {
            return NothingMatched(from, sections);
        }

        return Result.Ok(new MaterialEditOutcome(
            file with { Sections = edited.MoveToImmutable() }, changedSections, changedBindings));
    }

    /// <summary>
    /// Binds <paramref name="path"/> on the named parts, whatever they carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation for "give this part that texture", as distinct from
    /// <see cref="Repoint"/>'s "move every binding of this texture". Repoint
    /// needs to be told what it is replacing, which is a question nobody has to
    /// answer when they have already named the part: it has one binding on that
    /// channel and this replaces it, whatever it was.
    /// </para>
    /// <para>
    /// Named parts are required rather than optional. Without them this would
    /// bind one texture across an entire model, which is a thing to do by
    /// accident and never on purpose.
    /// </para>
    /// </remarks>
    /// <param name="file">The file to edit. It is not modified.</param>
    /// <param name="sections">The section ordinals to change. Must not be empty.</param>
    /// <param name="channel">The engine channel name, as the editordata spells it.</param>
    /// <param name="path">The texture to bind.</param>
    public static Result<MaterialEditOutcome> Bind(
        EditordataFile file,
        IReadOnlyCollection<int> sections,
        string channel,
        string path)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == 0)
        {
            return Refusal.Unsupported("A part needs a texture path to bind.");
        }

        Result<Refusal?> scope = Scope(file, sections);
        if (scope.IsRefused)
        {
            return scope.Refusal;
        }

        ImmutableArray<EditordataSection>.Builder edited =
            ImmutableArray.CreateBuilder<EditordataSection>(file.Sections.Length);
        int changedSections = 0;
        int changedBindings = 0;

        foreach (EditordataSection section in file.Sections)
        {
            if (!sections.Contains(section.Ordinal))
            {
                edited.Add(section);
                continue;
            }

            int before = changedBindings;
            ImmutableArray<EditordataMaterial>.Builder materials =
                ImmutableArray.CreateBuilder<EditordataMaterial>(section.Materials.Length);

            foreach (EditordataMaterial material in section.Materials)
            {
                ImmutableArray<EditordataChannel>.Builder channels =
                    ImmutableArray.CreateBuilder<EditordataChannel>(material.Channels.Length);

                foreach (EditordataChannel bound in material.Channels)
                {
                    if (string.Equals(bound.Channel, channel, StringComparison.Ordinal))
                    {
                        channels.Add(bound with { TexturePath = Spell(path, bound.TexturePath) });
                        changedBindings++;
                    }
                    else
                    {
                        channels.Add(bound);
                    }
                }

                materials.Add(material with { Channels = channels.MoveToImmutable() });
            }

            edited.Add(section with { Materials = materials.MoveToImmutable() });

            if (changedBindings != before)
            {
                changedSections++;
            }
        }

        if (changedBindings == 0)
        {
            // The channel is absent rather than the part: Scope already proved
            // every named section exists. Adding the channel would be inventing
            // a binding the material never declared.
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"None of the {sections.Count} named parts has a {channel} channel to bind."));
        }

        return Result.Ok(new MaterialEditOutcome(
            file with { Sections = edited.MoveToImmutable() }, changedSections, changedBindings));
    }

    /// <summary>
    /// Recolours every section that <em>draws</em> <paramref name="boundTo"/>.
    /// </summary>
    /// <remarks>
    /// Selection is by the diffuse channel alone, unlike <see cref="Repoint"/>,
    /// and the difference is not cosmetic. The tint multiplies the diffuse
    /// sample, so a section reached through its NormalMap is a section the
    /// caller never named — and <c>tex_white16_d.dds</c> is both the blank white
    /// sheet this operation exists for and the placeholder sitting in the other
    /// four channels of nearly every section in the game. Aiming at it
    /// any-channel repaints a whole model while truthfully reporting the count.
    /// <para>
    /// Measured, because narrowing a selection is the kind of improvement that
    /// silently breaks a population: across 2,272 corpus files, exactly
    /// <b>one</b> texture is both drawn somewhere and named off-diffuse
    /// somewhere, and it is white16. For every other texture the two predicates
    /// already agree, so this changes nothing except the case it was written
    /// for. (Roadmap §6.14.)
    /// </para>
    /// </remarks>
    /// <param name="file">The file to edit. It is not modified.</param>
    /// <param name="boundTo">The texture whose parts to recolour, in any spelling.</param>
    /// <param name="replacing">
    /// The tint those parts currently carry, or null for every tint. Naming it
    /// is usually what a caller wants: 36 distinct tints share
    /// <c>tex_white16_d.dds</c>, of which black is 86% and is the ink line work,
    /// so recolouring all of them at once flattens the drawing.
    /// </param>
    /// <param name="tint">The colour to give them.</param>
    public static Result<MaterialEditOutcome> Retint(
        EditordataFile file,
        string boundTo,
        Rgb? replacing,
        Rgb tint)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(boundTo);

        if (!Finite(tint))
        {
            return Refusal.Unsupported("A tint must be a finite colour.");
        }

        if (!Comparable(boundTo, out string wanted))
        {
            return Refusal.Unsupported("The texture to recolour has no path.");
        }

        if (file.CustomVersion is null)
        {
            // The tint lives in the custom tail. Adding one would mean deciding
            // a version and writing records for every section, which is
            // authoring a structure the file never had rather than editing it.
            return Refusal.Unsupported(
                "This editordata carries no custom data, so its parts have no tint to change.");
        }

        ImmutableArray<EditordataSection>.Builder edited =
            ImmutableArray.CreateBuilder<EditordataSection>(file.Sections.Length);
        int changedSections = 0;

        foreach (EditordataSection section in file.Sections)
        {
            if (section.CustomRecords.IsEmpty || !Draws(section, wanted) || !Matches(section, replacing))
            {
                edited.Add(section);
                continue;
            }

            EditordataCustomRecord record = section.CustomRecords[0];

            // W is unresolved and is not ours to set. Only the RGB the
            // specification proves behaves as an albedo tint is written.
            Float4 recoloured = record.Slot10 with
            {
                X = (float)tint.R,
                Y = (float)tint.G,
                Z = (float)tint.B,
            };

            edited.Add(section with
            {
                CustomRecords = section.CustomRecords.SetItem(0, record with { Slot10 = recoloured }),
            });

            changedSections++;
        }

        if (changedSections == 0)
        {
            return NothingMatched(boundTo, sections: null);
        }

        return Result.Ok(new MaterialEditOutcome(
            file with { Sections = edited.MoveToImmutable() }, changedSections, changedSections));
    }

    /// <summary>
    /// A path for a texture one model alone will use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under the textures tree, because all but 118 of the game's 595,389
    /// bindings live there and a texture is not stored beside the model that
    /// uses it. Under a folder of our own, because a path the game already
    /// holds would make an addition into a replacement — and a replacement
    /// changes that texture for every model binding it, which the shared
    /// paper-scan library means is often dozens.
    /// </para>
    /// <para>
    /// A proposal, to be shown and editable. Nothing here checks whether the
    /// path is free; that needs the archives, and the caller has them.
    /// </para>
    /// </remarks>
    /// <param name="model">The model's name, used only to keep paths apart.</param>
    /// <param name="original">The texture being replaced, for its stem.</param>
    /// <param name="parts">
    /// The sections the edit is restricted to, or null for all of them. Naming
    /// parts makes the edit about <em>those</em> parts, so the proposal has to
    /// differ from the one for other parts of the same texture. Without this,
    /// giving two parts of one paper sheet two different images proposes one
    /// path for both: the second image lands on it, and the first part changes
    /// with it because it was already pointed there. Reported by a user, whose
    /// workaround — clearing the path box between additions — was the diagnosis.
    /// </param>
    public static string ProposePath(string model, string original, IReadOnlyCollection<int>? parts = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(original);

        string stem = original.Replace('\\', '/');
        stem = stem[(stem.LastIndexOf('/') + 1)..];

        int dot = stem.LastIndexOf('.');
        if (dot > 0)
        {
            stem = stem[..dot];
        }

        // Ordered, so that "47,51" and "51,47" are one aim rather than two
        // paths for one edit.
        string aimed = parts is { Count: > 0 }
            ? $"{stem}_part_{string.Join('_', parts.Order())}"
            : stem;

        return $"camel/baked/assets/textures/perianth/{Safe(model, "model")}/{Safe(aimed, "texture")}.dds";
    }

    /// <summary>
    /// Folds a name to characters a path can hold, or to a placeholder.
    /// </summary>
    /// <remarks>
    /// A material or model name can hold anything the artist typed, and a
    /// proposed path built from one has to be a path. Substitution rather than
    /// removal, so two names that differ only in punctuation stay distinct.
    /// </remarks>
    private static string Safe(string text, string whenEmpty)
    {
        StringBuilder safe = new(text.Length);

        foreach (char character in text)
        {
            safe.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
                ? char.ToLowerInvariant(character)
                : '_');
        }

        string folded = safe.ToString().Trim('_');
        return folded.Length == 0 ? whenEmpty : folded;
    }

    private static Rgb? Tint(EditordataSection section) =>
        section.CustomRecords.IsEmpty
            ? null
            : new Rgb(section.CustomRecords[0].Slot10.X,
                      section.CustomRecords[0].Slot10.Y,
                      section.CustomRecords[0].Slot10.Z);

    /// <summary>Whether the section's albedo is sampled from this texture.</summary>
    private static bool Draws(EditordataSection section, string wanted) =>
        section.Materials.Any(material => material.Channels.Any(
            channel => string.Equals(channel.Channel, DiffuseChannel, StringComparison.Ordinal) &&
                       Comparable(channel.TexturePath, out string held) &&
                       string.Equals(held, wanted, StringComparison.Ordinal)));

    private static bool Matches(EditordataSection section, Rgb? replacing)
    {
        if (replacing is not Rgb wanted)
        {
            return true;
        }

        Rgb? held = Tint(section);
        return held is Rgb value &&
               Near(value.R, wanted.R) && Near(value.G, wanted.G) && Near(value.B, wanted.B);
    }

    // A tint read from the file is a binary32 widened to double, and a tint
    // typed by a user or round-tripped through text is not the same double. The
    // tolerance is one part in 65,536, far finer than the 36 distinct tints
    // sharing tex_white16 and far coarser than the representation error.
    private static bool Near(double left, double right) => Math.Abs(left - right) <= 1.0 / 65536.0;

    private static bool Finite(Rgb tint) =>
        double.IsFinite(tint.R) && double.IsFinite(tint.G) && double.IsFinite(tint.B);

    /// <summary>
    /// Folds a serialized path to the form two spellings of the same file share.
    /// </summary>
    /// <remarks>
    /// Separators and case both vary in the shipped files, and the archive folds
    /// case because the container does. Comparison folds both so a path copied
    /// from a listing matches the one the editordata spells; what gets
    /// <em>written</em> is never this form.
    /// </remarks>
    private static bool Comparable(string path, out string folded)
    {
        folded = path.Replace('\\', '/').ToLowerInvariant();
        return folded.Length != 0;
    }

    /// <summary>
    /// Spells a new path the way the path it replaces was spelled.
    /// </summary>
    /// <remarks>
    /// The shipped files use backslashes and the reader accepts either, so this
    /// is about staying near what the engine demonstrably loads rather than
    /// imposing our own convention on a file we are only partly rewriting.
    /// </remarks>
    private static string Spell(string path, string replacing) =>
        replacing.Contains('/', StringComparison.Ordinal)
            ? path.Replace('\\', '/')
            : path.Replace('/', '\\');

    private static Refusal NothingMatched(string path, IReadOnlyCollection<int>? sections) =>
        Refusal.Unsupported(
            sections is null
                ? string.Create(CultureInfo.InvariantCulture, $"Nothing in this editordata binds {path}.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"Nothing in the {sections.Count} named sections binds {path}."),
            DiagnosticIds.MaterialEditMatchedNothing);

    private static Result<Refusal?> Scope(EditordataFile file, IReadOnlyCollection<int>? sections)
    {
        if (sections is null)
        {
            return Result.Ok<Refusal?>(null);
        }

        if (sections.Count == 0)
        {
            return Refusal.Unsupported("An edit restricted to no sections would change nothing.");
        }

        foreach (int ordinal in sections)
        {
            if (ordinal < 0 || ordinal >= file.Sections.Length)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"This editordata has {file.Sections.Length} sections, and section {ordinal} was named."));
            }
        }

        return Result.Ok<Refusal?>(null);
    }
}

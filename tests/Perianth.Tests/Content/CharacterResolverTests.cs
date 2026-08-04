using System;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks the conventions that assemble one model's asset set.
/// </summary>
/// <remarks>
/// Every rule here was measured over the shipped archive before it was written,
/// and the fixtures reproduce the shapes that measurement found: the variant
/// whose rig family owns its setup, and the character whose name is extended by
/// another's.
/// </remarks>
public sealed class CharacterResolverTests
{
    private const string Animation = "camel/baked/snowdrop/animation/";

    private static ImmutableArray<SdfPathEntry> Index(params string[] paths) =>
        [.. paths.Select((path, ordinal) => new SdfPathEntry(path, ordinal + 1, IsDirectory: false))];

    private static CharacterAssets Resolve(ImmutableArray<SdfPathEntry> paths, string model)
    {
        Result<CharacterAssets> resolved = CharacterResolver.Resolve(paths, model);
        Assert.False(resolved.IsRefused, resolved.IsRefused ? resolved.Refusal.Message : null);
        return resolved.Value;
    }

    [Fact]
    public void A_character_named_directly_resolves_every_convention()
    {
        CharacterAssets assets = Resolve(
            Index(
                "chr/cartman/chr_cartman.mmb",
                "chr/cartman/chr_cartman.cameldata",
                "chr/cartman/chr_cartman.editordata",
                Animation + "anm_cartman_setup.anim",
                Animation + "anm_cartman_mouth_all.anim",
                Animation + "anm_cartman_eyes_all.anim",
                Animation + "anm_cartman_pupils_all.anim",
                Animation + "anm_cartman_eyebrows_all.anim",
                Animation + "anm_cartman_idle_front.anim",
                "camel/baked/assets/lipsync/lipsync_global.mlipsyncdatabase"),
            "chr/cartman/chr_cartman.mmb");

        Assert.Equal("cartman", assets.Name);
        Assert.Equal("chr/cartman/chr_cartman.cameldata", assets.Cameldata);
        Assert.Equal(AssetMatch.Exact, assets.Setup!.Match);
        Assert.Equal(Animation + "anm_cartman_eyebrows_all.anim", assets.Eyebrows!.VirtualPath);
        Assert.Equal(Animation + "anm_cartman_idle_front.anim", Assert.Single(assets.Clips).VirtualPath);
        Assert.NotNull(assets.LipsyncDatabase);
        Assert.Empty(assets.Unresolved);
    }

    [Fact]
    public void A_variant_resolves_through_its_rig_family_and_says_so()
    {
        // chr_catskinny.mmb does not exist: catskinny names a rig family in the
        // animation tree only. Without this clause the setup convention covers
        // 65.47% of characters; with it, 96.84%.
        CharacterAssets assets = Resolve(
            Index(
                "chr/catskinny_var_a/chr_catskinny_var_a.mmb",
                "chr/catskinny_var_a/chr_catskinny_var_a.cameldata",
                Animation + "anm_catskinny_setup.anim",
                Animation + "anm_catskinny_idle.anim"),
            "chr/catskinny_var_a/chr_catskinny_var_a.mmb");

        Assert.Equal(Animation + "anm_catskinny_setup.anim", assets.Setup!.VirtualPath);
        Assert.Equal(AssetMatch.VariantBase, assets.Setup.Match);
        Assert.Equal(AssetMatch.VariantBase, Assert.Single(assets.Clips).Match);
    }

    [Fact]
    public void A_model_does_not_absorb_the_clips_of_a_character_extending_its_name()
    {
        // The shape that costs 178 clips in the shipped archive: monsterranged
        // beside monsterranged_milka, which is a separate character rather than
        // a variant. The separator alone does not settle this; the longest
        // matching name does.
        ImmutableArray<SdfPathEntry> index = Index(
            "chr/a/chr_monsterranged.mmb",
            "chr/a/chr_monsterranged.cameldata",
            "chr/b/chr_monsterranged_milka.mmb",
            "chr/b/chr_monsterranged_milka.cameldata",
            Animation + "anm_monsterranged_setup.anim",
            Animation + "anm_monsterranged_combat.anim",
            Animation + "anm_monsterranged_milka_combat.anim");

        CharacterAssets @base = Resolve(index, "chr/a/chr_monsterranged.mmb");
        CharacterAssets other = Resolve(index, "chr/b/chr_monsterranged_milka.mmb");

        Assert.Equal(Animation + "anm_monsterranged_combat.anim", Assert.Single(@base.Clips).VirtualPath);
        Assert.Equal(Animation + "anm_monsterranged_milka_combat.anim", Assert.Single(other.Clips).VirtualPath);
    }

    [Fact]
    public void A_variant_falling_back_gets_the_family_clips_and_not_its_siblings()
    {
        // The family's exclusions must be recomputed on the fallback. Reusing
        // the variant's gave monsterranged_var_c 371 clips where monsterranged
        // itself had 195.
        ImmutableArray<SdfPathEntry> index = Index(
            "chr/a/chr_monsterranged.mmb",
            "chr/a/chr_monsterranged.cameldata",
            "chr/b/chr_monsterranged_milka.mmb",
            "chr/b/chr_monsterranged_milka.cameldata",
            "chr/c/chr_monsterranged_var_c.mmb",
            "chr/c/chr_monsterranged_var_c.cameldata",
            Animation + "anm_monsterranged_setup.anim",
            Animation + "anm_monsterranged_combat.anim",
            Animation + "anm_monsterranged_milka_combat.anim");

        CharacterAssets variant = Resolve(index, "chr/c/chr_monsterranged_var_c.mmb");
        CharacterAssets family = Resolve(index, "chr/a/chr_monsterranged.mmb");

        Assert.Equal(
            family.Clips.Select(clip => clip.VirtualPath),
            variant.Clips.Select(clip => clip.VirtualPath));
        Assert.Equal(AssetMatch.VariantBase, Assert.Single(variant.Clips).Match);
    }

    [Fact]
    public void The_setup_and_the_atlases_are_not_also_listed_as_clips()
    {
        CharacterAssets assets = Resolve(
            Index(
                "chr/x/chr_x.mmb",
                "chr/x/chr_x.cameldata",
                Animation + "anm_x_setup.anim",
                Animation + "anm_x_mouth_all.anim",
                Animation + "anm_x_walk.anim"),
            "chr/x/chr_x.mmb");

        Assert.Equal(Animation + "anm_x_walk.anim", Assert.Single(assets.Clips).VirtualPath);

        // The model, its cameldata, the setup, the mouth atlas and the one clip:
        // each named once, and the setup and atlas not counted twice as clips.
        Assert.Equal(5, assets.Paths().Length);
    }

    [Fact]
    public void A_model_without_its_cameldata_says_why_that_ends_the_export()
    {
        // 1,306 models ship this way, nearly all VFX. Positions live in the
        // cameldata pools, so this is a fact about the asset rather than a
        // lookup that failed.
        CharacterAssets assets = Resolve(
            Index("vfx/p/effect.mmb", "vfx/p/effect.editordata"),
            "vfx/p/effect.mmb");

        Assert.Null(assets.Cameldata);
        Assert.Contains(assets.Unresolved, note => note.Contains("no vertex positions", StringComparison.Ordinal));
    }

    [Fact]
    public void A_character_with_no_rig_reports_it_rather_than_resolving_something_near()
    {
        // The animals: 29 characters have no setup under either clause.
        CharacterAssets assets = Resolve(
            Index(
                "chr/shark/chr_shark.mmb",
                "chr/shark/chr_shark.cameldata",
                Animation + "anm_sharknado_setup.anim"),
            "chr/shark/chr_shark.mmb");

        Assert.Null(assets.Setup);
        Assert.Empty(assets.Clips);

        // Nothing at all poses it, which is a different report from a prop
        // having no setup: "no setup ANIM" is true of every prop in the
        // archive and describes a convention rather than a limitation.
        Assert.Contains(
            assets.Unresolved,
            note => note.Contains("no ANIM is named", StringComparison.Ordinal)
                && note.Contains("complete part list", StringComparison.Ordinal));
    }

    [Fact]
    public void More_than_one_lipsync_database_is_reported_rather_than_chosen_between()
    {
        // It is found by being the only one, so the count is the whole basis of
        // the claim and has to be checked rather than assumed.
        CharacterAssets assets = Resolve(
            Index(
                "chr/x/chr_x.mmb",
                "chr/x/chr_x.cameldata",
                "a/one.mlipsyncdatabase",
                "b/two.mlipsyncdatabase"),
            "chr/x/chr_x.mmb");

        Assert.Null(assets.LipsyncDatabase);
        Assert.Contains(assets.Unresolved, note => note.Contains("2 lip-sync databases", StringComparison.Ordinal));
    }

    [Fact]
    public void A_path_that_is_not_a_model_is_refused()
    {
        Result<CharacterAssets> resolved = CharacterResolver.Resolve(
            Index("chr/x/chr_x.mmb"), "chr/x/chr_x.cameldata");

        Assert.True(resolved.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, resolved.Refusal.Kind);
    }

    [Fact]
    public void A_model_the_archives_do_not_hold_is_refused_by_name()
    {
        Result<CharacterAssets> resolved = CharacterResolver.Resolve(
            Index("chr/x/chr_x.mmb"), "chr/y/chr_y.mmb");

        Assert.True(resolved.IsRefused);
        Assert.Contains("chr_y.mmb", resolved.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prop_with_animations_but_no_setup_is_told_to_choose_one()
    {
        // The note used to say a prop "can only be exported as its complete
        // part list", which was true of 3,317 props that pose perfectly well
        // by an idle. Their animations drop the kind prefix, so none of them
        // were found at all.
        CharacterAssets assets = Resolve(
            Index(
                "prop/prp_aframe_sign.mmb",
                "prop/prp_aframe_sign.cameldata",
                "prop/prp_aframe_sign.editordata",
                Animation + "anm_aframe_sign_idle_intact.anim",
                Animation + "anm_aframe_sign_idle_destroy.anim"),
            "prop/prp_aframe_sign.mmb");

        Assert.Null(assets.Setup);
        Assert.Equal(2, assets.Clips.Length);
        Assert.Contains(
            assets.Unresolved,
            note => note.Contains("normal for a prop", StringComparison.Ordinal)
                && note.Contains("2 animations", StringComparison.Ordinal));
    }
}

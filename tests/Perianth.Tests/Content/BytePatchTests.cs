using System;
using System.Linq;
using System.Security.Cryptography;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks the byte-level patch: what it produces, what it refuses, and what it
/// does not contain.
/// </summary>
public sealed class BytePatchTests
{
    private const string Path = "camel/baked/assets/textures/thing/tex_thing.dds";

    private static byte[] Bytes(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) + seed);
        }

        return bytes;
    }

    [Fact]
    public void A_patch_applied_to_its_original_reproduces_the_edit_exactly()
    {
        byte[] original = Bytes(20_000, 0);
        byte[] edited = Bytes(20_000, 0);
        edited[9_000] ^= 0xFF;

        byte[] patch = BytePatch.Make(original, edited, Path).Value;

        Assert.Equal(edited, BytePatch.Apply(patch, original).Value);
    }

    [Fact]
    public void A_small_edit_makes_a_small_patch()
    {
        // The reason a delta is worth having at all. An uncompressed texture
        // changes only where it was painted, so the patch is the painting.
        byte[] original = Bytes(1_000_000, 0);
        byte[] edited = (byte[])original.Clone();
        for (int i = 500_000; i < 500_100; i++)
        {
            edited[i] ^= 0xFF;
        }

        byte[] patch = BytePatch.Make(original, edited, Path).Value;

        Assert.True(
            patch.Length < original.Length / 50,
            $"a 100-byte change in a 1 MB file made a {patch.Length}-byte patch");
    }

    [Fact]
    public void A_patch_does_not_carry_the_unchanged_original()
    {
        // The point of shipping a difference rather than a file. An unchanged
        // region must not appear in the patch, or sharing one would share the
        // game's own bytes.
        byte[] original = Bytes(40_000, 0);

        // A run distinctive enough that finding it in the patch means it was
        // copied, not coincidence.
        byte[] fingerprint = [0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];
        fingerprint.CopyTo(original, 30_000);

        byte[] edited = (byte[])original.Clone();
        edited[100] ^= 0xFF;

        byte[] patch = BytePatch.Make(original, edited, Path).Value;

        Assert.DoesNotContain(fingerprint, Windows(patch, fingerprint.Length));
    }

    [Fact]
    public void Applying_to_the_wrong_file_is_refused_rather_than_producing_rubbish()
    {
        // The failure this project refuses everywhere: a plausible, broken
        // asset. The digest makes it impossible rather than unlikely.
        byte[] original = Bytes(8_000, 0);
        byte[] edited = Bytes(8_000, 0);
        edited[42] ^= 0xFF;

        byte[] patch = BytePatch.Make(original, edited, Path).Value;
        byte[] stranger = Bytes(8_000, 7);

        Result<byte[]> applied = BytePatch.Apply(patch, stranger);

        Assert.True(applied.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, applied.Refusal.Kind);
        Assert.Contains("different file", applied.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_of_the_wrong_length_is_refused_before_anything_is_built()
    {
        byte[] original = Bytes(8_000, 0);
        byte[] edited = Bytes(8_000, 0);
        edited[42] ^= 0xFF;

        byte[] patch = BytePatch.Make(original, edited, Path).Value;

        Assert.True(BytePatch.Apply(patch, Bytes(7_999, 0)).IsRefused);
    }

    [Fact]
    public void A_file_that_grew_is_patched_to_its_new_length()
    {
        byte[] original = Bytes(5_000, 0);
        byte[] edited = Bytes(9_000, 3);

        byte[] patch = BytePatch.Make(original, edited, Path).Value;

        Assert.Equal(edited, BytePatch.Apply(patch, original).Value);
    }

    [Fact]
    public void A_file_that_shrank_is_patched_to_its_new_length()
    {
        byte[] original = Bytes(9_000, 3);
        byte[] edited = Bytes(5_000, 3);

        byte[] patch = BytePatch.Make(original, edited, Path).Value;

        Assert.Equal(edited, BytePatch.Apply(patch, original).Value);
    }

    [Fact]
    public void A_patch_carries_the_archive_path_so_apply_knows_where_it_goes()
    {
        byte[] original = Bytes(100, 0);
        byte[] edited = Bytes(100, 1);

        PatchHeader header = BytePatch.Describe(BytePatch.Make(original, edited, Path).Value).Value;

        Assert.Equal(Path, header.VirtualPath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), header.OriginalSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(edited)), header.ResultSha256);
        Assert.Equal(100, header.ResultLength);
    }

    [Fact]
    public void Patching_a_file_to_itself_is_refused()
    {
        byte[] same = Bytes(100, 0);

        Assert.True(BytePatch.Make(same, same, Path).IsRefused);
    }

    [Fact]
    public void Building_the_same_patch_twice_gives_the_same_bytes()
    {
        // A patch is a thing people share and compare. Two builds from one
        // source must be one file.
        byte[] original = Bytes(30_000, 0);
        byte[] edited = Bytes(30_000, 5);

        Assert.Equal(
            BytePatch.Make(original, edited, Path).Value,
            BytePatch.Make(original, edited, Path).Value);
    }

    [Fact]
    public void A_patch_from_a_later_build_says_so_rather_than_denying_it_is_one()
    {
        // Once somebody else holds a patch file, how this build reads its first
        // bytes is fixed. "Not a patch" would send them looking for the wrong
        // problem entirely.
        byte[] patch = BytePatch.Make(Bytes(500, 0), Bytes(500, 1), Path).Value;
        patch[14] = 9;

        Result<PatchHeader> described = BytePatch.Describe(patch);

        Assert.True(described.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, described.Refusal.Kind);
        Assert.Contains("version 9", described.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Something_that_is_not_a_patch_is_refused()
    {
        Assert.True(BytePatch.Apply("not a patch at all, not even close"u8, [1, 2, 3]).IsRefused);
        Assert.True(BytePatch.Describe([]).IsRefused);
    }

    [Fact]
    public void A_truncated_patch_is_malformed_rather_than_a_crash()
    {
        byte[] patch = BytePatch.Make(Bytes(9_000, 0), Bytes(9_000, 1), Path).Value;

        for (int length = 1; length < patch.Length; length += 97)
        {
            Result<byte[]> applied = BytePatch.Apply(patch.AsSpan(0, length), Bytes(9_000, 0));
            Assert.True(applied.IsRefused, $"a patch cut to {length} bytes was accepted");
        }
    }

    [Fact]
    public void A_corrupted_patch_body_does_not_yield_a_plausible_file()
    {
        // The result digest is the backstop: damage anywhere in the payload
        // must surface as a refusal rather than as a file that looks fine.
        byte[] original = Bytes(9_000, 0);
        byte[] edited = Bytes(9_000, 0);
        edited[5_000] ^= 0xFF;

        byte[] patch = BytePatch.Make(original, edited, Path).Value;

        for (int at = patch.Length - 40; at < patch.Length; at++)
        {
            byte[] damaged = (byte[])patch.Clone();
            damaged[at] ^= 0xFF;

            Result<byte[]> applied = BytePatch.Apply(damaged, original);
            if (!applied.IsRefused)
            {
                Assert.Equal(edited, applied.Value);
            }
        }
    }

    // --- A file the game never had, carried whole.

    [Fact]
    public void An_addition_round_trips_without_an_original()
    {
        byte[] mine = [.. Enumerable.Range(0, 9000).Select(i => (byte)(i * 37))];

        Result<byte[]> patch = BytePatch.MakeAddition(mine, "camel/mods/tex_mine_d.dds");
        Assert.False(patch.IsRefused, patch.IsRefused ? patch.Refusal.Message : null);

        Result<byte[]> applied = BytePatch.Apply(patch.Value, ReadOnlySpan<byte>.Empty);

        Assert.False(applied.IsRefused, applied.IsRefused ? applied.Refusal.Message : null);
        Assert.Equal(mine, applied.Value);
    }

    [Fact]
    public void An_addition_says_it_is_one()
    {
        // The recipient has to know whether to go and find a file of their own
        // before this can be applied, and only the patch can tell them.
        Result<byte[]> patch = BytePatch.MakeAddition([1, 2, 3], "camel/mods/a.dds");
        PatchHeader header = BytePatch.Describe(patch.Value).Value;

        Assert.True(header.IsNewFile);
        Assert.Equal(0, header.OriginalLength);
        Assert.Equal("camel/mods/a.dds", header.VirtualPath);
    }

    [Fact]
    public void A_patch_against_a_shipped_file_is_not_an_addition()
    {
        Result<byte[]> patch = BytePatch.Make([9, 9, 9], [1, 2, 3], "camel/x.dds");

        Assert.False(BytePatch.Describe(patch.Value).Value.IsNewFile);
    }

    [Fact]
    public void Applying_an_addition_over_a_file_that_exists_is_refused()
    {
        // It claims a zero-length original, so handing it a real file means the
        // two disagree about what is being patched — the same protection an
        // ordinary patch gets from its digest.
        Result<byte[]> patch = BytePatch.MakeAddition([1, 2, 3], "camel/mods/a.dds");
        Result<byte[]> applied = BytePatch.Apply(patch.Value, [4, 5, 6]);

        Assert.True(applied.IsRefused);
    }

    [Fact]
    public void An_empty_file_is_not_something_to_add()
    {
        Result<byte[]> patch = BytePatch.MakeAddition(ReadOnlySpan<byte>.Empty, "camel/mods/a.dds");

        Assert.True(patch.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, patch.Refusal.Kind);
    }

    private static System.Collections.Generic.IEnumerable<byte[]> Windows(byte[] haystack, int size)
    {
        for (int i = 0; i + size <= haystack.Length; i++)
        {
            yield return haystack[i..(i + size)];
        }
    }
}

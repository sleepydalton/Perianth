using System;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Cameldata;

/// <summary>
/// Synthetic cover for the writer, aimed at what the corpus cannot see.
/// </summary>
/// <remarks>
/// The corpus round trip in <see cref="CameldataCorpusTests"/> is the primary
/// oracle and it is strong: 1,612 files written back byte-identically. But it can
/// only check behaviour the files exercise, and mutation testing found where it
/// cannot. <strong>Every corpus file accounts for every byte</strong>, so
/// dropping the preserved trailing bytes leaves all 1,612 passing. These tests
/// are that gap, plus the refusals, which by design no decoded file can reach.
/// </remarks>
public sealed class CameldataWriterTests
{
    [Fact]
    public void A_synthetic_mode_3_file_survives_the_round_trip()
    {
        CameldataBuilder builder = new()
        {
            Mode = 3,
            ConstantCount = 2,
            PackedFlags = 0x21,
            Xy = [new(1, 2), new(3, 4), new(5, 6)],
            Z = [0.5f, 1.5f],
            Uv0 = [0x1234u, 0xFFFFu],
            PackedZ = [0xDEADBEEFu],
        };

        AssertRoundTrips(builder.Build());
    }

    [Fact]
    public void A_mode_2_file_survives_the_round_trip()
    {
        CameldataBuilder builder = new()
        {
            Mode = 2,
            ConstantCount = 2,
            Positions = [new(1, 2, 3), new(4, 5, 6)],
        };

        AssertRoundTrips(builder.Build());
    }

    [Fact]
    public void The_trailing_bytes_the_corpus_never_has_are_written_back()
    {
        // The blind spot, found by mutating the writer: no corpus file carries
        // trailing bytes, so dropping them there changes nothing and all 1,612
        // still pass. Only a file built to have them can say the writer keeps
        // them, and the reader keeps them precisely so it can.
        CameldataBuilder builder = new() { Mode = 3, Trailing = [0xC0, 0xFF, 0xEE] };

        AssertRoundTrips(builder.Build());
    }

    [Fact]
    public void The_Bezier_block_is_preserved_rather_than_recounted()
    {
        // Skipped by the reader and never interpreted, which is exactly why a
        // writer might emit the right number of zeroes instead. Distinct values
        // here would catch that.
        CameldataBuilder builder = new() { Mode = 3, BezierWords = [0xBE000001u, 0xBE000002u, 0xBE000003u] };

        AssertRoundTrips(builder.Build());
    }

    [Fact]
    public void The_optional_constant_tail_is_preserved_when_the_flag_asks_for_it()
    {
        CameldataBuilder builder = new() { Mode = 3, Flags = 1, ConstantCount = 2 };

        AssertRoundTrips(builder.Build());
    }

    [Fact]
    public void The_packed_flags_are_written_whole_rather_than_rebuilt_from_their_named_bits()
    {
        // Bits 0 to 7 have names -- unified UV0, the UV scale index, the Z bit
        // width -- and bits 8 upward do not. A writer rebuilding the word from
        // the named parts would drop the rest, and the corpus does catch that,
        // so this pins the behaviour where the reason for it is visible.
        CameldataBuilder builder = new() { Mode = 3, PackedFlags = 0xABCD_0021u };

        AssertRoundTrips(builder.Build());

        Mode3Cameldata file = (Mode3Cameldata)ReadOrThrow(builder.Build());
        Assert.Equal(0xABCD_0021u, file.Constants[0].PackedFlags);
        Assert.True(file.Constants[0].UsesUnifiedUv0);
    }

    [Fact]
    public void Negative_zero_is_not_flattened_to_zero()
    {
        // A value arithmetic would erase and a bit copy keeps. Nothing in the
        // writer touches a float on the way through, and this is what says so.
        CameldataBuilder builder = new() { Mode = 3, Xy = [new(-0.0f, 0.0f)], Z = [-0.0f] };

        byte[] original = builder.Build();
        AssertRoundTrips(original);

        Mode3Cameldata file = (Mode3Cameldata)ReadOrThrow(original);
        Assert.True(float.IsNegative(file.Xy[0].X));
        Assert.False(float.IsNegative(file.Xy[0].Y));
    }

    [Fact]
    public void A_constant_whose_preserved_block_is_the_wrong_length_refuses()
    {
        // Unreachable from a decoded file: the stride fixes both lengths. It
        // arises from a file assembled in code, which a geometry edit does, and
        // a short block would shift every byte after it into a file that still
        // parses.
        Mode3Cameldata file = OneMode3Constant();
        Mode3Constant constant = file.Constants[0] with { DataIndices = new byte[15] };

        Result<byte[]> written = CameldataWriter.Write(Rebuilt(file, [constant]));

        Assert.True(written.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, written.Refusal.Kind);
        Assert.Contains("15 data-index bytes", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tail_the_flag_did_not_ask_for_refuses()
    {
        Mode3Cameldata file = OneMode3Constant();
        Mode3Constant constant = file.Constants[0] with { OptionalTail = new byte[8] };

        Result<byte[]> written = CameldataWriter.Write(Rebuilt(file, [constant]));

        Assert.True(written.IsRefused);
        Assert.Contains("8 tail bytes", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Bezier_count_that_disagrees_with_its_bytes_refuses()
    {
        Mode3Cameldata file = OneMode3Constant();
        // A count no fixture can arrive at by accident. It used to be 2, which
        // is what a one-vertex record's coverage happens to occupy once the
        // builder started declaring real ranges — so the test passed by
        // agreeing rather than by refusing.
        Mode3Cameldata mismatched = new(
            file.Path, file.HeaderWord, file.Flags, bezierWordCount: 99, file.BezierBytes,
            file.Constants, file.Xy, file.Z, file.Uv0, file.PackedZ, file.TrailingBytes);

        Result<byte[]> written = CameldataWriter.Write(mismatched);

        Assert.True(written.IsRefused);
        Assert.Contains("99 Bezier words", written.Refusal.Message, StringComparison.Ordinal);
    }

    private static Mode3Cameldata OneMode3Constant() =>
        (Mode3Cameldata)ReadOrThrow(new CameldataBuilder { Mode = 3 }.Build());

    private static Mode3Cameldata Rebuilt(Mode3Cameldata file, ImmutableArray<Mode3Constant> constants) =>
        new(file.Path, file.HeaderWord, file.Flags, file.BezierWordCount, file.BezierBytes,
            constants, file.Xy, file.Z, file.Uv0, file.PackedZ, file.TrailingBytes);

    private static void AssertRoundTrips(byte[] original)
    {
        Result<byte[]> written = CameldataWriter.Write(ReadOrThrow(original));

        Assert.False(written.IsRefused, written.IsRefused ? written.Refusal.Message : string.Empty);
        Assert.Equal(original, written.Value);
    }

    private static CameldataFile ReadOrThrow(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cameldata-{Guid.NewGuid():N}.cameldata");
        File.WriteAllBytes(path, bytes);
        try
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            Assert.False(source.IsRefused, source.IsRefused ? source.Refusal.Message : string.Empty);

            Result<CameldataFile> file = CameldataReader.Read(source.Value);
            Assert.False(file.IsRefused, file.IsRefused ? file.Refusal.Message : string.Empty);
            return file.Value;
        }
        finally
        {
            File.Delete(path);
        }
    }
}

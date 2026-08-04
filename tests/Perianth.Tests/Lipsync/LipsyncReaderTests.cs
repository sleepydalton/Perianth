using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Lipsync;
using Xunit;

namespace Perianth.Tests.Lipsync;

/// <summary>
/// Decodes the compact BVM lip-sync schedule. Every database here is assembled
/// from the field layout in specification section 10; nothing comes from a game
/// file.
/// </summary>
public sealed class LipsyncReaderTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"bvm-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void The_schedule_for_the_requested_id_is_returned()
    {
        byte[] bytes = Database(
            ("100", [(0, 5), (10, 1)]),
            ("200", [(0, 1), (5, 1)]));

        ImmutableArray<LipsyncPair> schedule = LipsyncReader.ReadSchedule(Source(bytes), "100").Value;

        Assert.Equal(2, schedule.Length);
        Assert.Equal(new LipsyncPair(0, 5), schedule[0]);
        Assert.Equal(new LipsyncPair(10, 1), schedule[1]);
    }

    [Fact]
    public void A_negative_preroll_key_time_decodes_signed()
    {
        byte[] bytes = Database(("100", [(-5, 3), (5, 1)]));

        ImmutableArray<LipsyncPair> schedule = LipsyncReader.ReadSchedule(Source(bytes), "100").Value;

        Assert.Equal(-5, schedule[0].KeyTime);
    }

    [Fact]
    public void An_absent_speech_id_is_unsupported()
    {
        byte[] bytes = Database(("100", [(0, 1), (5, 1)]));

        Result<ImmutableArray<LipsyncPair>> result = LipsyncReader.ReadSchedule(Source(bytes), "999");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_schedule_of_one_pair_has_no_complete_interval()
    {
        byte[] bytes = Database(("100", [(0, 1)]));

        Result<ImmutableArray<LipsyncPair>> result = LipsyncReader.ReadSchedule(Source(bytes), "100");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void An_unresolved_selector_is_unsupported(int selector)
    {
        byte[] bytes = Database(("100", [(0, selector), (5, 1)]));

        Result<ImmutableArray<LipsyncPair>> result = LipsyncReader.ReadSchedule(Source(bytes), "100");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_file_that_is_not_a_BVM_database_is_malformed()
    {
        Result<ImmutableArray<LipsyncPair>> result = LipsyncReader.ReadSchedule(Source([0x00, 0x42, 0x56, 0x4D]), "100");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void Trailing_data_is_malformed()
    {
        List<byte> bytes = [.. Database(("100", [(0, 1), (5, 1)])), 0x00];

        Result<ImmutableArray<LipsyncPair>> result = LipsyncReader.ReadSchedule(Source([.. bytes]), "100");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    // --- fixtures ------------------------------------------------------------

    private SourceFile Source(byte[] bytes)
    {
        string path = Path.Combine(_directory.FullName, $"s{Guid.NewGuid():N}.mlipsyncdatabase");
        File.WriteAllBytes(path, bytes);
        return SourceFileReader.Read(path).Value;
    }

    private static byte[] Database(params (string Id, (int KeyTime, int Selector)[] Pairs)[] entries)
    {
        List<byte> bytes = [0xFF, (byte)'B', (byte)'V', (byte)'M'];
        Compact(bytes, entries.Length);
        foreach ((string id, _) in entries)
        {
            Compact(bytes, id.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(id));
        }

        bytes.Add(0x01);
        bytes.Add(0x00);
        Compact(bytes, entries.Length);
        for (int ordinal = 0; ordinal < entries.Length; ordinal++)
        {
            bytes.Add(0x0D);
            Compact(bytes, ordinal);
            Array(bytes, entries[ordinal].Pairs.Length);
            foreach ((int keyTime, int selector) in entries[ordinal].Pairs)
            {
                Array(bytes, 2);
                bytes.Add(0x04);
                CompactSigned(bytes, keyTime);
                bytes.Add(0x04);
                Compact(bytes, selector);
            }
        }

        return [.. bytes];
    }

    private static void Array(List<byte> bytes, int count)
    {
        bytes.Add(0x01);
        Compact(bytes, count);
        bytes.Add(0x00);
    }

    /// <summary>The single-byte form of the compact encoding, valid for 0..63.</summary>
    private static void Compact(List<byte> bytes, int value)
    {
        Assert.InRange(value, 0, 63);
        bytes.Add((byte)value);
    }

    /// <summary>The single-byte signed compact form, valid for -32..31.</summary>
    private static void CompactSigned(List<byte> bytes, int value)
    {
        Assert.InRange(value, -32, 31);
        bytes.Add((byte)(value & 0x3F));
    }
}

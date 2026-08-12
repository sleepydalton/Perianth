using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Anim;

public sealed class AnimReaderTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"anim-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_static_translation_decodes_a_packed_float3()
    {
        // highX=16, packed exponent 15 (m = 2^-4 = 0.0625): x = (16<<4)*0.0625 = 16.
        byte[] entry = Fixed3(16, 0, 0);

        AnimFile anim = Read(hierarchy: false, nodeCount: 1, layout: 5,
            ("DTRA", entry),
            ("TRAI", Selectors(0x8000)),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("root")));

        AnimVec3 t = anim.DecodeTranslation(0, 0).Value;
        Assert.Equal(16.0, t.X, 9);
        Assert.Equal(0.0, t.Y, 9);
        Assert.Equal(0.0, t.Z, 9);
    }

    [Fact]
    public void A_sentinel_selector_reads_as_the_channel_identity()
    {
        AnimFile anim = Read(hierarchy: false, nodeCount: 1, layout: 5,
            ("TRAI", Selectors(0xFFFF)),
            ("SCAI", Selectors(0xFFFE)),
            ("NAME", Names("root")));

        Assert.Equal(AnimVec3.Zero, anim.DecodeTranslation(0, 0).Value);
        Assert.Equal(AnimVec3.One, anim.DecodeScale(0, 0).Value);
        Assert.Equal(AnimQuat.Identity, anim.DecodeRotation(0, 0).Value);
    }

    [Fact]
    public void A_three_byte_rotation_decodes_by_its_code()
    {
        // fixed = 16384<<5 = 524288, s = 1, companion = 0, code 1 -> (s,0,0,c).
        byte[] entry = [0x00, 0x40, 0x20];

        AnimFile anim = Read(hierarchy: false, nodeCount: 1, layout: 5,
            ("DROT", entry),
            ("ROTI", Selectors(0x8000)),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("root")));

        AnimQuat q = anim.DecodeRotation(0, 0).Value;
        Assert.Equal(1.0, q.X, 9);
        Assert.Equal(0.0, q.Y, 9);
        Assert.Equal(0.0, q.Z, 9);
        Assert.Equal(0.0, q.W, 9);
    }

    [Fact]
    public void An_unobserved_three_byte_rotation_code_refuses()
    {
        // code 0 (top three bits clear) is unused within this codec.
        byte[] entry = [0x00, 0x40, 0x00];

        Result<AnimFile> anim = TryRead(hierarchy: false, nodeCount: 1, layout: 5,
            ("DROT", entry),
            ("ROTI", Selectors(0x8000)),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("root")));

        Assert.True(anim.IsSuccess);
        Assert.True(anim.Value.DecodeRotation(0, 0).IsRefused);
    }

    [Fact]
    public void A_six_byte_smallest_three_rotation_recovers_the_omitted_component()
    {
        // a=b=c encode near zero, omitted index 3 (w): decodes near identity.
        byte[] entry = [0x00, 0xC0, 0x00, 0xC0, 0x00, 0x40];

        AnimFile anim = Read(hierarchy: false, nodeCount: 1, layout: 2,
            ("DROT", entry),
            ("ROTI", Selectors(0x8000)),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("root")));

        AnimQuat q = anim.DecodeRotation(0, 0).Value;
        Assert.Equal(0.0, q.X, 3);
        Assert.Equal(0.0, q.Y, 3);
        Assert.Equal(0.0, q.Z, 3);
        Assert.Equal(1.0, q.W, 3);
    }

    [Fact]
    public void A_six_byte_rotation_with_the_reserved_bit_set_refuses()
    {
        byte[] entry = [0x00, 0xC0, 0x00, 0xC0, 0x00, 0xC0];

        AnimFile anim = Read(hierarchy: false, nodeCount: 1, layout: 2,
            ("DROT", entry),
            ("ROTI", Selectors(0x8000)),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("root")));

        Assert.True(anim.DecodeRotation(0, 0).IsRefused);
    }

    [Fact]
    public void An_eight_byte_rotation_is_accepted_at_unit_norm_and_refused_off_it()
    {
        // (0,0,0,1) at scale 2^-14: w = 16384.
        byte[] unit = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40];
        AnimFile ok = Read(hierarchy: false, nodeCount: 1, layout: 0,
            ("DROT", unit), ("ROTI", Selectors(0x8000)), ("SCAI", Selectors(0xFFFF)), ("NAME", Names("root")));
        AnimQuat q = ok.DecodeRotation(0, 0).Value;
        Assert.Equal(1.0, q.W, 9);

        // (x=1,y=1) at the same scale has norm sqrt(2): the derived-scale guard fires.
        byte[] offNorm = [0x00, 0x40, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00];
        AnimFile bad = Read(hierarchy: false, nodeCount: 1, layout: 0,
            ("DROT", offNorm), ("ROTI", Selectors(0x8000)), ("SCAI", Selectors(0xFFFF)), ("NAME", Names("root")));
        Assert.True(bad.DecodeRotation(0, 0).IsRefused);
    }

    [Fact]
    public void An_unknown_rotation_layout_selector_refuses_by_number()
    {
        Result<AnimFile> anim = TryRead(hierarchy: false, nodeCount: 1, layout: 7,
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("root")));

        Assert.True(anim.IsRefused);
    }

    [Fact]
    public void A_change_key_compressed_channel_selects_by_sample()
    {
        // Two animated channels, one change sample at 2, payload [A0,A1,B0].
        // Channel 0 resolves to A0 at 0 and 1 and A1 from 2; channel 1 is B0.
        byte[] payload = Concat(Fixed3(16, 0, 0), Fixed3(32, 0, 0), Fixed3(48, 0, 0));
        byte[] section = Concat(
            Chunk("TRAD", payload),
            Chunk("CHAK", U16(2)),
            Chunk("CAKS", U16(0, 1, 1)));

        AnimFile anim = Read(hierarchy: false, nodeCount: 2, layout: 5,
            ("TRAI", Selectors(0, 1)),
            ("__RAW__", section),
            ("SCAI", Selectors(0xFFFF, 0xFFFF)),
            ("NAME", Names("a", "b")));

        Assert.Equal(16.0, anim.DecodeTranslation(0, 0).Value.X, 9);
        Assert.Equal(16.0, anim.DecodeTranslation(0, 1).Value.X, 9);
        Assert.Equal(32.0, anim.DecodeTranslation(0, 2).Value.X, 9);
        Assert.Equal(48.0, anim.DecodeTranslation(1, 0).Value.X, 9);
        Assert.Equal(48.0, anim.DecodeTranslation(1, 5).Value.X, 9);
    }

    [Fact]
    public void A_flat_payload_holds_every_channel_at_one_sample_before_the_next()
    {
        // Two channels and two samples, which is the smallest shape where the
        // orderings differ — and the reason nothing caught this for so long is
        // that every other flat test here animates a single channel, where
        // sample-major and channel-major are the same bytes.
        //
        // Laid out [ch0@s0, ch1@s0, ch0@s1, ch1@s1] = 16, 32, 48, 64. Read
        // channel-major instead, channel 0 would give 16 then 32, and the
        // second half of the clip would be another joint's motion — which is
        // exactly how a real character came to flail. Roadmap §10.5.
        byte[] section = Concat(Chunk("TRAD", Concat(
            Fixed3(16, 0, 0), Fixed3(32, 0, 0), Fixed3(48, 0, 0), Fixed3(64, 0, 0))));

        AnimFile anim = Read(hierarchy: false, nodeCount: 2, layout: 5,
            ("TRAI", Selectors(0, 1)),
            ("__RAW__", section),
            ("SCAI", Selectors(0xFFFF, 0xFFFF)),
            ("NAME", Names("a", "b")));

        Assert.Equal(16.0, anim.DecodeTranslation(0, 0).Value.X, 9);
        Assert.Equal(32.0, anim.DecodeTranslation(1, 0).Value.X, 9);
        Assert.Equal(48.0, anim.DecodeTranslation(0, 1).Value.X, 9);
        Assert.Equal(64.0, anim.DecodeTranslation(1, 1).Value.X, 9);
    }

    [Fact]
    public void A_fractional_position_linearly_interpolates_translation()
    {
        // One animated channel, two samples: 0 then 64. Halfway is 32.
        byte[] section = Concat(Chunk("TRAD", Concat(Fixed3(0, 0, 0), Fixed3(64, 0, 0))));

        AnimFile anim = Read(hierarchy: false, nodeCount: 1, layout: 5,
            ("TRAI", Selectors(0)),
            ("__RAW__", section),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("n")));

        Assert.Equal(0.0, anim.TranslationAt(0, 0.0).Value.X, 9);
        Assert.Equal(32.0, anim.TranslationAt(0, 0.5).Value.X, 9);
        Assert.Equal(64.0, anim.TranslationAt(0, 1.0).Value.X, 9);
    }

    [Fact]
    public void Sample_position_maps_time_and_refuses_past_the_end()
    {
        // The builder writes 8 samples at 30 fps: the last sampleable time is
        // 7/30s, and a time past it refuses without faulting the file.
        AnimFile anim = Read(hierarchy: false, nodeCount: 1, layout: 5,
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("n")));

        Assert.Equal(0.0, anim.SamplePosition(0.0).Value, 9);
        Assert.Equal(7.0, anim.SamplePosition(7.0 / 30.0).Value, 6);

        Result<double> past = anim.SamplePosition(1.0);
        Assert.True(past.IsRefused);
        Assert.Equal(Perianth.Formats.Diagnostics.RefusalKind.Unsupported, past.Refusal.Kind);
    }

    [Fact]
    public void A_companion_track_holds_its_last_sample_instead_of_refusing()
    {
        // A setup ANIM is a rest pose and is routinely far shorter than the clip
        // played against it — three samples, an eighth of a second, against
        // clips of several seconds — so sampling it at the clip's time refused
        // and took every one of that character's animations with it.
        //
        // The distinction that has to survive is above: a --time past the end of
        // the file the user actually named is still a refusal, because there the
        // request is for a moment that does not exist. Only a companion clamps.
        AnimFile anim = Read(hierarchy: false, nodeCount: 1, layout: 5,
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("n")));

        Assert.Equal(7.0, anim.ClampedSamplePosition(1.0).Value, 9);
        Assert.Equal(7.0, anim.ClampedSamplePosition(1000.0).Value, 9);

        // Within range it is the same function, so a clamp cannot quietly
        // become a different mapping for times that always worked.
        Assert.Equal(anim.SamplePosition(0.0).Value, anim.ClampedSamplePosition(0.0).Value, 9);
        Assert.Equal(anim.SamplePosition(5.0 / 30.0).Value, anim.ClampedSamplePosition(5.0 / 30.0).Value, 9);

        // And it is a clamp, not a licence: the request still has to make sense.
        Assert.True(anim.ClampedSamplePosition(-1.0).IsRefused);
        Assert.True(anim.ClampedSamplePosition(double.NaN).IsRefused);
    }

    [Fact]
    public void A_straddling_chunk_tag_in_a_payload_is_not_mistaken_for_a_chunk()
    {
        // "DROT" followed by a payload beginning 'I' spells "ROTI" one byte into
        // the chunk. There is no real ROTI stream, so rotation must stay all
        // sentinels (identity). An unbounded search would find the straddling
        // "ROTI" inside DROT, read selectors from its payload, and pose a
        // rotation the file does not contain.
        AnimFile anim = Read(hierarchy: false, nodeCount: 1, layout: 5,
            ("DROT", Concat("I"u8.ToArray(), new byte[6])),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("root")));

        Assert.Equal(0xFFFF, anim.Selector(AnimChannel.Rotation, 0));
        Assert.Equal(AnimQuat.Identity, anim.DecodeRotation(0, 0).Value);
    }

    [Fact]
    public void A_missing_required_chunk_refuses()
    {
        Result<AnimFile> noScai = TryRead(hierarchy: false, nodeCount: 1, layout: 5,
            ("NAME", Names("root")));
        Assert.True(noScai.IsRefused);
    }

    [Fact]
    public void Duplicate_node_names_refuse()
    {
        Result<AnimFile> anim = TryRead(hierarchy: false, nodeCount: 2, layout: 5,
            ("SCAI", Selectors(0xFFFF, 0xFFFF)),
            ("NAME", Names("dup", "dup")));
        Assert.True(anim.IsRefused);
    }

    [Fact]
    public void A_setup_without_a_parent_hierarchy_refuses()
    {
        Result<AnimFile> anim = TryRead(hierarchy: true, nodeCount: 1, layout: 5,
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", Names("root")));
        Assert.True(anim.IsRefused);
    }

    [Fact]
    public void A_cyclic_parent_hierarchy_refuses()
    {
        // Node 0's parent is 1 and node 1's parent is 0.
        Result<AnimFile> anim = TryRead(hierarchy: true, nodeCount: 2, layout: 5,
            ("SCAI", Selectors(0xFFFF, 0xFFFF)),
            ("NAME", Names("a", "b")),
            ("PRNT", U16(1, 0)));
        Assert.True(anim.IsRefused);
    }

    [Fact]
    public void A_valid_parent_hierarchy_is_read()
    {
        AnimFile anim = Read(hierarchy: true, nodeCount: 2, layout: 5,
            ("SCAI", Selectors(0xFFFF, 0xFFFF)),
            ("NAME", Names("root", "child")),
            ("PRNT", U16(0xFFFF, 0)));

        Assert.Equal(AnimFile.Root, anim.Parents[0]);
        Assert.Equal(0, anim.Parents[1]);
        Assert.True(anim.TryGetNode("child", out int index));
        Assert.Equal(1, index);
    }

    // --- synthetic ANIM assembly ---------------------------------------------

    private AnimFile Read(bool hierarchy, int nodeCount, uint layout, params (string Tag, byte[] Payload)[] chunks)
    {
        Result<AnimFile> result = TryRead(hierarchy, nodeCount, layout, chunks);
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private Result<AnimFile> TryRead(bool hierarchy, int nodeCount, uint layout, params (string Tag, byte[] Payload)[] chunks)
    {
        List<byte> bytes = [.. new byte[0x3C]];
        "ANIM"u8.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..4]);
        Write32(bytes, 0x08, BitConverter.SingleToInt32Bits(30.0f));
        Write32(bytes, 0x10, 8);            // sampleCount
        Write32(bytes, 0x1C, (int)layout);  // rotation layout selector
        Write32(bytes, 0x24, nodeCount);    // nodeCount

        foreach ((string tag, byte[] payload) in chunks)
        {
            // "__RAW__" splices pre-assembled chunk bytes (an animated data
            // section) verbatim; anything else is one tag-and-payload chunk.
            bytes.AddRange(tag == "__RAW__" ? payload : Chunk(tag, payload));
        }

        string path = Path.Combine(_directory.FullName, $"a{Guid.NewGuid():N}.anim");
        File.WriteAllBytes(path, [.. bytes]);
        SourceFile file = SourceFileReader.Read(path).Value;
        return AnimReader.Read(file, hierarchy);
    }

    private static byte[] Chunk(string tag, byte[] payload) => Concat(Encoding.ASCII.GetBytes(tag), payload);

    private static byte[] Selectors(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)(values[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)((values[i] >> 8) & 0xFF);
        }

        return bytes;
    }

    private static byte[] U16(params int[] values) => Selectors(values);

    private static byte[] Names(params string[] names)
    {
        List<byte> bytes = [];
        foreach (string name in names)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes(name));
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static byte[] Fixed3(int x, int y, int z)
    {
        // With exponent 15 (m = 2^-4) and the low nibbles clear, a high word h
        // decodes to (h << 4) * 0.0625 = h, so the decoded component equals the
        // high word given here.
        byte[] entry = new byte[8];
        WriteI16(entry, 0, (short)x);
        WriteI16(entry, 2, (short)y);
        WriteI16(entry, 4, (short)z);
        entry[6] = 0x0F; // packed: exponent 15, low nibbles 0
        entry[7] = 0x00;
        return entry;
    }

    private static void WriteI16(byte[] bytes, int offset, short value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void Write32(List<byte> bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        List<byte> bytes = [];
        foreach (byte[] part in parts)
        {
            bytes.AddRange(part);
        }

        return [.. bytes];
    }
}

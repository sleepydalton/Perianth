using System;
using System.IO;
using System.Numerics;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Cameldata;

public sealed class CameldataReaderTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-camel-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_mode_two_file_yields_constants_and_an_absolute_position_pool()
    {
        Mode2Cameldata file = Assert.IsType<Mode2Cameldata>(ReadOk(new CameldataBuilder
        {
            Mode = 2,
            Positions = [new(1, 2, 3), new(4, 5, 6)],
        }));

        Assert.Equal(2, file.Mode);
        Assert.Equal(0, file.Flags);
        Assert.Single(file.Constants);
        Assert.Equal([new Vector3(1, 2, 3), new Vector3(4, 5, 6)], file.Positions);
    }

    [Fact]
    public void A_mode_three_file_yields_its_four_counted_arrays()
    {
        Mode3Cameldata file = Assert.IsType<Mode3Cameldata>(ReadOk(new CameldataBuilder
        {
            Mode = 3,
            Xy = [new(1, 2), new(3, 4)],
            Z = [5f, 6f, 7f],
            Uv0 = [0x1111_1111, 0x2222_2222],
            PackedZ = [0xDEAD_BEEF],
        }));

        Assert.Equal(3, file.Mode);
        Assert.Equal(2, file.Xy.Length);
        Assert.Equal([5f, 6f, 7f], file.Z);
        Assert.Equal(2, file.Uv0.Length);
        Assert.Equal([0xDEAD_BEEFu], file.PackedZ);
    }

    [Fact]
    public void The_surface_and_matrix_of_a_constant_are_read_in_serialized_order()
    {
        Mode2Cameldata file = Assert.IsType<Mode2Cameldata>(ReadOk(new CameldataBuilder { Mode = 2 }));
        Mode2Constant constant = file.Constants[0];

        Assert.Equal(new Vector4(1, 1, 2, 3), constant.SurfaceOrigin);
        Assert.Equal(new Vector4(4, 5, 6, 7), constant.SurfaceU);
        Assert.Equal(new Vector4(8, 9, 10, 11), constant.SurfaceV);
        Assert.Equal(new Vector4(0, 1, 2, 3), constant.InverseLocal.Group0);
        Assert.Equal(2f, constant.PositionXScale);
        Assert.Equal(0.5f, constant.InverseUnitScale);

        // Section 5.4 asks for a column of the inverse-local matrix, which is
        // one element from each serialized group.
        Assert.Equal(new Vector4(0, 4, 8, 12), constant.InverseLocal.Column(0));
        Assert.Equal(new Vector4(1, 5, 9, 13), constant.InverseLocal.Column(1));
    }

    [Fact]
    public void The_sixteen_uninterpreted_bytes_of_a_constant_are_kept()
    {
        Mode2Cameldata file = Assert.IsType<Mode2Cameldata>(
            ReadOk(new CameldataBuilder { Mode = 2, ArbitraryDataIndices = true }));

        Assert.Equal(16, file.Constants[0].DataIndices.Length);
        Assert.Equal(0xA0, file.Constants[0].DataIndices.Span[0]);
        Assert.Equal(0xAF, file.Constants[0].DataIndices.Span[15]);
    }

    [Fact]
    public void The_header_flag_adds_an_eight_byte_tail_to_every_constant()
    {
        Mode2Cameldata file = Assert.IsType<Mode2Cameldata>(ReadOk(new CameldataBuilder
        {
            Mode = 2,
            Flags = 1,
            ConstantCount = 2,
        }));

        Assert.Equal(1, file.Flags);
        Assert.All(file.Constants, constant => Assert.Equal(8, constant.OptionalTail.Length));
        Assert.Equal(0xE0, file.Constants[1].OptionalTail.Span[0]);
    }

    [Fact]
    public void Without_the_flag_a_constant_carries_no_tail()
    {
        Mode2Cameldata file = Assert.IsType<Mode2Cameldata>(ReadOk(new CameldataBuilder { Flags = 0 }));

        Assert.True(file.Constants[0].OptionalTail.IsEmpty);
    }

    [Fact]
    public void The_bezier_block_is_skipped_and_kept()
    {
        CameldataFile file = ReadOk(new CameldataBuilder { BezierWords = [0x1234_5678, 0x9ABC_DEF0] });

        Assert.Equal(2, file.BezierWordCount);
        Assert.Equal(8, file.BezierBytes.Length);
        Assert.Equal(0x78, file.BezierBytes.Span[0]);
    }

    [Fact]
    public void Trailing_bytes_are_kept_rather_than_refused()
    {
        // Section 13 warns about these and ignores them, unlike editordata and
        // BVM where they refuse. There is no warning channel yet, so they are
        // preserved for whoever gains one.
        CameldataFile file = ReadOk(new CameldataBuilder { Trailing = [0x01, 0x02, 0x03] });

        Assert.Equal([0x01, 0x02, 0x03], file.TrailingBytes.ToArray());
    }

    [Fact]
    public void A_file_with_nothing_after_its_arrays_has_no_trailing_bytes()
    {
        CameldataFile file = ReadOk(new CameldataBuilder());

        Assert.True(file.TrailingBytes.IsEmpty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_mode_this_build_does_not_implement_is_unsupported_rather_than_malformed(int mode)
    {
        // The header is coherent and the file may be perfectly good. Reporting
        // it as malformed would tell someone their asset is broken.
        Refusal refusal = ReadRefused(new CameldataBuilder { Mode = mode, ConstantCount = 0 });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Equal(DiagnosticIds.FormatUnsupported, refusal.DiagnosticId);
    }

    [Fact]
    public void A_header_setting_reserved_bits_refuses()
    {
        Refusal refusal = ReadRefused(new CameldataBuilder { HeaderWord = 2 | (1u << 5) });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("reserved bits", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_whose_flag_field_exceeds_one_refuses()
    {
        // Bits 2 to 14 stay clear, so only the flag field is at fault.
        Refusal refusal = ReadRefused(new CameldataBuilder { HeaderWord = 2 | (1u << 16) });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("flag field", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_header_refuses()
    {
        Refusal refusal = ReadRefused(new CameldataBuilder { TruncateTo = 8 });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("header is truncated", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_three_with_no_constants_refuses()
    {
        Refusal refusal = ReadRefused(new CameldataBuilder { Mode = 3, ConstantCount = 0 });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("mode 3 with no constants", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_constant_carrying_a_value_that_is_not_finite_refuses(float value)
    {
        Refusal refusal = ReadRefused(new CameldataBuilder { FirstFloat = value });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("constant 0", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_position_that_is_not_finite_refuses()
    {
        Refusal refusal = ReadRefused(new CameldataBuilder
        {
            Positions = [new(1, float.NaN, 3)],
        });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("position 0", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_constant_count_larger_than_the_file_refuses_before_allocating()
    {
        Refusal refusal = ReadRefused(new CameldataBuilder { DeclaredConstantCount = 0x00FF_FFFF });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("constants run past the end", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_position_count_larger_than_the_file_refuses_before_allocating()
    {
        Refusal refusal = ReadRefused(new CameldataBuilder { DeclaredPositionCount = 0x00FF_FFFF });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("position pool runs past the end", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bezier_block_running_past_the_file_refuses()
    {
        byte[] bytes = new CameldataBuilder().Build();
        // Rewrite the declared Bezier word count without adding the words.
        bytes[8] = 0xFF;
        bytes[9] = 0xFF;

        Refusal refusal = ReadRefused(bytes);

        Assert.Contains("Bezier block runs past", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_mode_three_array_refuses()
    {
        byte[] full = new CameldataBuilder { Mode = 3 }.Build();

        Refusal refusal = ReadRefused(new CameldataBuilder { Mode = 3, TruncateTo = full.Length - 2 });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    [Theory]
    [InlineData(0u, false, 0, 1)]
    [InlineData(1u, true, 0, 1)]
    [InlineData(0b0100u, false, 2, 1)]
    [InlineData(0b0000_1000u, false, 0, 2)]
    [InlineData(0b1111_1000u, false, 0, 32)]
    public void The_packed_flags_of_a_mode_three_constant_split_into_three_fields(
        uint packed, bool unified, int scaleIndex, int zBitWidth)
    {
        Mode3Constant constant = new(
            default, default, default, default, 0, 0, 0, packed, default, 0, 0, default);

        Assert.Equal(unified, constant.UsesUnifiedUv0);
        Assert.Equal(scaleIndex, constant.Uv0ScaleIndex);
        Assert.Equal(zBitWidth, constant.ZBitWidth);
    }

    [Fact]
    public void Reading_a_null_file_is_a_fault()
    {
        Assert.Throws<ArgumentNullException>(() => CameldataReader.Read(null!));
    }

    private CameldataFile ReadOk(CameldataBuilder builder)
    {
        Result<CameldataFile> result = CameldataReader.Read(Load(builder.Build()));
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private Refusal ReadRefused(CameldataBuilder builder) => ReadRefused(builder.Build());

    private Refusal ReadRefused(byte[] bytes)
    {
        Result<CameldataFile> result = CameldataReader.Read(Load(bytes));
        Assert.True(result.IsRefused);
        return result.Refusal;
    }

    private SourceFile Load(byte[] bytes)
    {
        string path = Path.Combine(_directory.FullName, $"model-{Guid.NewGuid():N}.cameldata");
        File.WriteAllBytes(path, bytes);
        return SourceFileReader.Read(path).Value;
    }
}

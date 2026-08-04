using System;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Content;

public sealed class TexturePathTests
{
    [Fact]
    public void Backslashes_become_forward_slashes()
    {
        Assert.Equal("a/b/c.dds", NormalizeOk(@"a\b\c.dds"));
    }

    [Fact]
    public void A_suffixless_path_gains_dds()
    {
        // Appended only when there is no suffix at all.
        Assert.Equal("tex/plain.dds", NormalizeOk("tex/plain"));
    }

    [Fact]
    public void A_dds_path_is_left_alone()
    {
        Assert.Equal("tex/already.dds", NormalizeOk("tex/already.dds"));
    }

    [Fact]
    public void A_dds_suffix_is_matched_case_insensitively()
    {
        // The suffix check folds case; the rest of the path does not.
        Assert.Equal("tex/Mixed.DDS", NormalizeOk("tex/Mixed.DDS"));
    }

    [Fact]
    public void Some_other_suffix_is_refused_rather_than_corrected()
    {
        // Appending .dds to a .png would invent a file that is not named.
        Refusal refusal = NormalizeRefused("tex/wrong.png");
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("not a DDS", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Case_is_preserved_in_the_path_body()
    {
        // Loose lookup is case-sensitive, so folding here would resolve the
        // wrong file on a case-sensitive filesystem.
        Assert.Equal("Tex/CamelCase.dds", NormalizeOk("Tex/CamelCase.dds"));
    }

    [Theory]
    [InlineData("/absolute/path.dds")]
    [InlineData("tex/../escape.dds")]
    [InlineData("tex/./here.dds")]
    [InlineData("tex//double.dds")]
    [InlineData("C:/drive/path.dds")]
    [InlineData("tex/stream:name.dds")]
    public void A_path_that_could_escape_the_root_is_refused(string path)
    {
        Refusal refusal = NormalizeRefused(path);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    private static string NormalizeOk(string path)
    {
        Result<string> result = TexturePath.Normalize(path, "DiffuseColor");
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private static Refusal NormalizeRefused(string path)
    {
        Result<string> result = TexturePath.Normalize(path, "DiffuseColor");
        Assert.True(result.IsRefused, "expected a refusal");
        return result.Refusal;
    }
}

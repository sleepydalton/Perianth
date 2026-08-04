using System;
using System.IO;
using System.Text;
using Perianth.Core.Audio;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Audio;

/// <summary>
/// Resolves a numeric speech WEM by its embedded Oasis label. The WEM files here
/// are empty but for the label bytes the resolver keys on; nothing is a real game
/// file.
/// </summary>
public sealed class WemResolverTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("perianth-wemtest-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void A_labelled_wem_resolves_with_its_locale()
    {
        Wem("voice/windows/english(us)/common", "17780", labelled: "17780");

        WemSelection selection = WemResolver.Resolve(_root.FullName, "17780").Value;

        Assert.EndsWith("17780.wem", selection.Path, StringComparison.Ordinal);
        Assert.Equal("english(us)", selection.Locale);
    }

    [Fact]
    public void The_english_variant_is_preferred_over_others()
    {
        Wem("voice/windows/french/common", "17780", labelled: "17780");
        Wem("voice/windows/english(us)/common", "17780", labelled: "17780");

        WemSelection selection = WemResolver.Resolve(_root.FullName, "17780").Value;

        Assert.Equal("english(us)", selection.Locale);
    }

    [Fact]
    public void A_candidate_lacking_the_label_is_not_confirmed()
    {
        Wem("voice/windows/english(us)/common", "17780", labelled: "99999");

        Result<WemSelection> result = WemResolver.Resolve(_root.FullName, "17780");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void Confirmed_variants_with_no_english_and_no_unique_choice_are_ambiguous()
    {
        Wem("voice/windows/french/common", "17780", labelled: "17780");
        Wem("voice/windows/german/common", "17780", labelled: "17780");

        Result<WemSelection> result = WemResolver.Resolve(_root.FullName, "17780");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void No_matching_file_is_unsupported()
    {
        Wem("voice/windows/english(us)/common", "12345", labelled: "12345");

        Result<WemSelection> result = WemResolver.Resolve(_root.FullName, "17780");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("17780.0")]
    [InlineData("")]
    public void A_non_numeric_speech_id_is_unsupported(string speechId)
    {
        Result<WemSelection> result = WemResolver.Resolve(_root.FullName, speechId);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_root_that_is_not_a_directory_is_a_resource_refusal()
    {
        Result<WemSelection> result = WemResolver.Resolve(Path.Combine(_root.FullName, "absent"), "17780");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Resource, result.Refusal.Kind);
    }

    private void Wem(string relativeDir, string stem, string labelled)
    {
        string dir = Path.Combine(_root.FullName, relativeDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        byte[] bytes = [0x00, 0x01, .. Encoding.ASCII.GetBytes($"OasisID{labelled}\0"), 0x02, 0x03];
        File.WriteAllBytes(Path.Combine(dir, $"{stem}.wem"), bytes);
    }
}

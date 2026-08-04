using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Audio;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks finding one voice line among a quarter of a million of them.
/// </summary>
public sealed class SpeechCatalogueTests
{
    private static ImmutableArray<SdfPathEntry> Index(params string[] paths) =>
        [.. paths.Select((path, ordinal) => new SdfPathEntry(path, ordinal + 1, IsDirectory: false))];

    private static ImmutableArray<SdfPathEntry> Voices() => Index(
        "camel/voice/windows/english(us)/common/1003.wem",
        "camel/voice/windows/german/common/1003.wem",
        "camel/voice/windows/french(france)/common/1003.wem",
        "camel/voice/windows/german/common/2222.wem");

    [Fact]
    public void The_chosen_locale_is_the_one_returned()
    {
        Result<SpeechAudio> found = SpeechCatalogue.Find(Voices(), "1003", "german");

        Assert.Equal("camel/voice/windows/german/common/1003.wem", found.Value.Wem);
    }

    [Fact]
    public void A_line_absent_from_the_chosen_locale_names_the_ones_that_have_it()
    {
        // "No audio" and "not in that language" are different answers, and only
        // one of them is fixed by picking another locale.
        Result<SpeechAudio> found = SpeechCatalogue.Find(Voices(), "2222", "english(us)");

        Assert.Null(found.Value.Wem);
        Assert.Equal(["german"], found.Value.Locales);
    }

    [Fact]
    public void An_id_no_locale_carries_reports_nothing_rather_than_refusing()
    {
        // A number that is simply not spoken is an ordinary answer.
        Result<SpeechAudio> found = SpeechCatalogue.Find(Voices(), "9999", "english(us)");

        Assert.False(found.IsRefused);
        Assert.Null(found.Value.Wem);
        Assert.Empty(found.Value.Locales);
    }

    [Fact]
    public void Every_locale_carrying_the_line_is_listed()
    {
        Result<SpeechAudio> found = SpeechCatalogue.Find(Voices(), "1003", "english(us)");

        Assert.Equal(["english(us)", "french(france)", "german"], found.Value.Locales);
    }

    [Fact]
    public void Something_that_is_not_a_number_is_refused()
    {
        Result<SpeechAudio> found = SpeechCatalogue.Find(Voices(), "cartman", "english(us)");

        Assert.True(found.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, found.Refusal.Kind);
    }

    [Fact]
    public void A_shorter_id_does_not_match_a_longer_one()
    {
        // 003.wem must not answer for 1003.wem, which a bare Contains would let
        // happen across a quarter of a million numeric names.
        Result<SpeechAudio> found = SpeechCatalogue.Find(Voices(), "003", "english(us)");

        Assert.Null(found.Value.Wem);
        Assert.Empty(found.Value.Locales);
    }

    [Fact]
    public void Only_the_full_locales_are_offered()
    {
        // The archive also holds female and neutral variant sets of a few
        // hundred lines each; offering them beside the seven complete languages
        // would imply they are interchangeable.
        Assert.Equal(7, SpeechCatalogue.Locales.Length);
        Assert.Contains(SpeechCatalogue.DefaultLocale, SpeechCatalogue.Locales);
    }
}

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Perianth.Core.Audio;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks finding a voice line by what it says.
/// </summary>
public sealed class SubtitleCatalogueTests
{
    // Invented identifiers and invented lines. The fixture exercises the
    // grammar — quoted fields, doubled quotes, markup, empty text — and none of
    // that needs the game's own dialogue, which does not belong in this
    // repository any more than its textures do.
    //
    // This file once held three real Oasis GUIDs and three real lines, and they
    // are still in the history where they cannot be removed. The line below is
    // read by the content scanner, which cannot tell an invented identifier
    // from a real one and so takes the claim from whoever wrote it.
    //
    // scan-ok: identifiers here are invented
    private const string GuidA = "00112233445566778899AABBCCDDEEFF";
    private const string GuidB = "0123456789ABCDEF0123456789ABCDEF";
    private const string GuidC = "FEDCBA9876543210FEDCBA9876543210";

    private static ReadOnlyMemory<byte> Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static SubtitleCatalogue Build(string ids, params string[] packages)
    {
        Result<SubtitleCatalogue> built = SubtitleCatalogue.Read(
            Bytes(ids), [.. packages.Select(Bytes)]);

        Assert.False(built.IsRefused, built.IsRefused ? built.Refusal.Message : null);
        return built.Value;
    }

    private static SubtitleCatalogue Sample() => Build(
        $"3\n{GuidA},5409\n{GuidB},5411\n{GuidC},5688\n",
        $"1,,\n3,,\n{GuidA},0,\"Stand clear!<split time=\"\"0.61\"\">\"\n"
        + $"{GuidB},0,\"Clear off!\"\n{GuidC},0,\"Nothing to see\"\n");

    [Fact]
    public void A_line_is_found_by_what_it_says()
    {
        Result<ImmutableArray<SpokenLine>> found = Sample().Search("clear", limit: 10);

        // "Clear off!" begins with the word and so comes before "Stand clear!",
        // which merely contains it.
        Assert.Equal(["5411", "5409"], found.Value.Select(line => line.SpeechId));
    }

    [Fact]
    public void The_timing_markup_is_not_part_of_the_words()
    {
        // The subtitle carries <split time="0.61"> for the game's own use, and
        // searching for "time" should not match every line that has one.
        SubtitleCatalogue catalogue = Sample();

        Assert.Equal("Stand clear!", catalogue.Line("5409")!.Text);
        Assert.Empty(catalogue.Search("split", limit: 10).Value);
    }

    [Fact]
    public void A_line_starting_with_the_words_comes_before_one_merely_containing_them()
    {
        // Someone typing "clear off" wants the line that is that, not the speech
        // it appears inside.
        SubtitleCatalogue catalogue = Build(
            $"2\n{GuidA},100\n{GuidB},200\n",
            $"{GuidA},0,\"And then I said clear off, didn't I\"\n{GuidB},0,\"Clear off!\"\n");

        Assert.Equal("200", catalogue.Search("clear off", limit: 10).Value[0].SpeechId);
    }

    [Fact]
    public void Searching_ignores_case()
    {
        Assert.Single(Sample().Search("STAND CLEAR", limit: 10).Value);
    }

    [Fact]
    public void A_row_with_no_text_is_not_a_searchable_line()
    {
        // What the barks package is: 5,585 rows of "GUID,0," with an empty text
        // field, because barks are unsubtitled grunts. Including them would
        // offer thousands of blank lines to choose between.
        SubtitleCatalogue catalogue = Build(
            $"2\n{GuidA},100\n{GuidB},200\n",
            $"{GuidA},0,\n{GuidB},0,\"Clear off!\"\n");

        Assert.Equal(1, catalogue.Count);
        Assert.Null(catalogue.Line("100"));
    }

    [Fact]
    public void A_line_whose_guid_is_not_in_the_table_is_skipped()
    {
        SubtitleCatalogue catalogue = Build(
            $"1\n{GuidA},100\n",
            $"{GuidA},0,\"Kept\"\n{GuidB},0,\"Dropped\"\n");

        Assert.Equal(1, catalogue.Count);
        Assert.Empty(catalogue.Search("Dropped", limit: 10).Value);
    }

    [Fact]
    public void The_limit_is_honoured_and_empty_text_is_refused()
    {
        Assert.Equal(2, Sample().Search("!", limit: 2).Value.Length);
        Assert.True(Sample().Search("   ", limit: 10).IsRefused);
    }

    [Fact]
    public void An_identifier_table_with_no_rows_is_refused()
    {
        // Nothing can be joined without it, and silently searching zero lines
        // would look like a corpus with nothing to say.
        Result<SubtitleCatalogue> built = SubtitleCatalogue.Read(Bytes("nonsense\n"), []);

        Assert.True(built.IsRefused);
        Assert.Equal(RefusalKind.Malformed, built.Refusal.Kind);
    }
}

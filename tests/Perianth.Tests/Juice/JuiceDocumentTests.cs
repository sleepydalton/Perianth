using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Juice;
using Xunit;

namespace Perianth.Tests.Juice;

/// <summary>
/// The span index over the game's text configuration language.
/// </summary>
/// <remarks>
/// The claim under test is narrow and load-bearing: an edit changes the bytes it
/// names and nothing else. So most of these assert what is <em>preserved</em>
/// rather than what changed — a reader that quietly normalised indentation, line
/// endings or the trailing blank lines would pass a test that only looked at the
/// edited field.
/// </remarks>
public sealed class JuiceDocumentTests
{
    // The identifiers below are invented -- repeated nibbles, and the hex
    // digits counted up and back down. A uid is 128 opaque bits, so a fixture
    // needs one that is well-formed rather than one that is real, and the
    // game's own belong here no more than its textures do.
    //
    // This line is read by the content scan, which cannot tell an invented
    // identifier from a real one and so takes the claim from whoever wrote it.
    //
    // scan-ok: identifiers here are invented

    private const string Item =
        "include \"camel/game system data/fruit/items/items.fruit\"\n" +
        "\n" +
        "CostumeItemStreetHairBangs made_up_style_hair_bangs < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
        "{\n" +
        "\tmyModel \"[\\\"prefab:Skeleton\\\"] = { MMAFile = \\\"made/up/path.mmb\\\", }\"\n" +
        "\tmyDefaultTint1 AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n" +
        "}\n" +
        "\n";

    private static JuiceDocument Read(string text) =>
        JuiceDocument.Read(SourceFile.FromMemory("made-up.mitem", Encoding.UTF8.GetBytes(text)))
            .Value;

    [Fact]
    public void The_declaration_is_read_as_class_name_and_uid()
    {
        JuiceDocument document = Read(Item);

        Assert.Equal("CostumeItemStreetHairBangs", document.DeclaredClass);
        Assert.Equal("made_up_style_hair_bangs", Text(document, document.NameRange));
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF", Text(document, document.UidRange));
    }

    [Fact]
    public void A_quoted_name_containing_spaces_is_read_whole()
    {
        // The shape that mis-read a majority of shipped items once already.
        JuiceDocument document = Read(
            "ComponentItem \"10-sided Dice\" < uid=0123456789ABCDEF0123456789ABCDEF >\n{\n\tmyMaxStackable 5\n}\n");

        Assert.Equal("ComponentItem", document.DeclaredClass);
        Assert.Equal("\"10-sided Dice\"", Text(document, document.NameRange));
    }

    [Fact]
    public void Reading_and_writing_back_changes_nothing()
    {
        JuiceDocument document = Read(Item);

        Assert.Equal(Item, Encoding.UTF8.GetString(document.Bytes.Span));
    }

    [Fact]
    public void Replacing_a_field_leaves_every_other_byte_alone()
    {
        JuiceDocument edited = Read(Item).WithField("myDefaultTint1", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB").Value;

        string text = Encoding.UTF8.GetString(edited.Bytes.Span);
        Assert.Equal(Item.Replace("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"), text);
    }

    [Fact]
    public void Renaming_moves_the_name_and_the_uid_together()
    {
        JuiceDocument edited = Read(Item)
            .WithDeclaration("brand_new_hair_bangs", "FEDCBA9876543210FEDCBA9876543210").Value;

        string text = Encoding.UTF8.GetString(edited.Bytes.Span);
        Assert.Contains(
            "CostumeItemStreetHairBangs brand_new_hair_bangs < uid=FEDCBA9876543210FEDCBA9876543210 >",
            text,
            System.StringComparison.Ordinal);

        // The name is longer than the one it replaced, so a uid range that was
        // not recomputed would have been spliced over the wrong bytes.
        Assert.Contains("made/up/path.mmb", text, System.StringComparison.Ordinal);
        Assert.EndsWith("}\n\n", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_with_spaces_is_quoted_on_the_way_back_in()
    {
        JuiceDocument edited = Read(Item)
            .WithDeclaration("A Brand New Hat", "FEDCBA9876543210FEDCBA9876543210").Value;

        Assert.Contains(
            "CostumeItemStreetHairBangs \"A Brand New Hat\" <",
            Encoding.UTF8.GetString(edited.Bytes.Span),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_the_declaration_lacks_is_refused_rather_than_added()
    {
        Result<JuiceDocument> result = Read(Item).WithField("myIcon", "made/up/icon.png");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_block_field_is_refused_rather_than_flattened()
    {
        JuiceDocument document = Read(
            "CostumeItemStreetHair made_up < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
            "{\n\tmyExtraPieces\n\t{\n\t\tmyNested 1\n\t}\n}\n");

        Assert.True(document.TryGetField("myExtraPieces", out JuiceField block));
        Assert.True(block.IsBlock);
        Assert.False(document.WithField("myExtraPieces", "1").IsSuccess);
    }

    [Fact]
    public void A_field_inside_a_block_is_not_offered_as_the_declarations_own()
    {
        // "myItem" appears inside a recipe's ingredient list as well as at the
        // top level. Hoisting the nested one would let an edit aimed at the
        // result silently change an ingredient.
        JuiceDocument document = Read(
            "RecipeItemTuningData made_up < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
            "{\n\tmyItem AAAA\n\tmyIngredients\n\t{\n\t\tIngredient 0\n\t\t{\n\t\t\tmyItem BBBB\n\t\t}\n\t}\n}\n");

        Assert.Equal(2, document.Fields.Length);
        Assert.True(document.TryGetField("myItem", out JuiceField item));
        Assert.Equal("AAAA", Text(document, item.Value));
    }

    [Fact]
    public void A_file_with_no_declaration_refuses()
    {
        Result<JuiceDocument> result = JuiceDocument.Read(
            SourceFile.FromMemory("made-up.juice", Encoding.UTF8.GetBytes("include \"a.fruit\"\n\n")));

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcdef", false)]  // shipped uids are upper case
    [InlineData("0123456789ABCDEF0123456789ABCDE", false)]   // 31 digits
    [InlineData("0123456789ABCDEF0123456789ABCDEFF", false)] // 33
    public void A_uid_is_thirty_two_upper_case_hex_digits(string value, bool expected) =>
        Assert.Equal(expected, JuiceDocument.IsUid(value));

    [Fact]
    public void A_brace_inside_a_quoted_value_does_not_end_the_declaration()
    {
        // A shipped vendor's localisation blob ends with a literal '}' inside
        // its quoted text. Counting braces blindly stopped indexing after the
        // first field, and the file still wrote back byte-for-byte — so the
        // corpus oracle could not see it. Only this can.
        JuiceDocument document = Read(
            "VendorConfig \"Made Up\" < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
            "{\n" +
            "\tmyUIName \"text = \\\"Tacos\\\"}\"\n" +
            "\tmyVendorItemList\n\t{\n\t}\n" +
            "\tmyVendorGroup \"made up\"\n" +
            "}\n");

        Assert.Equal(3, document.Fields.Length);
        Assert.True(document.TryGetField("myVendorItemList", out JuiceField list));
        Assert.True(list.IsBlock);
    }

    [Fact]
    public void A_block_field_in_a_CRLF_file_is_still_a_block()
    {
        // Items are LF and vendor configs are CRLF. Leaving the carriage return
        // in the line made every block look like an inline value of "\r".
        JuiceDocument document = Read(
            "VendorConfig \"Made Up\" < uid=0123456789ABCDEF0123456789ABCDEF >\r\n" +
            "{\r\n\tmyVendorItemList\r\n\t{\r\n\t}\r\n}\r\n");

        Assert.True(document.TryGetField("myVendorItemList", out JuiceField list));
        Assert.True(list.IsBlock);
    }

    [Fact]
    public void An_entry_is_appended_at_the_end_of_a_block()
    {
        JuiceDocument document = Read(
            "VendorConfig made_up < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
            "{\n\tmyList\n\t{\n\t\tOne\n\t\t{\n\t\t}\n\t}\n\tmyAfter 1\n}\n");

        string text = Encoding.UTF8.GetString(
            document.WithBlockEntry("myList", "\t\tTwo\n\t\t{\n\t\t}\n").Value.Bytes.Span);

        Assert.Contains("\t\tOne\n\t\t{\n\t\t}\n\t\tTwo\n\t\t{\n\t\t}\n\t}\n\tmyAfter 1\n", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_declaration_is_found_past_the_first()
    {
        JuiceDocument document = JuiceDocument.Read(
            SourceFile.FromMemory("made-up.juice", Encoding.UTF8.GetBytes(
                "A one < uid=0123456789ABCDEF0123456789ABCDEF >\n{\n\tmyX 1\n}\n" +
                "A two < uid=FEDCBA9876543210FEDCBA9876543210 >\n{\n\tmyX 2\n}\n")),
            "two").Value;

        Assert.Equal("two", document.DeclaredName);
        Assert.True(document.TryGetField("myX", out JuiceField x));
        Assert.Equal("2", Text(document, x.Value));
    }

    [Fact]
    public void A_name_the_file_does_not_declare_is_refused()
    {
        Result<JuiceDocument> result = JuiceDocument.Read(
            SourceFile.FromMemory("made-up.juice", Encoding.UTF8.GetBytes(
                "A one < uid=0123456789ABCDEF0123456789ABCDEF >\n{\n\tmyX 1\n}\n")),
            "missing");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    private static string Text(JuiceDocument document, Perianth.Formats.ByteRange range) =>
        Encoding.UTF8.GetString(document.Bytes.Span.Slice(range.Offset, range.Length));
}

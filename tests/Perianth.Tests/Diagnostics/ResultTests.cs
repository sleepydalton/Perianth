using System;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Diagnostics;

public sealed class ResultTests
{
    [Fact]
    public void A_successful_result_carries_its_value()
    {
        Result<int> result = Result.Ok(7);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsRefused);
        Assert.Equal(7, result.Value);
    }

    [Fact]
    public void A_refusal_carries_its_kind_identifier_and_message()
    {
        Refusal refusal = Refusal.Unsupported("Rotation layout 4 is not one of the supported forms.");

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Equal(DiagnosticIds.FormatUnsupported, refusal.DiagnosticId);
        Assert.Equal("Rotation layout 4 is not one of the supported forms.", refusal.Message);
    }

    [Fact]
    public void A_refusal_may_carry_a_more_specific_identifier_than_its_kind()
    {
        // Adding context must never change an identifier, but a kind and an
        // identifier are independent: several identifiers share one kind.
        Refusal refusal = Refusal.Unsupported("No node is declared for this part.", "hierarchy_node_missing");

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Equal("hierarchy_node_missing", refusal.DiagnosticId);
    }

    [Fact]
    public void A_refusal_converts_to_a_result_of_any_type()
    {
        Result<string> result = Refusal.Malformed("The declared count exceeds the section.");

        Assert.True(result.IsRefused);
        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void Reading_the_value_of_a_refusal_is_a_fault()
    {
        Result<string> result = Refusal.Resource("The bake exceeds the available memory.");

        Assert.Throws<InvalidOperationException>(() => { _ = result.Value; });
    }

    [Fact]
    public void Reading_the_refusal_of_a_success_is_a_fault()
    {
        Result<string> result = Result.Ok("decoded");

        Assert.Throws<InvalidOperationException>(() => { _ = result.Refusal; });
    }

    [Fact]
    public void An_uninitialised_result_is_neither_outcome_and_says_so()
    {
        // Without the explicit success flag this would claim success and hand
        // back a null value, which is the one thing the type exists to prevent.
        Result<string> result = default;

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRefused);
        Assert.Throws<InvalidOperationException>(() => { _ = result.Value; });
        Assert.Throws<InvalidOperationException>(() => { _ = result.Refusal; });
        Assert.Throws<InvalidOperationException>(() => { _ = result.TryGetValue(out _, out _); });
    }

    [Fact]
    public void TryGetValue_hands_back_exactly_one_of_the_two()
    {
        Assert.True(Result.Ok(3).TryGetValue(out int value, out Refusal? absent));
        Assert.Equal(3, value);
        Assert.Null(absent);

        Result<int> refused = Refusal.Malformed("Truncated.");
        Assert.False(refused.TryGetValue(out _, out Refusal? present));
        Assert.NotNull(present);
        Assert.Equal("Truncated.", present.Message);
    }

    [Fact]
    public void A_refusal_with_no_message_is_a_fault()
    {
        // A refusal reaching a user with nothing to read is a defect in this
        // code, not a property of their file.
        Assert.Throws<ArgumentException>(() => Refusal.Malformed("   "));
        Assert.Throws<ArgumentNullException>(() => Refusal.Malformed(null!));
        Assert.Throws<ArgumentException>(() => Refusal.Malformed("Truncated.", ""));
    }

    [Fact]
    public void Two_refusals_describing_the_same_thing_are_equal()
    {
        // Value equality keeps deduplicating and comparing diagnostics free, and
        // determinism is the product.
        Assert.Equal(
            Refusal.Malformed("The declared count exceeds the section."),
            Refusal.Malformed("The declared count exceeds the section."));

        Assert.NotEqual(
            Refusal.Malformed("The declared count exceeds the section."),
            Refusal.Unsupported("The declared count exceeds the section."));
    }
}

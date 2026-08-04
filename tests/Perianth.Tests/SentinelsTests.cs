using Perianth.Formats;
using Xunit;

namespace Perianth.Tests;

public sealed class SentinelsTests
{
    [Fact]
    public void Both_selector_sentinels_sit_inside_the_static_table_range()
    {
        // Specification section 4 reads "selector >= 0x8000, excluding
        // sentinels". The exclusion is only necessary because both sentinels are
        // themselves above the base, and a decoder that tested the range first
        // would read them as static table indices 0x7FFE and 0x7FFF.
        Assert.True(Sentinels.AnimSelectorHiddenOrIdentity >= Sentinels.AnimSelectorStaticBase);
        Assert.True(Sentinels.AnimSelectorActiveOrIdentity >= Sentinels.AnimSelectorStaticBase);
        Assert.NotEqual(Sentinels.AnimSelectorHiddenOrIdentity, Sentinels.AnimSelectorActiveOrIdentity);
    }

    [Fact]
    public void The_compact_follower_table_has_one_entry_per_selector_bit_pattern()
    {
        // The selector is the top two bits of the first byte, so the table is
        // indexed by 0 to 3 and a shorter one would read out of bounds.
        Assert.Equal(4, Sentinels.BvmCompactExtraByteCounts.Length);
        Assert.Equal(new byte[] { 0, 1, 3, 7 }, Sentinels.BvmCompactExtraByteCounts.ToArray());
    }
}

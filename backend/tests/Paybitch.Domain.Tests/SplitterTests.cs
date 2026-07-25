namespace Paybitch.Domain.Tests;

public class SplitterTests
{
    // ---- EQUAL / largest-remainder example cases ----

    [Fact]
    public void Equal_100_EUR_among_3_is_3334_3333_3333()
    {
        var shares = Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Equal(new[] { "a", "b", "c" }));

        Assert.Equal(3334, TestSupport.ShareOf(shares, "a")); // leftover cent to first (ordinal tie-break)
        Assert.Equal(3333, TestSupport.ShareOf(shares, "b"));
        Assert.Equal(3333, TestSupport.ShareOf(shares, "c"));
        Assert.Equal(10000, shares.Sum(s => s.Share.Minor));
    }

    [Fact]
    public void Equal_100_CZK_among_3_is_34_33_33_zero_decimals()
    {
        var shares = Splitter.Resolve(
            new Money(100, Currency.CZK),   // 100 Kč, 0 decimals — same code, scale on Currency
            new SplitType.Equal(new[] { "a", "b", "c" }));

        Assert.Equal(34, TestSupport.ShareOf(shares, "a"));
        Assert.Equal(33, TestSupport.ShareOf(shares, "b"));
        Assert.Equal(33, TestSupport.ShareOf(shares, "c"));
        Assert.Equal(100, shares.Sum(s => s.Share.Minor));
    }

    [Fact]
    public void Equal_one_cent_among_3_gives_one_member_the_cent()
    {
        var shares = Splitter.Resolve(
            new Money(1, Currency.EUR),
            new SplitType.Equal(new[] { "a", "b", "c" }));

        Assert.Equal(1, shares.Sum(s => s.Share.Minor));
        Assert.Equal(1, shares.Count(s => s.Share.Minor == 1));
        Assert.Equal(2, shares.Count(s => s.Share.Minor == 0));
        Assert.Equal(1, TestSupport.ShareOf(shares, "a")); // ordinal tie-break → first
    }

    [Fact]
    public void Equal_share_spread_is_fair_max_minus_min_at_most_one()
    {
        // 10 among 3, "fairness over 1000×" essence: no member off by more than 1 unit.
        var shares = Splitter.Resolve(
            new Money(10, Currency.EUR),
            new SplitType.Equal(TestSupport.Members(3)));

        var minors = shares.Select(s => s.Share.Minor).ToList();
        Assert.Equal(10, minors.Sum());
        Assert.True(minors.Max() - minors.Min() <= 1);
        Assert.Equal(4, minors.Max()); // 4,3,3
    }

    [Fact]
    public void Equal_preserves_input_order()
    {
        var shares = Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Equal(new[] { "z", "y", "x" }));

        Assert.Equal(new[] { "z", "y", "x" }, shares.Select(s => s.MemberId).ToArray());
        // Ordinal tie-break gives the cent to "x" (smallest ordinal), regardless of position.
        Assert.Equal(3334, TestSupport.ShareOf(shares, "x"));
    }

    // ---- SHARES ----

    [Fact]
    public void Shares_distribute_by_weight()
    {
        var shares = Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Shares(new[] { ("a", 2), ("b", 1), ("c", 1) }));

        Assert.Equal(5000, TestSupport.ShareOf(shares, "a"));
        Assert.Equal(2500, TestSupport.ShareOf(shares, "b"));
        Assert.Equal(2500, TestSupport.ShareOf(shares, "c"));
        Assert.Equal(10000, shares.Sum(s => s.Share.Minor));
    }

    [Fact]
    public void Shares_all_zero_weights_throws_not_divide_by_zero()
    {
        var ex = Assert.Throws<InvalidSplitException>(() => Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Shares(new[] { ("a", 0), ("b", 0) })));
        Assert.Contains("weight", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- PERCENTAGE ----

    [Fact]
    public void Percentage_50_30_20_of_100_EUR()
    {
        var shares = Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Percentage(new[] { ("a", 5000), ("b", 3000), ("c", 2000) }));

        Assert.Equal(5000, TestSupport.ShareOf(shares, "a"));
        Assert.Equal(3000, TestSupport.ShareOf(shares, "b"));
        Assert.Equal(2000, TestSupport.ShareOf(shares, "c"));
    }

    [Fact]
    public void Percentage_thirds_of_10_EUR_rounds_by_largest_remainder()
    {
        // 10.00 EUR at 3333 / 3333 / 3334 bp → 333 / 333 / 334 (leftover cent to largest remainder).
        var shares = Splitter.Resolve(
            new Money(1000, Currency.EUR),
            new SplitType.Percentage(new[] { ("a", 3333), ("b", 3333), ("c", 3334) }));

        Assert.Equal(333, TestSupport.ShareOf(shares, "a"));
        Assert.Equal(333, TestSupport.ShareOf(shares, "b"));
        Assert.Equal(334, TestSupport.ShareOf(shares, "c"));
        Assert.Equal(1000, shares.Sum(s => s.Share.Minor));
    }

    [Fact]
    public void Percentage_sum_not_10000_throws()
    {
        var ex = Assert.Throws<InvalidSplitException>(() => Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Percentage(new[] { ("a", 5000), ("b", 3000) })));  // 8000 ≠ 10000
        Assert.Contains("10000", ex.Message);
    }

    // ---- EXACT ----

    [Fact]
    public void Exact_passes_through_when_sum_matches()
    {
        var shares = Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Exact(new[] { ("a", 4000L), ("b", 3500L), ("c", 2500L) }));

        Assert.Equal(4000, TestSupport.ShareOf(shares, "a"));
        Assert.Equal(3500, TestSupport.ShareOf(shares, "b"));
        Assert.Equal(2500, TestSupport.ShareOf(shares, "c"));
    }

    [Fact]
    public void Exact_not_summing_to_amount_throws()
    {
        var ex = Assert.Throws<InvalidSplitException>(() => Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Exact(new[] { ("a", 4000L), ("b", 3500L) })));  // 7500 ≠ 10000
        Assert.Contains("10000", ex.Message);
    }

    [Fact]
    public void Exact_negative_share_throws()
    {
        Assert.Throws<InvalidSplitException>(() => Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Exact(new[] { ("a", 11000L), ("b", -1000L) })));
    }

    [Fact]
    public void Exact_allows_zero_share()
    {
        var shares = Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Exact(new[] { ("a", 10000L), ("b", 0L) }));
        Assert.Equal(0, TestSupport.ShareOf(shares, "b"));
    }

    // ---- shared validation ----

    [Fact]
    public void Duplicate_member_ids_throw()
    {
        Assert.Throws<InvalidSplitException>(() => Splitter.Resolve(
            new Money(9000, Currency.EUR),
            new SplitType.Equal(new[] { "a", "a", "b" })));
    }

    [Fact]
    public void Empty_member_set_throws()
    {
        Assert.Throws<InvalidSplitException>(() => Splitter.Resolve(
            new Money(9000, Currency.EUR),
            new SplitType.Equal(Array.Empty<string>())));
    }

    [Fact]
    public void ProportionalAllocate_zero_total_yields_all_zero_shares()
    {
        var shares = Splitter.ProportionalAllocate(0, Currency.EUR, new[] { "a", "b" }, _ => 1);
        Assert.All(shares, s => Assert.Equal(0, s.Share.Minor));
    }

    [Fact]
    public void ProportionalAllocate_does_not_overflow_on_large_total()
    {
        // total * weight would overflow long (Int128 path guards it).
        long total = 9_000_000_000_000_000L; // 9e15
        var shares = Splitter.ProportionalAllocate(
            total, Currency.EUR, new[] { "a", "b" }, id => id == "a" ? 5000L : 5000L);

        Assert.Equal(total, shares.Sum(s => s.Share.Minor));
    }
}

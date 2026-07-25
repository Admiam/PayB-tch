namespace Paybitch.Domain.Tests;

public class BalanceCalculatorTests
{
    private static long Net(IReadOnlyList<MemberBalance> balances, string id) =>
        balances.First(b => b.MemberId == id).Net.Minor;

    [Fact]
    public void Payer_also_in_split_nets_correctly()
    {
        // Alice pays 90.00 EUR, split equally among alice/bob/carol → 30/30/30.
        var shares = Splitter.Resolve(
            new Money(9000, Currency.EUR),
            new SplitType.Equal(new[] { "alice", "bob", "carol" }));

        var expenses = new[]
        {
            new ExpenseInput("alice", new Money(9000, Currency.EUR), TestSupport.ToShares(shares)),
        };

        var balances = BalanceCalculator.Compute(Currency.EUR, expenses, Array.Empty<SettlementInput>());

        Assert.Equal(6000, Net(balances, "alice"));   // paid 9000 − share 3000
        Assert.Equal(-3000, Net(balances, "bob"));
        Assert.Equal(-3000, Net(balances, "carol"));
        Assert.Equal(0, balances.Sum(b => b.Net.Minor));
    }

    [Fact]
    public void Settlement_exactly_extinguishes_debt()
    {
        // Bob pays 100 EUR split with alice → alice owes 50, bob is owed 50.
        var shares = Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Equal(new[] { "alice", "bob" }));
        var expenses = new[] { new ExpenseInput("bob", new Money(10000, Currency.EUR), TestSupport.ToShares(shares)) };

        // Alice settles exactly 50.00 to bob.
        var settlements = new[] { new SettlementInput("alice", "bob", new Money(5000, Currency.EUR)) };

        var balances = BalanceCalculator.Compute(Currency.EUR, expenses, settlements);

        Assert.Equal(0, Net(balances, "alice"));
        Assert.Equal(0, Net(balances, "bob"));
    }

    [Fact]
    public void Settlement_exceeding_debt_flips_sign_no_clamp()
    {
        var shares = Splitter.Resolve(
            new Money(10000, Currency.EUR),
            new SplitType.Equal(new[] { "alice", "bob" }));
        var expenses = new[] { new ExpenseInput("bob", new Money(10000, Currency.EUR), TestSupport.ToShares(shares)) };

        // Alice owes 50 but pays 80 → she is now owed 30, bob owes 30.
        var settlements = new[] { new SettlementInput("alice", "bob", new Money(8000, Currency.EUR)) };

        var balances = BalanceCalculator.Compute(Currency.EUR, expenses, settlements);

        Assert.Equal(3000, Net(balances, "alice"));   // flipped: now a creditor
        Assert.Equal(-3000, Net(balances, "bob"));
        Assert.Equal(0, balances.Sum(b => b.Net.Minor));
    }

    [Fact]
    public void Member_with_outstanding_balance_is_tracked_even_if_never_a_payer()
    {
        // "Removed/leaving member" essence at the domain level: a member that only ever
        // appears as a debtor still shows up in balances with a non-zero net.
        var shares = Splitter.Resolve(
            new Money(9000, Currency.EUR),
            new SplitType.Equal(new[] { "alice", "ghost" }));
        var expenses = new[] { new ExpenseInput("alice", new Money(9000, Currency.EUR), TestSupport.ToShares(shares)) };

        var balances = BalanceCalculator.Compute(Currency.EUR, expenses, Array.Empty<SettlementInput>());

        Assert.Contains(balances, b => b.MemberId == "ghost");
        Assert.Equal(-4500, Net(balances, "ghost"));
    }

    [Fact]
    public void Mixed_currency_input_to_single_currency_Compute_throws()
    {
        var eurShares = TestSupport.ToShares(Splitter.Resolve(
            new Money(9000, Currency.EUR), new SplitType.Equal(new[] { "a", "b" })));
        var expenses = new[]
        {
            new ExpenseInput("a", new Money(9000, Currency.CZK), eurShares), // amount CZK, asked for EUR
        };

        Assert.Throws<CurrencyMismatchException>(() =>
            BalanceCalculator.Compute(Currency.EUR, expenses, Array.Empty<SettlementInput>()));
    }

    [Fact]
    public void Shares_not_summing_to_amount_throws()
    {
        var badShares = new (string, long)[] { ("a", 4000), ("b", 4000) }; // 8000 ≠ 9000
        var expenses = new[] { new ExpenseInput("a", new Money(9000, Currency.EUR), badShares) };

        Assert.Throws<InvalidSplitException>(() =>
            BalanceCalculator.Compute(Currency.EUR, expenses, Array.Empty<SettlementInput>()));
    }

    [Fact]
    public void ComputeByCurrency_buckets_independently_and_each_bucket_sums_to_zero()
    {
        var eur = TestSupport.ToShares(Splitter.Resolve(
            new Money(9000, Currency.EUR), new SplitType.Equal(new[] { "a", "b" })));
        var czk = TestSupport.ToShares(Splitter.Resolve(
            new Money(300, Currency.CZK), new SplitType.Equal(new[] { "a", "b", "c" })));

        var expenses = new[]
        {
            new ExpenseInput("a", new Money(9000, Currency.EUR), eur),
            new ExpenseInput("c", new Money(300, Currency.CZK), czk),
        };

        var byCurrency = BalanceCalculator.ComputeByCurrency(expenses, Array.Empty<SettlementInput>());

        Assert.Equal(2, byCurrency.Count);
        Assert.Equal(0, byCurrency[Currency.EUR].Sum(b => b.Net.Minor));
        Assert.Equal(0, byCurrency[Currency.CZK].Sum(b => b.Net.Minor));
    }

    [Fact]
    public void Empty_input_yields_empty_balances()
    {
        var balances = BalanceCalculator.Compute(
            Currency.EUR, Array.Empty<ExpenseInput>(), Array.Empty<SettlementInput>());
        Assert.Empty(balances);
    }
}

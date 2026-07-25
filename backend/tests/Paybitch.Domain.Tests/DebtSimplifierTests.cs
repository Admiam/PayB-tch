namespace Paybitch.Domain.Tests;

public class DebtSimplifierTests
{
    private static MemberBalance Bal(string id, long net, Currency? c = null) =>
        new(id, new Money(net, c ?? Currency.CZK));

    [Fact]
    public void Simplifies_one_creditor_two_debtors()
    {
        // Matches §3.3: alice +96050, carol −53000, dave −43050 (CZK).
        var balances = new[]
        {
            Bal("alice", 96050),
            Bal("carol", -53000),
            Bal("dave", -43050),
        };

        var edges = DebtSimplifier.Simplify(balances);

        // ≤ N−1 transfers (N = 3 non-zero members ⇒ ≤ 2).
        Assert.True(edges.Count <= 2);
        // Everyone pays alice; totals reconcile.
        Assert.All(edges, e => Assert.Equal("alice", e.ToMemberId));
        Assert.Equal(96050, edges.Sum(e => e.Amount.Minor));
        Assert.Contains(edges, e => e.FromMemberId == "carol" && e.Amount.Minor == 53000);
        Assert.Contains(edges, e => e.FromMemberId == "dave" && e.Amount.Minor == 43050);
    }

    [Fact]
    public void Sum_of_edges_equals_total_credit()
    {
        var balances = new[]
        {
            Bal("a", 5000),
            Bal("b", 3000),
            Bal("c", -6000),
            Bal("d", -2000),
        };

        var edges = DebtSimplifier.Simplify(balances);

        long totalCredit = balances.Where(b => b.Net.Minor > 0).Sum(b => b.Net.Minor);
        Assert.Equal(totalCredit, edges.Sum(e => e.Amount.Minor));
        Assert.All(edges, e => Assert.True(e.Amount.Minor > 0));
    }

    [Fact]
    public void All_settled_produces_no_edges()
    {
        var balances = new[] { Bal("a", 0), Bal("b", 0), Bal("c", 0) };
        Assert.Empty(DebtSimplifier.Simplify(balances));
    }

    [Fact]
    public void Empty_produces_no_edges()
    {
        Assert.Empty(DebtSimplifier.Simplify(Array.Empty<MemberBalance>()));
    }

    [Fact]
    public void Simple_two_party_debt()
    {
        var edges = DebtSimplifier.Simplify(new[] { Bal("a", -2500), Bal("b", 2500) });

        var e = Assert.Single(edges);
        Assert.Equal("a", e.FromMemberId);
        Assert.Equal("b", e.ToMemberId);
        Assert.Equal(2500, e.Amount.Minor);
    }

    [Fact]
    public void Edges_never_exceed_N_minus_1()
    {
        var balances = new[]
        {
            Bal("a", 10000),
            Bal("b", -2500),
            Bal("c", -2500),
            Bal("d", -2500),
            Bal("e", -2500),
        };

        var edges = DebtSimplifier.Simplify(balances);
        Assert.True(edges.Count <= balances.Length - 1);
    }

    [Fact]
    public void Mismatched_currency_in_single_bucket_throws()
    {
        var balances = new[] { Bal("a", 100, Currency.EUR), Bal("b", -100, Currency.CZK) };
        Assert.Throws<CurrencyMismatchException>(() => DebtSimplifier.Simplify(balances));
    }

    [Fact]
    public void SimplifyByCurrency_buckets_and_never_nets_across_currencies()
    {
        var balances = new[]
        {
            Bal("a", 5000, Currency.EUR),
            Bal("b", -5000, Currency.EUR),
            Bal("a", -300, Currency.CZK),
            Bal("c", 300, Currency.CZK),
        };

        var byCurrency = DebtSimplifier.SimplifyByCurrency(balances);

        Assert.Equal(2, byCurrency.Count);
        var eur = Assert.Single(byCurrency[Currency.EUR]);
        Assert.Equal(Currency.EUR, eur.Amount.Currency);
        Assert.Equal(5000, eur.Amount.Minor);
        var czk = Assert.Single(byCurrency[Currency.CZK]);
        Assert.Equal(Currency.CZK, czk.Amount.Currency);
        Assert.Equal(300, czk.Amount.Minor);
    }

    [Fact]
    public void Same_input_is_deterministic()
    {
        var balances = new[]
        {
            Bal("a", 7000), Bal("b", 3000), Bal("c", -4000), Bal("d", -6000),
        };

        var first = DebtSimplifier.Simplify(balances);
        var second = DebtSimplifier.Simplify(balances);
        Assert.True(first.SequenceEqual(second));
    }
}

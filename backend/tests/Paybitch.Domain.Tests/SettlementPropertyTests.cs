using FsCheck.Xunit;

namespace Paybitch.Domain.Tests;

/// <summary>
/// Property-based invariants for balances and debt simplification (§2.6):
/// Σ net == 0 per currency; Σ edges == total credit; edges ≤ N−1; determinism.
/// </summary>
public class SettlementPropertyTests
{
    private static readonly Currency C = Currency.EUR;

    // ---- BalanceCalculator: Σ net == 0 per currency ----

    [Property]
    public bool Net_sums_to_zero_with_expenses_and_settlements(
        long totalRaw, int[] weightsRaw, int payerRaw, long settleRaw, int fromRaw, int toRaw)
    {
        if (weightsRaw is null || weightsRaw.Length == 0) return true;
        int n = Math.Min(weightsRaw.Length, 12);
        var members = TestSupport.Members(n);
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
            map[members[i]] = Math.Abs(weightsRaw[i] % 1000) + 1;

        long total = Math.Abs(totalRaw % 100_000_000);
        var amount = new Money(total, C);
        var shares = Splitter.ProportionalAllocate(total, C, members, id => map[id]);
        var expenses = new[]
        {
            new ExpenseInput(members[(payerRaw & int.MaxValue) % n], amount, TestSupport.ToShares(shares)),
        };

        var settlements = Array.Empty<SettlementInput>();
        if (n >= 2)
        {
            int from = (fromRaw & int.MaxValue) % n;
            int to = (toRaw & int.MaxValue) % n;
            if (from != to)
            {
                long amt = Math.Abs(settleRaw % 10_000_000);
                settlements = new[] { new SettlementInput(members[from], members[to], new Money(amt, C)) };
            }
        }

        var balances = BalanceCalculator.Compute(C, expenses, settlements);
        return balances.Sum(b => b.Net.Minor) == 0;
    }

    // ---- DebtSimplifier ----

    /// <summary>Build a deterministic zero-sum balance vector from arbitrary longs.</summary>
    private static List<MemberBalance> ZeroSumBalances(long[] raw, out int nonZero)
    {
        int n = Math.Min(raw.Length, 20);
        var vals = new long[n];
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            vals[i] = raw[i] % 1_000_000; // -1e6 .. 1e6
            sum += vals[i];
        }
        vals[0] -= sum; // force Σ == 0

        var members = TestSupport.Members(n);
        var list = new List<MemberBalance>(n);
        nonZero = 0;
        for (int i = 0; i < n; i++)
        {
            if (vals[i] != 0) nonZero++;
            list.Add(new MemberBalance(members[i], new Money(vals[i], C)));
        }
        return list;
    }

    [Property]
    public bool Edges_reconcile_credit_are_positive_and_bounded(long[] raw)
    {
        if (raw is null || raw.Length == 0) return true;
        var balances = ZeroSumBalances(raw, out int nonZero);

        var edges = DebtSimplifier.Simplify(balances);

        long totalCredit = balances.Where(b => b.Net.Minor > 0).Sum(b => b.Net.Minor);
        bool reconciles = edges.Sum(e => e.Amount.Minor) == totalCredit;
        bool allPositive = edges.All(e => e.Amount.Minor > 0);
        bool bounded = edges.Count <= Math.Max(0, nonZero - 1);
        return reconciles && allPositive && bounded;
    }

    [Property]
    public bool Simplify_is_deterministic(long[] raw)
    {
        if (raw is null || raw.Length == 0) return true;
        var balances = ZeroSumBalances(raw, out _);

        var a = DebtSimplifier.Simplify(balances);
        var b = DebtSimplifier.Simplify(balances);
        return a.SequenceEqual(b);
    }

    [Property]
    public bool Post_simplification_positions_are_settled(long[] raw)
    {
        // Applying every edge as a settlement drives all nets to zero.
        if (raw is null || raw.Length == 0) return true;
        var balances = ZeroSumBalances(raw, out _);
        var net = balances.ToDictionary(b => b.MemberId, b => b.Net.Minor, StringComparer.Ordinal);

        foreach (var e in DebtSimplifier.Simplify(balances))
        {
            net[e.FromMemberId] += e.Amount.Minor;  // debtor pays → net rises toward 0
            net[e.ToMemberId] -= e.Amount.Minor;     // creditor is paid → net falls toward 0
        }
        return net.Values.All(v => v == 0);
    }
}

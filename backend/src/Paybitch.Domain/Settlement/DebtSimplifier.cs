namespace Paybitch.Domain.Settlement;

/// <summary>
/// Greedy min-cash-flow debt simplification (decision §2.4): repeatedly match the largest
/// creditor with the largest debtor and transfer the smaller magnitude. Each step zeroes
/// at least one party ⇒ ≤ N−1 transfers. Integer math, exact. Single currency by contract
/// (D5) — use <see cref="SimplifyByCurrency"/> for mixed sets.
/// </summary>
public static class DebtSimplifier
{
    public static IReadOnlyList<DebtEdge> Simplify(IReadOnlyCollection<MemberBalance> balances)
    {
        if (balances.Count == 0) return Array.Empty<DebtEdge>();

        var currency = balances.First().Net.Currency;
        foreach (var b in balances)
            if (b.Net.Currency != currency)
                throw new CurrencyMismatchException(currency, b.Net.Currency);

        var creditors = new PriorityQueue<string, long>();   // net > 0, keyed by -net (max first)
        var debtors = new PriorityQueue<string, long>();     // net < 0, keyed by net (most-negative first)
        var owed = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var b in balances)
        {
            long n = b.Net.Minor;
            if (n > 0) { creditors.Enqueue(b.MemberId, -n); owed[b.MemberId] = n; }
            else if (n < 0) { debtors.Enqueue(b.MemberId, n); owed[b.MemberId] = n; }
        }

        var edges = new List<DebtEdge>();
        while (creditors.Count > 0 && debtors.Count > 0)
        {
            var cr = creditors.Dequeue();
            var dr = debtors.Dequeue();
            long pay = Math.Min(owed[cr], -owed[dr]);
            edges.Add(new DebtEdge(dr, cr, new Money(pay, currency)));
            if ((owed[cr] -= pay) > 0) creditors.Enqueue(cr, -owed[cr]);
            if ((owed[dr] += pay) < 0) debtors.Enqueue(dr, owed[dr]);
        }
        return edges;
    }

    /// <summary>
    /// Bucket balances by currency and simplify each independently (never nets EUR against
    /// CZK — decision D5).
    /// </summary>
    public static IReadOnlyDictionary<Currency, IReadOnlyList<DebtEdge>> SimplifyByCurrency(
        IReadOnlyCollection<MemberBalance> balances)
    {
        var result = new Dictionary<Currency, IReadOnlyList<DebtEdge>>();
        foreach (var group in balances.GroupBy(b => b.Net.Currency))
            result[group.Key] = Simplify(group.ToList());
        return result;
    }
}

namespace Paybitch.Domain.Settlement;

/// <summary>
/// Immutable input for one expense: who paid, the full amount, and the resolved
/// per-member shares (which must sum to the amount — §1.5 / §2.2).
/// </summary>
public sealed record ExpenseInput(
    string PaidBy,
    Money Amount,
    IReadOnlyList<(string MemberId, long ShareMinor)> Shares);

/// <summary>
/// Immutable input for one settlement: <see cref="From"/> pays <see cref="To"/> the
/// <see cref="Amount"/>. Same currency as the debt (decision D5).
/// </summary>
public sealed record SettlementInput(string From, string To, Money Amount);

/// <summary>
/// On-demand net-balance computation (decision D4, §2.2). No materialized balance table.
///
///   net(m, C) = Σ paid(m,e) − Σ share(m,e) + Σ settlementEffect(m,s)
///
/// A settlement from→to A adds +A to <c>from</c> and −A to <c>to</c>. Σ net == 0 per
/// currency, always — the first invariant the property tests assert.
/// </summary>
public static class BalanceCalculator
{
    /// <summary>
    /// Compute per-member nets for a single currency. All inputs must be in
    /// <paramref name="currency"/>; a foreign amount throws
    /// <see cref="CurrencyMismatchException"/>. Results are ordered by member id.
    /// </summary>
    public static IReadOnlyList<MemberBalance> Compute(
        Currency currency,
        IReadOnlyList<ExpenseInput> expenses,
        IReadOnlyList<SettlementInput> settlements)
    {
        var net = new Dictionary<string, long>(StringComparer.Ordinal);

        void Bump(string id, long delta) => net[id] = net.GetValueOrDefault(id) + delta;

        foreach (var e in expenses)
        {
            if (e.Amount.Currency != currency)
                throw new CurrencyMismatchException(currency, e.Amount.Currency);

            Bump(e.PaidBy, e.Amount.Minor);

            long shareSum = 0;
            foreach (var (memberId, shareMinor) in e.Shares)
            {
                Bump(memberId, -shareMinor);
                shareSum = checked(shareSum + shareMinor);
            }

            if (shareSum != e.Amount.Minor)
                throw new InvalidSplitException(
                    $"Expense shares must sum to amount {e.Amount.Minor} (got {shareSum}).");
        }

        foreach (var s in settlements)
        {
            if (s.Amount.Currency != currency)
                throw new CurrencyMismatchException(currency, s.Amount.Currency);

            Bump(s.From, s.Amount.Minor);
            Bump(s.To, -s.Amount.Minor);
        }

        return net
            .Select(kv => new MemberBalance(kv.Key, new Money(kv.Value, currency)))
            .OrderBy(b => b.MemberId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Bucket a mixed-currency set of inputs by currency and compute each bucket
    /// independently (never nets one currency against another — decision D5).
    /// </summary>
    public static IReadOnlyDictionary<Currency, IReadOnlyList<MemberBalance>> ComputeByCurrency(
        IReadOnlyList<ExpenseInput> expenses,
        IReadOnlyList<SettlementInput> settlements)
    {
        var currencies = new HashSet<Currency>();
        foreach (var e in expenses) currencies.Add(e.Amount.Currency);
        foreach (var s in settlements) currencies.Add(s.Amount.Currency);

        var result = new Dictionary<Currency, IReadOnlyList<MemberBalance>>();
        foreach (var c in currencies)
        {
            var bucketExpenses = expenses.Where(e => e.Amount.Currency == c).ToList();
            var bucketSettlements = settlements.Where(s => s.Amount.Currency == c).ToList();
            result[c] = Compute(c, bucketExpenses, bucketSettlements);
        }
        return result;
    }
}

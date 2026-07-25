namespace Paybitch.Domain.Splitting;

/// <summary>
/// Largest-remainder (Hamilton) split allocation (decision D3, §2.3). EQUAL / SHARES /
/// PERCENTAGE are one problem: distribute <c>total</c> minor units by integer weights,
/// then hand the leftover units one-each to the largest remainders with a deterministic
/// tie-break. Shares always sum to the total exactly — the rounding error is spread
/// fairly, never dumped on the last member.
/// </summary>
public static class Splitter
{
    /// <summary>
    /// Distribute <paramref name="total"/> minor units of <paramref name="c"/> across
    /// <paramref name="members"/> proportionally to <paramref name="weightOf"/>.
    /// Returns one share per member, in input order, summing to <paramref name="total"/>.
    /// </summary>
    public static IReadOnlyList<(string MemberId, Money Share)> ProportionalAllocate(
        long total, Currency c, IReadOnlyList<string> members, Func<string, long> weightOf)
    {
        if (members.Count == 0)
            throw new InvalidSplitException("Split must have at least one member.");

        long W = 0;
        foreach (var m in members)
        {
            long w = weightOf(m);
            if (w < 0) throw new InvalidSplitException("Weights must be non-negative.");
            W = checked(W + w);
        }
        if (W <= 0) throw new InvalidSplitException("Total weight must be positive.");

        var rows = new List<(string Id, long Floor, Int128 Rem, long W)>(members.Count);
        long allocated = 0;
        foreach (var m in members)
        {
            long w = weightOf(m);
            Int128 num = (Int128)total * w;   // Int128 avoids long overflow on total*w
            long floor = (long)(num / W);
            Int128 rem = num % W;
            rows.Add((m, floor, rem, w));
            allocated = checked(allocated + floor);
        }
        long leftover = total - allocated;    // 0 <= leftover < members.Count

        var order = rows.OrderByDescending(r => r.Rem)
                        .ThenByDescending(r => r.W)
                        .ThenBy(r => r.Id, StringComparer.Ordinal)   // simple, unbiased tie-break
                        .ToList();
        var bonus = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int k = 0; k < leftover; k++)
            bonus[order[k].Id] = bonus.GetValueOrDefault(order[k].Id) + 1;

        return rows
            .Select(r => (r.Id, new Money(r.Floor + bonus.GetValueOrDefault(r.Id), c)))
            .ToList();
    }

    /// <summary>
    /// Resolve a <see cref="SplitType"/> against an expense <paramref name="amount"/> into
    /// per-member shares that always sum to <c>amount.Minor</c> exactly.
    ///   EQUAL      → equal weights.
    ///   SHARES     → weightOf = weight.
    ///   PERCENTAGE → weightOf = basisPoints (validates Σ == 10000).
    ///   EXACT      → passthrough after validating Σ == amount and each share ≥ 0.
    /// </summary>
    public static IReadOnlyList<(string MemberId, Money Share)> Resolve(Money amount, SplitType split)
    {
        var c = amount.Currency;
        long total = amount.Minor;

        switch (split)
        {
            case SplitType.Equal equal:
            {
                RequireNonEmpty(equal.Among, "EQUAL");
                RequireDistinct(equal.Among);
                return ProportionalAllocate(total, c, equal.Among, static _ => 1);
            }
            case SplitType.Shares shares:
            {
                var ids = shares.Weights.Select(w => w.MemberId).ToList();
                RequireNonEmpty(ids, "SHARES");
                RequireDistinct(ids);
                var map = shares.Weights.ToDictionary(w => w.MemberId, w => (long)w.Weight, StringComparer.Ordinal);
                return ProportionalAllocate(total, c, ids, id => map[id]);
            }
            case SplitType.Percentage pct:
            {
                var ids = pct.Percents.Select(p => p.MemberId).ToList();
                RequireNonEmpty(ids, "PERCENTAGE");
                RequireDistinct(ids);
                long sumBp = 0;
                foreach (var p in pct.Percents) sumBp = checked(sumBp + p.BasisPoints);
                if (sumBp != 10_000)
                    throw new InvalidSplitException(
                        $"Percentage basis points must sum to 10000 exactly (got {sumBp}).");
                var map = pct.Percents.ToDictionary(p => p.MemberId, p => (long)p.BasisPoints, StringComparer.Ordinal);
                return ProportionalAllocate(total, c, ids, id => map[id]);
            }
            case SplitType.Exact exact:
            {
                var ids = exact.Amounts.Select(a => a.MemberId).ToList();
                RequireNonEmpty(ids, "EXACT");
                RequireDistinct(ids);
                long sum = 0;
                foreach (var (memberId, shareMinor) in exact.Amounts)
                {
                    if (shareMinor < 0)
                        throw new InvalidSplitException($"EXACT share for '{memberId}' must be non-negative.");
                    sum = checked(sum + shareMinor);
                }
                if (sum != total)
                    throw new InvalidSplitException($"EXACT shares must sum to {total} (got {sum}).");
                return exact.Amounts
                    .Select(a => (a.MemberId, new Money(a.ShareMinor, c)))
                    .ToList();
            }
            default:
                throw new InvalidSplitException($"Unknown split type: {split.GetType().Name}.");
        }
    }

    private static void RequireNonEmpty(IReadOnlyCollection<string> members, string kind)
    {
        if (members.Count == 0)
            throw new InvalidSplitException($"{kind} split needs at least one member.");
    }

    private static void RequireDistinct(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (!seen.Add(id))
                throw new InvalidSplitException($"Duplicate member id in split: '{id}'.");
    }
}

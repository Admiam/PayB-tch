using FsCheck.Xunit;

namespace Paybitch.Domain.Tests;

/// <summary>
/// Property-based invariants for splitting (§2.6). Inputs are plain primitives that are
/// sanitized inside each test into the valid domain range, keeping the tests robust and
/// free of custom generators.
/// </summary>
public class SplitterPropertyTests
{
    private static long BoundedTotal(long raw) => Math.Abs(raw % 100_000_000); // 0 .. ~1e8
    private static int Count(int raw, int max) => (raw & int.MaxValue) % max + 1;

    [Property]
    public bool Equal_split_always_sums_to_total(long totalRaw, int memberCountRaw)
    {
        long total = BoundedTotal(totalRaw);
        int n = Count(memberCountRaw, 16);
        var members = TestSupport.Members(n);

        var shares = Splitter.ProportionalAllocate(total, Currency.EUR, members, static _ => 1);
        return shares.Sum(s => s.Share.Minor) == total;
    }

    [Property]
    public bool Weighted_split_always_sums_to_total(long totalRaw, int[] weightsRaw)
    {
        if (weightsRaw is null || weightsRaw.Length == 0) return true;
        int n = Math.Min(weightsRaw.Length, 12);
        var members = TestSupport.Members(n);
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
            map[members[i]] = Math.Abs(weightsRaw[i] % 1000) + 1; // 1..1000

        long total = BoundedTotal(totalRaw);
        var shares = Splitter.ProportionalAllocate(total, Currency.EUR, members, id => map[id]);
        return shares.Sum(s => s.Share.Minor) == total;
    }

    [Property]
    public bool Equal_split_is_fair_within_one_unit(long totalRaw, int memberCountRaw)
    {
        long total = BoundedTotal(totalRaw);
        int n = Count(memberCountRaw, 16);
        var members = TestSupport.Members(n);

        var minors = Splitter.ProportionalAllocate(total, Currency.EUR, members, static _ => 1)
            .Select(s => s.Share.Minor)
            .ToList();

        return minors.Sum() == total && (minors.Max() - minors.Min()) <= 1;
    }

    [Property]
    public bool Split_is_deterministic(long totalRaw, int[] weightsRaw)
    {
        if (weightsRaw is null || weightsRaw.Length == 0) return true;
        int n = Math.Min(weightsRaw.Length, 12);
        var members = TestSupport.Members(n);
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
            map[members[i]] = Math.Abs(weightsRaw[i] % 1000) + 1;

        long total = BoundedTotal(totalRaw);
        var a = Splitter.ProportionalAllocate(total, Currency.EUR, members, id => map[id]);
        var b = Splitter.ProportionalAllocate(total, Currency.EUR, members, id => map[id]);
        return a.SequenceEqual(b);
    }

    [Property]
    public bool No_share_is_negative(long totalRaw, int[] weightsRaw)
    {
        if (weightsRaw is null || weightsRaw.Length == 0) return true;
        int n = Math.Min(weightsRaw.Length, 12);
        var members = TestSupport.Members(n);
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
            map[members[i]] = Math.Abs(weightsRaw[i] % 1000) + 1;

        long total = BoundedTotal(totalRaw);
        var shares = Splitter.ProportionalAllocate(total, Currency.EUR, members, id => map[id]);
        return shares.All(s => s.Share.Minor >= 0);
    }
}

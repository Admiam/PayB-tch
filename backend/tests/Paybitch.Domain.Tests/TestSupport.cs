global using Xunit;
global using Paybitch.Domain;
global using Paybitch.Domain.Splitting;
global using Paybitch.Domain.Settlement;

namespace Paybitch.Domain.Tests;

/// <summary>Shared helpers for building deterministic member ids and reading shares.</summary>
internal static class TestSupport
{
    /// <summary>Zero-padded member ids so ordinal and numeric ordering agree ("m0000", "m0001", …).</summary>
    public static List<string> Members(int n) =>
        Enumerable.Range(0, n).Select(i => $"m{i:D4}").ToList();

    /// <summary>Project resolved shares into (id, minor) tuples for ExpenseInput.</summary>
    public static IReadOnlyList<(string, long)> ToShares(
        IReadOnlyList<(string MemberId, Money Share)> resolved) =>
        resolved.Select(r => (r.MemberId, r.Share.Minor)).ToList();

    public static long ShareOf(IReadOnlyList<(string MemberId, Money Share)> resolved, string id) =>
        resolved.First(r => r.MemberId == id).Share.Minor;
}

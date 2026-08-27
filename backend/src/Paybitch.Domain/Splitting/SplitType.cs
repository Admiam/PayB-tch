namespace Paybitch.Domain.Splitting;

/// <summary>
/// The DOMAIN model of how an expense is split (decision D3) — a closed, tagged union.
/// This is NOT the wire format; the application edge parses request JSON into these.
///
/// The private base constructor seals the hierarchy: only the four nested records can
/// derive from it.
/// </summary>
public abstract record SplitType
{
    private SplitType() { }

    /// <summary>Split equally among the listed members.</summary>
    public sealed record Equal(IReadOnlyList<string> Among) : SplitType;

    /// <summary>Explicit per-member minor-unit amounts (must sum to the expense total).</summary>
    public sealed record Exact(IReadOnlyList<(string MemberId, long ShareMinor)> Amounts) : SplitType;

    /// <summary>Integer weights per member; resolved by largest-remainder allocation.</summary>
    public sealed record Shares(IReadOnlyList<(string MemberId, int Weight)> Weights) : SplitType;

    /// <summary>Percentages as integer basis points per member (must sum to 10000).</summary>
    public sealed record Percentage(IReadOnlyList<(string MemberId, int BasisPoints)> Percents) : SplitType;
}

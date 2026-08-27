using Paybitch.Domain;
using Paybitch.Domain.Settlement;

namespace Paybitch.Api.Features.Balances;

/// <summary>
/// On-demand balance computation over a group's live ledger (D4 — no materialized balances). The
/// single owner of the "load expenses + settlements → Domain <see cref="BalanceCalculator"/> /
/// <see cref="DebtSimplifier"/>" pipeline, exposed as an injectable service so sibling slices reuse
/// the exact same math:
/// <list type="bullet">
///   <item>the Groups slice's member-removal zero-balance precondition (§3.8.3) — a member is
///     removable iff <see cref="MemberBalancesAsync"/> returns empty;</item>
///   <item>the <c>GET /groups</c> <c>callerBalance</c> projection (§3.1) — the caller's own
///     per-currency nets.</item>
/// </list>
/// </summary>
public interface IBalanceService
{
    /// <summary>
    /// Full §3.3 balance sheet for a group: per-currency member nets (non-zero only) plus the
    /// simplified min-cash-flow debt edges. Currency buckets whose members all net exactly zero are
    /// omitted; within a returned bucket <c>Σ net == 0</c> still holds (the client-checkable invariant).
    /// </summary>
    Task<GroupBalanceResult> ComputeGroupBalancesAsync(Guid groupId, CancellationToken ct = default);

    /// <summary>
    /// One member's non-zero net per currency. Empty ⇒ the member is fully settled in every currency
    /// (the removable condition, §3.8.3). Used both for the removal precondition and the caller-balance
    /// projection.
    /// </summary>
    Task<IReadOnlyList<MemberCurrencyBalance>> MemberBalancesAsync(
        Guid groupId,
        Guid memberId,
        CancellationToken ct = default);
}

/// <summary>One currency bucket of a group's balance sheet (§3.3).</summary>
public sealed record CurrencyBalances(
    Currency Currency,
    IReadOnlyList<MemberBalance> Balances,
    IReadOnlyList<DebtEdge> Simplified);

/// <summary>A group's full balance sheet across every currency it holds a non-zero position in.</summary>
public sealed record GroupBalanceResult(IReadOnlyList<CurrencyBalances> ByCurrency);

/// <summary>A single member's net position in one currency (only materialized when non-zero).</summary>
public sealed record MemberCurrencyBalance(Currency Currency, Money Net);

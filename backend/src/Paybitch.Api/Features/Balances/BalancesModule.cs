using System.Globalization;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Balances;

/// <summary>
/// <c>GET /v1/groups/{groupId}/balances</c> (§3.3) — the on-demand, per-currency balance sheet with
/// simplified debts (D4). Member-only; reads keep working on archived groups (§3.7). The compute is
/// delegated to <see cref="BalanceService"/> (also the injectable seam sibling slices reuse).
/// </summary>
public sealed class BalancesModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/groups/{groupId:guid}/balances",
                async (Guid groupId, AppDbContext db, CancellationToken ct) =>
                {
                    var result = await new BalanceService(db).ComputeGroupBalancesAsync(groupId, ct);
                    return Results.Ok(ToResponse(groupId, result));
                })
            .RequireGroupMembership()
            .WithName("GetGroupBalances")
            .WithSummary("Per-member nets and simplified debts, per currency (Σ net == 0 per currency).")
            .WithTags("Balances");
    }

    private static BalancesResponse ToResponse(Guid groupId, GroupBalanceResult result) =>
        new(
            groupId.ToString(),
            result.ByCurrency
                .Select(c => new CurrencyBucketResponse(
                    c.Currency.Code,
                    c.Balances
                        .Select(b => new MemberNetResponse(b.MemberId, Minor(b.Net.Minor)))
                        .ToArray(),
                    c.Simplified
                        .Select(e => new SimplifiedEdgeResponse(
                            e.FromMemberId,
                            e.ToMemberId,
                            Minor(e.Amount.Minor)))
                        .ToArray()))
                .ToArray());

    // D1 wire money: minor units as a JSON string, never a number.
    private static string Minor(long minor) => minor.ToString(CultureInfo.InvariantCulture);
}

/// <summary>§3.3 balances envelope. Money fields are minor-unit strings (D1).</summary>
public sealed record BalancesResponse(string GroupId, IReadOnlyList<CurrencyBucketResponse> ByCurrency);

/// <summary>One currency bucket: raw member nets plus the simplified transfer set.</summary>
public sealed record CurrencyBucketResponse(
    string Currency,
    IReadOnlyList<MemberNetResponse> Balances,
    IReadOnlyList<SimplifiedEdgeResponse> Simplified);

/// <summary>A member's signed net in the bucket's currency (positive ⇒ owed to the member).</summary>
public sealed record MemberNetResponse(string MemberId, string Net);

/// <summary>A simplified transfer: <see cref="From"/> pays <see cref="To"/> <see cref="Amount"/>.</summary>
public sealed record SimplifiedEdgeResponse(string From, string To, string Amount);

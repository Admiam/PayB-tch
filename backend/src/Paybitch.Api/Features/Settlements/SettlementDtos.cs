namespace Paybitch.Api.Features.Settlements;

/// <summary>
/// <c>POST /v1/groups/{g}/settlements</c> body (§3.3, §3.6). <see cref="Amount"/> is a minor-unit
/// integer STRING (D1 wire money); <see cref="Currency"/> is the debt's currency (D5). Validated by
/// <see cref="CreateSettlementValidator"/>; <c>(groupId, clientId)</c> is the permanent idempotency
/// key (D9).
/// </summary>
public sealed record CreateSettlementRequest(
    string? ClientId,
    Guid FromMember,
    Guid ToMember,
    string Amount,
    string Currency,
    string? Method,
    DateOnly SettledOn,
    string? Notes);

/// <summary>
/// Settlement wire representation — the shape returned by create (201), the idempotent replay (200),
/// each list row, and the <c>current</c> body of a 412 void conflict. <see cref="Amount"/> is a
/// minor-unit string (D1). <see cref="Deleted"/> is <c>true</c> once voided (§3.6); list rows are
/// always live so it is <c>false</c> there.
/// </summary>
public sealed record SettlementResponse(
    string Id,
    string GroupId,
    string? ClientId,
    string FromMember,
    string ToMember,
    string Amount,
    string Currency,
    string? Method,
    DateOnly SettledOn,
    string? Notes,
    int Version,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    bool Deleted);

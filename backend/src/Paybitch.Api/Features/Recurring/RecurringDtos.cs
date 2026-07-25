using Paybitch.Api.Features.Expenses;

namespace Paybitch.Api.Features.Recurring;

/// <summary>
/// The mutable business body shared by <see cref="CreateRecurringRuleRequest"/> and
/// <see cref="ReplaceRecurringRuleRequest"/> (§3.4). Every scalar binds as a loose string / nullable so the
/// FluentValidation twins own the 422 codes; the cross-field recurrence / split semantics are checked in the
/// handler (they need their own <c>invalid_recurrence</c> / <c>recurring_split_required</c> codes).
/// The <see cref="Split"/> reuses the Expenses tagged-union DTO verbatim (D3 — one split path): the ONLY
/// memberless split is <c>{"type":"equal"}</c> = dynamic-equal (EXT-D3d).
/// </summary>
public interface IRecurringRuleBody
{
    string? Title { get; }
    string? Amount { get; }        // string minor units (D1)
    string? Currency { get; }
    string? PaidBy { get; }        // group_members.id (Guid string)
    string? CategoryId { get; }
    string? IconSymbol { get; }
    string? Notes { get; }
    RecurrenceDto? Recurrence { get; }
    SplitDto? Split { get; }
}

/// <summary>POST body (§3.4). <c>clientId</c> drives (groupId, clientId) create-idempotency (D9, X1).</summary>
public sealed record CreateRecurringRuleRequest : IRecurringRuleBody
{
    public string? ClientId { get; init; }
    public string? Title { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    public string? PaidBy { get; init; }
    public string? CategoryId { get; init; }
    public string? IconSymbol { get; init; }
    public string? Notes { get; init; }
    public RecurrenceDto? Recurrence { get; init; }
    public SplitDto? Split { get; init; }
}

/// <summary>
/// PUT body (§3.4) — a full-aggregate replace (future-only, EXT-D3h). Server-owned echo fields
/// (<c>id</c>, <c>groupId</c>, <c>clientId</c>, <c>status</c>, <c>createdBy</c>, <c>createdAt</c>) may be
/// omitted or echoed unchanged; a body that disagrees → 422 <c>immutable_field</c> (§3.7).
/// </summary>
public sealed record ReplaceRecurringRuleRequest : IRecurringRuleBody
{
    // Immutable echo fields (server-owned).
    public string? Id { get; init; }
    public string? GroupId { get; init; }
    public string? ClientId { get; init; }
    public string? Status { get; init; }
    public string? CreatedBy { get; init; }
    public string? CreatedAt { get; init; }

    // Mutable business body.
    public string? Title { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    public string? PaidBy { get; init; }
    public string? CategoryId { get; init; }
    public string? IconSymbol { get; init; }
    public string? Notes { get; init; }
    public RecurrenceDto? Recurrence { get; init; }
    public SplitDto? Split { get; init; }
}

/// <summary>
/// The curated recurrence payload (EXT-D3a): typed columns, never an RRULE string. <c>interval</c> defaults
/// to 1 when omitted. End bound is <c>endsOn</c> XOR <c>remainingCount</c> XOR neither (open-ended).
/// </summary>
public sealed record RecurrenceDto(
    string? Freq,
    int? Interval,
    int? ByMonthDay,   // monthly only, 1..31
    int? ByWeekday,    // weekly only, ISO 1=Mon..7=Sun
    string? Timezone,  // IANA tzdb id (default = creator's, applied by the client)
    string? StartsOn,  // ISO date
    string? EndsOn,    // inclusive ISO date; null = open / count-bounded
    int? RemainingCount);

using System.Text.Json.Serialization;
using Paybitch.Api.Features.Expenses;

namespace Paybitch.Api.Features.Recurring;

/// <summary>
/// The canonical rule representation returned by every recurring route and embedded in <c>/sync</c> as the
/// <c>recurring_rule</c> entity (§3.4, §3.5). Money is a string of minor units + <c>currency</c> (D1, EXT-D3k);
/// there is never a <c>display</c>/<c>approx</c> block (X3).
/// </summary>
public sealed record RecurringRuleResponse(
    string Id,
    string? ClientId,
    string GroupId,
    string Title,
    string Amount,
    string Currency,
    string PaidBy,
    string? CategoryId,
    string? IconSymbol,
    string? Notes,
    RecurrenceResponse Recurrence,
    RecurringSplitResponse Split,
    string Status,
    string? PauseReason,
    string? NextRunAt,
    string? LastRunAt,
    string? CreatedBy,
    string CreatedAt,
    int Version,
    bool Deleted);

/// <summary>The recurrence block echoed back — the stored typed columns (EXT-D3a).</summary>
public sealed record RecurrenceResponse(
    string Freq,
    int Interval,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ByMonthDay,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ByWeekday,
    string Timezone,
    string StartsOn,
    string? EndsOn,
    int? RemainingCount);

/// <summary>
/// The split-template representation. Mirrors the Expenses split tagged union, plus the additive optional
/// <c>dynamic</c> flag: <c>{"type":"equal","dynamic":true}</c> is dynamic-equal (memberless, splits over the
/// active set at fire time, EXT-D3d). Unused arrays are omitted so the wire stays a clean tagged union.
/// </summary>
public sealed record RecurringSplitResponse
{
    public required string Type { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Dynamic { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Among { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExactSplitEntryDto>? Amounts { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ShareSplitEntryDto>? Weights { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PercentSplitEntryDto>? Percents { get; init; }
}

/// <summary>Computed occurrence-preview envelope (X2 — no stored calendar table, §3.4).</summary>
public sealed record OccurrencesResponse(
    string RuleId,
    string Timezone,
    IReadOnlyList<OccurrenceItem> Occurrences);

/// <summary>One previewed occurrence: the local <c>occurrenceDate</c> and its UTC <c>runsAt</c> instant.</summary>
public sealed record OccurrenceItem(string OccurrenceDate, string RunsAt);

using System.Text.Json.Serialization;

namespace Paybitch.Api.Features.Expenses;

/// <summary>
/// The mutable business body shared by <see cref="CreateExpenseRequest"/> and
/// <see cref="ReplaceExpenseRequest"/> (§3.2, §3.2b). Every value binds as a string / loose DTO so the
/// FluentValidation twins own the 422 codes — a malformed JSON number never short-circuits to a raw 400.
/// </summary>
public interface IExpenseBody
{
    string? Title { get; }
    string? Amount { get; }        // string minor units (D1)
    string? Currency { get; }
    string? PaidBy { get; }        // group_members.id (Guid string)
    string? Date { get; }          // ISO date "yyyy-MM-dd"
    SplitDto? Split { get; }
    string? CategoryId { get; }
    string? IconSymbol { get; }
    string? Notes { get; }
}

/// <summary>POST body (§3.2). <c>clientId</c> is the offline id that drives (groupId, clientId) idempotency (D9).</summary>
public sealed record CreateExpenseRequest : IExpenseBody
{
    public string? ClientId { get; init; }
    public string? Title { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    public string? PaidBy { get; init; }
    public string? Date { get; init; }
    public SplitDto? Split { get; init; }
    public string? CategoryId { get; init; }
    public string? IconSymbol { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// PUT body (§3.2b) — a full-aggregate replace, not a merge-patch. The immutable echo fields
/// (<c>id</c>, <c>groupId</c>, <c>clientId</c>, <c>createdBy</c>, <c>createdAt</c>) may be omitted or
/// echoed unchanged; a body that disagrees → 422 <c>immutable_field</c>.
/// </summary>
public sealed record ReplaceExpenseRequest : IExpenseBody
{
    // Immutable echo fields (server-owned).
    public string? Id { get; init; }
    public string? GroupId { get; init; }
    public string? ClientId { get; init; }
    public string? CreatedBy { get; init; }
    public string? CreatedAt { get; init; }

    // Mutable business body.
    public string? Title { get; init; }
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    public string? PaidBy { get; init; }
    public string? Date { get; init; }
    public SplitDto? Split { get; init; }
    public string? CategoryId { get; init; }
    public string? IconSymbol { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// The §3.4 split tagged union as one permissive DTO: <c>type</c> selects which of the four member
/// arrays is populated. Bound directly from JSON (camelCase) on input; on output the unused arrays are
/// omitted (<see cref="JsonIgnoreCondition.WhenWritingNull"/>) so the wire is a clean tagged union.
/// </summary>
public sealed record SplitDto
{
    public string? Type { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Among { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExactSplitEntryDto>? Amounts { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ShareSplitEntryDto>? Weights { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PercentSplitEntryDto>? Percents { get; init; }
}

/// <summary>EXACT entry: an explicit per-member minor-unit amount (string, D1).</summary>
public sealed record ExactSplitEntryDto(string MemberId, string Amount);

/// <summary>SHARES entry: an integer weight per member (largest-remainder resolved server-side).</summary>
public sealed record ShareSplitEntryDto(string MemberId, int Weight);

/// <summary>PERCENTAGE entry: integer basis points per member (1% = 100 bp; Σ == 10000).</summary>
public sealed record PercentSplitEntryDto(string MemberId, int BasisPoints);

/// <summary>The server-authoritative resolved owed amount for one member (§3.2 <c>shares</c>).</summary>
public sealed record ShareDto(string MemberId, string Amount);

/// <summary>
/// The canonical expense representation returned by every expense route and embedded in <c>/sync</c>
/// (§3.2, §3.5.1). Money is always a string of minor units (D1); <c>shares</c> are server-authoritative.
/// </summary>
public sealed record ExpenseResponse(
    string Id,
    string? ClientId,
    string GroupId,
    string Title,
    string Amount,
    string Currency,
    string PaidBy,
    string Date,
    SplitDto Split,
    IReadOnlyList<ShareDto> Shares,
    string? CategoryId,
    string? IconSymbol,
    string? Notes,
    string? CreatedBy,
    string CreatedAt,
    int Version,
    bool Deleted);

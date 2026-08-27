namespace Paybitch.Api.Features.Me;

/// <summary><c>GET /me</c> / <c>PATCH /me</c> profile projection (§3.1, §4.4).</summary>
public sealed record MeResponse(
    string Id,
    string DisplayName,
    string? Email,
    string DefaultCurrency,
    string Locale,
    DateTimeOffset CreatedAt);

/// <summary>
/// <c>PATCH /me</c> body — only <c>displayName</c>, <c>locale</c>, <c>defaultCurrency</c> are writable.
/// <see cref="AvatarUrl"/> / <see cref="ImageUrl"/> are declared solely to REJECT over-posting: any
/// non-null value → <c>422 immutable_field</c> (§3.12), never a silent ignore.
/// </summary>
public sealed record UpdateMeRequest(
    string? DisplayName,
    string? Locale,
    string? DefaultCurrency,
    string? AvatarUrl,
    string? ImageUrl);

/// <summary><c>GET /me/export</c> — Art. 15/20 data export envelope (§4.4).</summary>
public sealed record ExportResponse(
    DateTimeOffset ExportedAt,
    ExportProfile Profile,
    IReadOnlyList<ExportMembership> Memberships,
    IReadOnlyList<ExportExpense> Expenses,
    IReadOnlyList<ExportSettlement> Settlements);

public sealed record ExportProfile(
    string Id,
    string DisplayName,
    string? Email,
    string DefaultCurrency,
    string Locale,
    DateTimeOffset CreatedAt);

public sealed record ExportMembership(
    string GroupId,
    string GroupName,
    string MemberId,
    string Role,
    DateTimeOffset JoinedAt);

/// <summary>Money is D1: <c>amount</c>/<c>myShare</c> are string minor units with a sibling <c>currency</c>.</summary>
public sealed record ExportExpense(
    string GroupId,
    string Id,
    string Title,
    string Amount,
    string Currency,
    string PaidBy,
    DateOnly Date,
    string? MyShare,
    string? CreatedBy,
    IReadOnlyList<ExportSplit> Splits);

public sealed record ExportSplit(string MemberId, string Share);

public sealed record ExportSettlement(
    string GroupId,
    string Id,
    string From,
    string To,
    string Amount,
    string Currency,
    DateOnly SettledOn);

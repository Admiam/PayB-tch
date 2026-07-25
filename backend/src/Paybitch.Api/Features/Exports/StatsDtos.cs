namespace Paybitch.Api.Features.Exports;

/// <summary>
/// <c>GET /groups/{g}/stats</c> envelope (§4.4, EXT-D4g/h). Mirrors the <c>/balances</c> <c>byCurrency</c>
/// shape (§3.3): per-currency buckets, <b>never summed across</b> (D5). Money fields are string minor
/// units (D1). The optional <see cref="Display"/> block is the X3 quarantine — an approximate converted
/// total, present only when <c>convert</c> is supplied AND E2 (Live FX) has shipped; never an authoritative
/// bucket, never an export column.
/// </summary>
public sealed record StatsResponse(
    string GroupId,
    string Granularity,
    string Tz,
    IReadOnlyList<CurrencyStatsBucket> ByCurrency,
    ConvertedDisplay? Display = null);

/// <summary>One currency's spend, bucketed by period / category / member (EXT-D4g).</summary>
public sealed record CurrencyStatsBucket(
    string Currency,
    string TotalSpend,
    IReadOnlyList<PeriodSpend> ByPeriod,
    IReadOnlyList<CategorySpend> ByCategory,
    IReadOnlyList<MemberSpend> ByMember);

/// <summary>Spend in one time bucket. <c>period</c> is a <c>to_char</c> label (e.g. <c>2026-06</c>).</summary>
public sealed record PeriodSpend(string Period, string Spend);

/// <summary>Spend in one category; <see cref="CategoryId"/> null ⇒ uncategorized.</summary>
public sealed record CategorySpend(string? CategoryId, string Spend);

/// <summary>A member's <c>paid</c> (as payer) and <c>share</c> (their split slots) in the bucket currency.</summary>
public sealed record MemberSpend(string MemberId, string Paid, string Share);

/// <summary>
/// X3 quarantine block — an <b>indicative</b> converted total (string minor units of the TARGET currency
/// + rate provenance from E2/ČNB). Never enters <see cref="StatsResponse.ByCurrency"/>, balances,
/// settlements, or exports (EXT-D4h). Absent when <c>convert</c> is omitted or E2 hasn't shipped.
/// </summary>
public sealed record ConvertedDisplay(
    string Currency,
    string Amount,
    bool Approx,
    string RateDate,
    string Source,
    string Note);

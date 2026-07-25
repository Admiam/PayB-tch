using System.Globalization;
using System.Text.RegularExpressions;

namespace Paybitch.Api.Features.Expenses;

/// <summary>
/// Money / date wire helpers (D1). Amounts travel as a JSON <b>string</b> of minor units — never a
/// number — and must be canonical (no leading zeros, no sign) so the D9 idempotency byte-comparison is
/// well-defined (§3.4). Also the closed v1 currency set and the split-type kind constants.
/// </summary>
public static partial class ExpenseWire
{
    /// <summary>The closed v1 currency set (§2.1). A non-member → 422 <c>unsupported_currency</c>.</summary>
    public static readonly IReadOnlySet<string> AllowedCurrencies =
        new HashSet<string>(StringComparer.Ordinal) { "CZK", "EUR", "USD", "GBP" };

    [GeneratedRegex(@"^[1-9][0-9]*$")]
    private static partial Regex PositiveMinorRegex();

    [GeneratedRegex(@"^(0|[1-9][0-9]*)$")]
    private static partial Regex NonNegativeMinorRegex();

    /// <summary>A canonical, strictly positive minor-unit amount string that fits in a <see cref="long"/>.</summary>
    public static bool IsCanonicalPositiveMinor(string? s) =>
        s is not null
        && PositiveMinorRegex().IsMatch(s)
        && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
        && v > 0;

    /// <summary>A canonical, non-negative minor-unit amount string (allows "0") that fits in a <see cref="long"/>.</summary>
    public static bool IsCanonicalNonNegativeMinor(string? s) =>
        s is not null
        && NonNegativeMinorRegex().IsMatch(s)
        && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>Parse a pre-validated canonical minor-unit string.</summary>
    public static long ParseMinor(string s) => long.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>Emit minor units as a canonical wire string.</summary>
    public static string Minor(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Emit an ISO date "yyyy-MM-dd".</summary>
    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Parse a strict ISO date "yyyy-MM-dd".</summary>
    public static bool TryParseDate(string? s, out DateOnly date) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Emit an instant as an ISO-8601 UTC timestamp "yyyy-MM-ddTHH:mm:ssZ".</summary>
    public static string Timestamp(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>The four <c>expenses.split_type</c> values (§3.4 CHECK) and the domain↔string bridge.</summary>
public static class SplitKind
{
    public const string Equal = "equal";
    public const string Exact = "exact";
    public const string Shares = "shares";
    public const string Percentage = "percentage";

    public static string Of(Paybitch.Domain.Splitting.SplitType split) => split switch
    {
        Paybitch.Domain.Splitting.SplitType.Equal => Equal,
        Paybitch.Domain.Splitting.SplitType.Exact => Exact,
        Paybitch.Domain.Splitting.SplitType.Shares => Shares,
        Paybitch.Domain.Splitting.SplitType.Percentage => Percentage,
        _ => throw new Paybitch.Domain.InvalidSplitException("Unknown split type."),
    };
}

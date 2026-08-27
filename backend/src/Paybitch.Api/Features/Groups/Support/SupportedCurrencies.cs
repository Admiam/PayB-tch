namespace Paybitch.Api.Features.Groups.Support;

/// <summary>
/// The closed v1 currency set (§2.1, D2) as a boundary check for group <c>defaultCurrency</c>. The FK
/// to <c>currencies.code</c> is the DB backstop; this is the <c>422 unsupported_currency</c> twin so an
/// unknown code never surfaces as a 500.
/// </summary>
public static class SupportedCurrencies
{
    public static readonly IReadOnlySet<string> Codes =
        new HashSet<string>(StringComparer.Ordinal) { "CZK", "EUR", "USD", "GBP" };

    public static bool IsSupported(string? code) => code is not null && Codes.Contains(code);
}

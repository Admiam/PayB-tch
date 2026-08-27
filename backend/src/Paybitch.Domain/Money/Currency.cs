namespace Paybitch.Domain;

/// <summary>
/// A currency identified by its ISO-ish <see cref="Code"/> and its decimal
/// <see cref="Decimals"/> (scale). Per decision D2/G11 the scale is carried ON the
/// value — the domain never looks a scale up from a Code check. The application edge
/// builds instances from the <c>currencies</c> table (<c>minor_units</c>); the presets
/// here exist for tests and domain convenience only.
///
/// The v1 currency set is CLOSED to { CZK(0), EUR(2), USD(2), GBP(2) }.
/// </summary>
public readonly record struct Currency(string Code, int Decimals)
{
    // Presets — the closed v1 set. Runtime instances are built at the edge from
    // currencies.minor_units, NOT resolved from this list.
    public static readonly Currency CZK = new("CZK", 0);
    public static readonly Currency EUR = new("EUR", 2);
    public static readonly Currency USD = new("USD", 2);
    public static readonly Currency GBP = new("GBP", 2);

    // minor_units CHECK is 0..4; index by Decimals.
    private static readonly long[] Pow10 = { 1, 10, 100, 1_000, 10_000 };

    /// <summary>Minor units in one major unit (1 for CZK, 100 for EUR/USD/GBP).</summary>
    public long MinorPerMajor => Pow10[Decimals];

    /// <summary>
    /// Resolve a preset from the closed v1 set. Throws
    /// <see cref="UnsupportedCurrencyException"/> for any other code.
    /// </summary>
    public static Currency FromCode(string code) => code switch
    {
        "CZK" => CZK,
        "EUR" => EUR,
        "USD" => USD,
        "GBP" => GBP,
        _ => throw new UnsupportedCurrencyException(code),
    };
}

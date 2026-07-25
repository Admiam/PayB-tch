namespace Paybitch.Domain;

/// <summary>
/// Money as integer minor units tagged with a <see cref="Currency"/> (decision D1).
/// Never float, never NUMERIC — this makes "splits sum to total" a provable integer
/// partition invariant. All arithmetic is <c>checked</c> and currency-guarded.
/// </summary>
public readonly record struct Money(long Minor, Currency Currency) : IComparable<Money>
{
    public static Money Zero(Currency c) => new(0, c);

    public bool IsZero => Minor == 0;
    public bool IsPositive => Minor > 0;
    public bool IsNegative => Minor < 0;

    public Money Add(Money o) { Ensure(o); return this with { Minor = checked(Minor + o.Minor) }; }
    public Money Subtract(Money o) { Ensure(o); return this with { Minor = checked(Minor - o.Minor) }; }
    public Money Negate() => this with { Minor = checked(-Minor) };

    /// <summary>Compares magnitude; throws <see cref="CurrencyMismatchException"/> across currencies.</summary>
    public int CompareTo(Money o) { Ensure(o); return Minor.CompareTo(o.Minor); }

    public static bool operator <(Money a, Money b) => a.CompareTo(b) < 0;
    public static bool operator >(Money a, Money b) => a.CompareTo(b) > 0;
    public static bool operator <=(Money a, Money b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Money a, Money b) => a.CompareTo(b) >= 0;

    /// <summary>Major-unit decimal — JSON edge only, NEVER for money math.</summary>
    public decimal ToDecimal() => (decimal)Minor / Currency.MinorPerMajor;

    /// <summary>Banker's rounding (ToEven) at the decimal → minor-unit boundary.</summary>
    public static Money FromDecimal(decimal major, Currency c) =>
        new((long)Math.Round(major * c.MinorPerMajor, 0, MidpointRounding.ToEven), c);

    private void Ensure(Money o)
    {
        if (Currency != o.Currency)
            throw new CurrencyMismatchException(Currency, o.Currency);
    }
}

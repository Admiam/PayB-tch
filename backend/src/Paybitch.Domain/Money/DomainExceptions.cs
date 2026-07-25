namespace Paybitch.Domain;

/// <summary>Base type for every Paybitch domain-rule violation.</summary>
public abstract class PaybitchDomainException : Exception
{
    protected PaybitchDomainException(string message) : base(message) { }
}

/// <summary>Thrown when two <see cref="Money"/> values of different currencies are combined.</summary>
public sealed class CurrencyMismatchException : PaybitchDomainException
{
    public Currency Left { get; }
    public Currency Right { get; }

    public CurrencyMismatchException(Currency left, Currency right)
        : base($"Currency mismatch: {left.Code} vs {right.Code}.")
    {
        Left = left;
        Right = right;
    }
}

/// <summary>Thrown when a currency code is outside the closed v1 set { CZK, EUR, USD, GBP }.</summary>
public sealed class UnsupportedCurrencyException : PaybitchDomainException
{
    public string Code { get; }

    public UnsupportedCurrencyException(string code)
        : base($"Unsupported currency code '{code}'. v1 supports CZK, EUR, USD, GBP.")
    {
        Code = code;
    }
}

/// <summary>Thrown when a split cannot be resolved (bad weights, sums, or membership).</summary>
public sealed class InvalidSplitException : PaybitchDomainException
{
    public InvalidSplitException(string message) : base(message) { }
}

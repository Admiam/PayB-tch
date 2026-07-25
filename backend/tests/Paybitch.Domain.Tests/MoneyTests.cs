namespace Paybitch.Domain.Tests;

public class MoneyTests
{
    [Fact]
    public void Zero_has_zero_minor_and_the_currency()
    {
        var z = Money.Zero(Currency.EUR);
        Assert.Equal(0, z.Minor);
        Assert.Equal(Currency.EUR, z.Currency);
        Assert.True(z.IsZero);
    }

    [Fact]
    public void Add_and_Subtract_operate_on_minor_units()
    {
        var a = new Money(1000, Currency.EUR);
        var b = new Money(250, Currency.EUR);

        Assert.Equal(new Money(1250, Currency.EUR), a.Add(b));
        Assert.Equal(new Money(750, Currency.EUR), a.Subtract(b));
    }

    [Fact]
    public void Add_does_not_mutate_operands()
    {
        var a = new Money(1000, Currency.EUR);
        var b = new Money(250, Currency.EUR);
        _ = a.Add(b);
        Assert.Equal(1000, a.Minor);
        Assert.Equal(250, b.Minor);
    }

    [Fact]
    public void Negate_flips_sign()
    {
        Assert.Equal(new Money(-500, Currency.CZK), new Money(500, Currency.CZK).Negate());
        Assert.Equal(new Money(500, Currency.CZK), new Money(-500, Currency.CZK).Negate());
    }

    [Fact]
    public void Add_across_currencies_throws()
    {
        var eur = new Money(100, Currency.EUR);
        var czk = new Money(100, Currency.CZK);

        var ex = Assert.Throws<CurrencyMismatchException>(() => eur.Add(czk));
        Assert.Equal(Currency.EUR, ex.Left);
        Assert.Equal(Currency.CZK, ex.Right);
    }

    [Fact]
    public void Subtract_across_currencies_throws()
    {
        var eur = new Money(100, Currency.EUR);
        var usd = new Money(100, Currency.USD);
        Assert.Throws<CurrencyMismatchException>(() => eur.Subtract(usd));
    }

    [Fact]
    public void Add_overflow_throws_checked()
    {
        var big = new Money(long.MaxValue, Currency.EUR);
        Assert.Throws<OverflowException>(() => big.Add(new Money(1, Currency.EUR)));
    }

    [Fact]
    public void Comparison_helpers_compare_magnitude()
    {
        var small = new Money(100, Currency.EUR);
        var large = new Money(200, Currency.EUR);

        Assert.True(small < large);
        Assert.True(large > small);
        Assert.True(small <= new Money(100, Currency.EUR));
        Assert.True(large >= new Money(200, Currency.EUR));
        Assert.Equal(-1, small.CompareTo(large));
    }

    [Fact]
    public void Comparison_across_currencies_throws()
    {
        var eur = new Money(100, Currency.EUR);
        var czk = new Money(100, Currency.CZK);
        Assert.Throws<CurrencyMismatchException>(() => eur < czk);
    }

    [Theory]
    [InlineData(10000, "EUR", 100.00)]
    [InlineData(100, "CZK", 100)]
    [InlineData(1, "EUR", 0.01)]
    public void ToDecimal_scales_by_currency(long minor, string code, double expected)
    {
        var m = new Money(minor, Currency.FromCode(code));
        Assert.Equal((decimal)expected, m.ToDecimal());
    }

    [Theory]
    [InlineData(100.00, "EUR", 10000)]
    [InlineData(100, "CZK", 100)]
    [InlineData(0.005, "EUR", 0)]   // banker's rounding: 0.5 minor → to even (0)
    [InlineData(0.015, "EUR", 2)]   // 1.5 minor → to even (2)
    public void FromDecimal_uses_bankers_rounding(double major, string code, long expectedMinor)
    {
        var m = Money.FromDecimal((decimal)major, Currency.FromCode(code));
        Assert.Equal(expectedMinor, m.Minor);
    }

    [Fact]
    public void Value_equality_requires_same_minor_and_currency()
    {
        Assert.Equal(new Money(100, Currency.EUR), new Money(100, Currency.EUR));
        Assert.NotEqual(new Money(100, Currency.EUR), new Money(100, Currency.CZK));
        Assert.NotEqual(new Money(100, Currency.EUR), new Money(101, Currency.EUR));
    }
}

namespace Paybitch.Domain.Tests;

public class CurrencyTests
{
    [Theory]
    [InlineData("CZK", 0, 1)]
    [InlineData("EUR", 2, 100)]
    [InlineData("USD", 2, 100)]
    [InlineData("GBP", 2, 100)]
    public void FromCode_returns_preset_with_correct_scale(string code, int decimals, long minorPerMajor)
    {
        var c = Currency.FromCode(code);

        Assert.Equal(code, c.Code);
        Assert.Equal(decimals, c.Decimals);
        Assert.Equal(minorPerMajor, c.MinorPerMajor);
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("KWD")]
    [InlineData("czk")] // case-sensitive: lower-case is not the CZK preset
    [InlineData("")]
    public void FromCode_throws_for_codes_outside_closed_v1_set(string code)
    {
        var ex = Assert.Throws<UnsupportedCurrencyException>(() => Currency.FromCode(code));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Presets_match_FromCode()
    {
        Assert.Equal(Currency.FromCode("CZK"), Currency.CZK);
        Assert.Equal(Currency.FromCode("EUR"), Currency.EUR);
        Assert.Equal(Currency.FromCode("USD"), Currency.USD);
        Assert.Equal(Currency.FromCode("GBP"), Currency.GBP);
    }

    [Fact]
    public void Value_equality_holds_for_same_code_and_scale()
    {
        Assert.Equal(new Currency("EUR", 2), Currency.EUR);
        Assert.NotEqual(new Currency("EUR", 2), new Currency("EUR", 0));
    }
}

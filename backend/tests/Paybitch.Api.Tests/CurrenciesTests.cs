using System.Net;
using Paybitch.Api.Tests.Support;

namespace Paybitch.Api.Tests;

[Collection(ApiTestCollection.Name)]
public sealed class CurrenciesTests(PostgresFixture fixture)
{
    // #1 — GET /v1/currencies → 200, 4 currencies, CZK has minorUnits 0.
    [Fact]
    public async Task Lists_the_four_v1_currencies_unauthenticated()
    {
        var client = fixture.CreateAnonymousClient();

        var resp = await client.GetAsync("/v1/currencies");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();

        var codes = body.EnumerateArray()
            .Select(c => c.GetProperty("code").GetString())
            .OrderBy(c => c)
            .ToArray();
        Assert.Equal(new[] { "CZK", "EUR", "GBP", "USD" }, codes);

        var czk = body.EnumerateArray().Single(c => c.GetProperty("code").GetString() == "CZK");
        Assert.Equal(0, czk.GetProperty("minorUnits").GetInt32());
    }
}

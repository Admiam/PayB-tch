using System.Net;
using System.Net.Http.Json;
using Paybitch.Api.Tests.Support;

namespace Paybitch.Api.Tests;

[Collection(ApiTestCollection.Name)]
public sealed class SettlementTests(PostgresFixture fixture)
{
    // #5 — A settlement from B to the owner moves the ledger: B settles to 0, the owner drops to +3000.
    [Fact]
    public async Task Settlement_moves_balances()
    {
        var (_, client) = await fixture.NewUserClientAsync("Owner");
        var g = await client.SetupCzkGroupAsync();

        // Seed a debt: owner pays 9000, split equally ⇒ B and C each owe 3000.
        var expense = await client.PostAsJsonAsync($"/v1/groups/{g.GroupId}/expenses", new
        {
            clientId = Guid.NewGuid().ToString(),
            title = "Groceries",
            amount = "9000",
            currency = "CZK",
            paidBy = g.Owner.ToString(),
            date = ApiHelpers.TodayIso(),
            split = new { type = "equal", among = new[] { g.Owner.ToString(), g.B.ToString(), g.C.ToString() } },
        });
        Assert.Equal(HttpStatusCode.Created, expense.StatusCode);

        // B pays the owner 3000.
        var settlement = await client.PostAsJsonAsync($"/v1/groups/{g.GroupId}/settlements", new
        {
            clientId = Guid.NewGuid().ToString(),
            fromMember = g.B.ToString(),
            toMember = g.Owner.ToString(),
            amount = "3000",
            currency = "CZK",
            method = (string?)null,
            settledOn = ApiHelpers.TodayIso(),
            notes = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, settlement.StatusCode);

        var balances = await (await client.GetAsync($"/v1/groups/{g.GroupId}/balances")).ReadJsonAsync();

        // B is fully settled ⇒ omitted from the (non-zero) bucket; the owner is now owed only 3000 (by C).
        Assert.Null(ApiHelpers.NetOf(balances, "CZK", g.B));
        Assert.Equal("3000", ApiHelpers.NetOf(balances, "CZK", g.Owner));
        Assert.Equal("-3000", ApiHelpers.NetOf(balances, "CZK", g.C));
        Assert.Equal(0, ApiHelpers.BucketNetSum(balances, "CZK"));
    }
}

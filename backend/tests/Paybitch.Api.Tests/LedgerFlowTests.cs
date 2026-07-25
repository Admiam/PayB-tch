using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Paybitch.Api.Tests.Support;

namespace Paybitch.Api.Tests;

[Collection(ApiTestCollection.Name)]
public sealed class LedgerFlowTests(PostgresFixture fixture)
{
    private sealed record Ledger(Guid GroupId, Guid Owner, Guid B, Guid C);

    /// <summary>Create a CZK group with an owner plus two ghost members (B, C).</summary>
    private static async Task<Ledger> SetupAsync(HttpClient client)
    {
        var createResp = await client.CreateGroupAsync("Trip", "CZK");
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        Assert.Equal("\"1\"", createResp.ETag());
        var group = await createResp.ReadJsonAsync();
        var groupId = Guid.Parse(group.GetProperty("id").GetString()!);
        Assert.Equal("CZK", group.GetProperty("defaultCurrency").GetString());

        var owner = await client.OwnerMemberIdAsync(groupId);
        var b = await client.AddGhostMemberAsync(groupId, "B");
        var c = await client.AddGhostMemberAsync(groupId, "C");
        return new Ledger(groupId, owner, b, c);
    }

    private static object EqualExpenseBody(Ledger l, string clientId, string amount) => new
    {
        clientId,
        title = "Dinner",
        amount,
        currency = "CZK",
        paidBy = l.Owner.ToString(),
        date = ApiHelpers.TodayIso(),
        split = new
        {
            type = "equal",
            among = new[] { l.Owner.ToString(), l.B.ToString(), l.C.ToString() },
        },
    };

    // #3 — Full ledger flow: create expense, assert server-authoritative shares, then read balances.
    [Fact]
    public async Task Equal_split_expense_produces_correct_shares_and_balances()
    {
        var (_, client) = await fixture.NewUserClientAsync("Owner");
        var l = await SetupAsync(client);

        var clientId = Guid.NewGuid().ToString();
        var resp = await client.PostAsJsonAsync(
            $"/v1/groups/{l.GroupId}/expenses", EqualExpenseBody(l, clientId, "9000"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("\"1\"", resp.ETag());

        var expense = await resp.ReadJsonAsync();
        Assert.Equal("9000", expense.GetProperty("amount").GetString());

        var shares = expense.GetProperty("shares").EnumerateArray().ToList();
        Assert.Equal(3, shares.Count);
        long shareSum = 0;
        foreach (var s in shares)
        {
            var minor = long.Parse(s.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(3000, minor);
            shareSum += minor;
        }

        Assert.Equal(9000, shareSum);

        // Balances: paid − owed. Owner paid 9000, owes 3000 ⇒ +6000; B, C each owe 3000 ⇒ −3000.
        var balResp = await client.GetAsync($"/v1/groups/{l.GroupId}/balances");
        Assert.Equal(HttpStatusCode.OK, balResp.StatusCode);
        var balances = await balResp.ReadJsonAsync();

        Assert.Equal("6000", ApiHelpers.NetOf(balances, "CZK", l.Owner));
        Assert.Equal("-3000", ApiHelpers.NetOf(balances, "CZK", l.B));
        Assert.Equal("-3000", ApiHelpers.NetOf(balances, "CZK", l.C));
        Assert.Equal(0, ApiHelpers.BucketNetSum(balances, "CZK"));

        // A simplified (min-cash-flow) debt set is present: B→Owner and C→Owner.
        var edges = ApiHelpers.SimplifiedEdges(balances, "CZK");
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(l.Owner.ToString(), e.GetProperty("to").GetString()));
    }

    // #4 — Idempotency: same clientId + identical body replays; same clientId + different body conflicts.
    [Fact]
    public async Task Same_clientId_replays_identically_but_conflicts_on_divergent_body()
    {
        var (_, client) = await fixture.NewUserClientAsync("Owner");
        var l = await SetupAsync(client);

        var clientId = Guid.NewGuid().ToString();
        var body = EqualExpenseBody(l, clientId, "9000");

        var first = await client.PostAsJsonAsync($"/v1/groups/{l.GroupId}/expenses", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await first.ReadJsonAsync()).GetProperty("id").GetString();

        // Identical replay ⇒ 200 with the SAME expense id (never a second row).
        var replay = await client.PostAsJsonAsync($"/v1/groups/{l.GroupId}/expenses", body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstId, (await replay.ReadJsonAsync()).GetProperty("id").GetString());

        // Same clientId, different amount ⇒ 409 client_id_conflict carrying the existing id.
        var conflicting = EqualExpenseBody(l, clientId, "12000");
        var conflict = await client.PostAsJsonAsync($"/v1/groups/{l.GroupId}/expenses", conflicting);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.ReadJsonAsync();
        Assert.Equal("client_id_conflict", problem.GetProperty("code").GetString());
        Assert.Equal(firstId, problem.GetProperty("existingId").GetString());
    }
}

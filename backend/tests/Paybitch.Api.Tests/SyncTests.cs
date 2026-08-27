using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Paybitch.Api.Tests.Support;

namespace Paybitch.Api.Tests;

[Collection(ApiTestCollection.Name)]
public sealed class SyncTests(PostgresFixture fixture)
{
    // #8 — Delta sync: a no-cursor call mints a head cursor; replaying from it after creating a group,
    // members and an expense returns those change entries (money embedded as strings).
    [Fact]
    public async Task Sync_returns_group_member_and_expense_changes_since_head_cursor()
    {
        var (_, client) = await fixture.NewUserClientAsync("Owner");

        // No `since` ⇒ head cursor, empty changes.
        var head = await client.GetAsync("/v1/sync");
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        var headBody = await head.ReadJsonAsync();
        Assert.Equal(0, headBody.GetProperty("changes").GetArrayLength());
        Assert.False(headBody.GetProperty("hasMore").GetBoolean());
        var cursor0 = headBody.GetProperty("nextCursor").GetString()!;

        // Produce ledger activity after the head cursor.
        var g = await client.SetupCzkGroupAsync();
        var expenseResp = await client.PostAsJsonAsync($"/v1/groups/{g.GroupId}/expenses", new
        {
            clientId = Guid.NewGuid().ToString(),
            title = "Sync me",
            amount = "9000",
            currency = "CZK",
            paidBy = g.Owner.ToString(),
            date = ApiHelpers.TodayIso(),
            split = new { type = "equal", among = new[] { g.Owner.ToString(), g.B.ToString(), g.C.ToString() } },
        });
        Assert.Equal(HttpStatusCode.Created, expenseResp.StatusCode);
        var expenseId = (await expenseResp.ReadJsonAsync()).GetProperty("id").GetString();

        // Delta from the earlier cursor must include the group, its members, and the expense.
        var delta = await client.GetAsync($"/v1/sync?since={Uri.EscapeDataString(cursor0)}");
        Assert.Equal(HttpStatusCode.OK, delta.StatusCode);
        var changes = (await delta.ReadJsonAsync()).GetProperty("changes").EnumerateArray().ToList();

        var gid = g.GroupId.ToString();
        Assert.Contains(changes, c =>
            c.GetProperty("type").GetString() == "group" && c.GetProperty("id").GetString() == gid);

        var memberChanges = changes.Where(c => c.GetProperty("type").GetString() == "member").ToList();
        Assert.True(memberChanges.Count >= 3, "expected owner + B + C member changes");

        var expenseChange = changes.Single(c =>
            c.GetProperty("type").GetString() == "expense" && c.GetProperty("id").GetString() == expenseId);
        Assert.False(expenseChange.GetProperty("deleted").GetBoolean());

        var data = expenseChange.GetProperty("data");
        // Money is always a JSON string of minor units (D1), both at the top level and per share.
        Assert.Equal(JsonValueKind.String, data.GetProperty("amount").ValueKind);
        Assert.Equal("9000", data.GetProperty("amount").GetString());
        var shares = data.GetProperty("shares").EnumerateArray().ToList();
        Assert.Equal(3, shares.Count);
        Assert.All(shares, s => Assert.Equal(JsonValueKind.String, s.GetProperty("amount").ValueKind));
    }
}

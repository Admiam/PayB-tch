using System.Net;
using System.Net.Http.Json;
using Paybitch.Api.Tests.Support;

namespace Paybitch.Api.Tests;

[Collection(ApiTestCollection.Name)]
public sealed class ConcurrencyTests(PostgresFixture fixture)
{
    // #6 — Optimistic concurrency on a full-aggregate PUT: If-Match is mandatory (428), a stale value
    // conflicts (412), and the correct value succeeds (200) and bumps the version.
    [Fact]
    public async Task Replace_expense_enforces_if_match_precondition()
    {
        var (_, client) = await fixture.NewUserClientAsync("Owner");
        var g = await client.SetupCzkGroupAsync();

        var created = await client.PostAsJsonAsync($"/v1/groups/{g.GroupId}/expenses", new
        {
            clientId = Guid.NewGuid().ToString(),
            title = "Taxi",
            amount = "9000",
            currency = "CZK",
            paidBy = g.Owner.ToString(),
            date = ApiHelpers.TodayIso(),
            split = new { type = "equal", among = new[] { g.Owner.ToString(), g.B.ToString(), g.C.ToString() } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.ETag());
        var expenseId = (await created.ReadJsonAsync()).GetProperty("id").GetString();

        var replaceBody = new
        {
            title = "Taxi (updated)",
            amount = "9000",
            currency = "CZK",
            paidBy = g.Owner.ToString(),
            date = ApiHelpers.TodayIso(),
            split = new { type = "equal", among = new[] { g.Owner.ToString(), g.B.ToString(), g.C.ToString() } },
        };
        var url = $"/v1/groups/{g.GroupId}/expenses/{expenseId}";

        // No If-Match ⇒ 428 precondition_required.
        var noPrecondition = await Put(client, url, replaceBody, ifMatch: null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, noPrecondition.StatusCode);
        Assert.Equal("precondition_required", (await noPrecondition.ReadJsonAsync()).GetProperty("code").GetString());

        // Stale If-Match "0" ⇒ 412 version_conflict.
        var stale = await Put(client, url, replaceBody, ifMatch: "\"0\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("version_conflict", (await stale.ReadJsonAsync()).GetProperty("code").GetString());

        // Correct If-Match "1" ⇒ 200 and the version bumps to 2.
        var ok = await Put(client, url, replaceBody, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("\"2\"", ok.ETag());
        var updated = await ok.ReadJsonAsync();
        Assert.Equal(2, updated.GetProperty("version").GetInt32());
        Assert.Equal("Taxi (updated)", updated.GetProperty("title").GetString());
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, string url, object body, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request);
    }
}

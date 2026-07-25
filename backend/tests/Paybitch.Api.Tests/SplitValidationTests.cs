using System.Net;
using System.Net.Http.Json;
using Paybitch.Api.Tests.Support;

namespace Paybitch.Api.Tests;

[Collection(ApiTestCollection.Name)]
public sealed class SplitValidationTests(PostgresFixture fixture)
{
    // #7 — An EXACT split whose per-member amounts do not sum to the total is rejected at the boundary
    // with 422 split_sum_mismatch (the deferred DB trigger is only the backstop, never reached here).
    [Fact]
    public async Task Exact_split_that_does_not_sum_to_total_is_422_split_sum_mismatch()
    {
        var (_, client) = await fixture.NewUserClientAsync("Owner");
        var g = await client.SetupCzkGroupAsync();

        var resp = await client.PostAsJsonAsync($"/v1/groups/{g.GroupId}/expenses", new
        {
            clientId = Guid.NewGuid().ToString(),
            title = "Mismatch",
            amount = "9000",
            currency = "CZK",
            paidBy = g.Owner.ToString(),
            date = ApiHelpers.TodayIso(),
            split = new
            {
                type = "exact",
                amounts = new[]
                {
                    new { memberId = g.Owner.ToString(), amount = "3000" },
                    new { memberId = g.B.ToString(), amount = "3000" },
                },
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var problem = await resp.ReadJsonAsync();
        Assert.Equal("split_sum_mismatch", problem.GetProperty("code").GetString());
    }
}

using System.Net;
using Paybitch.Api.Tests.Support;

namespace Paybitch.Api.Tests;

[Collection(ApiTestCollection.Name)]
public sealed class GroupsAuthTests(PostgresFixture fixture)
{
    // #2 — GET /v1/groups is auth-gated (401 without a token) and returns an empty page for a new user.
    [Fact]
    public async Task Groups_requires_auth_and_returns_empty_page_for_new_user()
    {
        var anonymous = fixture.CreateAnonymousClient();
        var unauthorized = await anonymous.GetAsync("/v1/groups");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var (_, client) = await fixture.NewUserClientAsync("Fresh");
        var ok = await client.GetAsync("/v1/groups");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var body = await ok.ReadJsonAsync();
        Assert.Equal(0, body.GetProperty("data").GetArrayLength());
    }
}

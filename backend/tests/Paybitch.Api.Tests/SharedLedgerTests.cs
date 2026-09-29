using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Paybitch.Api.Tests.Support;

namespace Paybitch.Api.Tests;

/// <summary>
/// E11 — cross-group identity. Two surfaces, one idea: the roster's <c>linkKey</c> says which rows the
/// server can already match, and <c>/me/shared-ledger</c> stores the ones only a human can.
///
/// The invariants worth a test are the ones whose failure invents money: a person holding two rows of
/// the same group would double a debt, a member claimed by two people has no consistent reading at all,
/// and a group the caller never joined must stay invisible rather than becoming addressable through a
/// settings endpoint.
/// </summary>
[Collection(ApiTestCollection.Name)]
public sealed class SharedLedgerTests(PostgresFixture fixture)
{
    // --- linkKey ---

    [Fact]
    public async Task Roster_gives_a_link_key_to_accounts_and_none_to_ghosts()
    {
        var (_, client) = await fixture.NewUserClientAsync("Linker");
        var group = await client.SetupCzkGroupAsync("Flat");

        var members = await (await client.GetAsync($"/v1/groups/{group.GroupId}/members")).ReadJsonAsync();

        foreach (var member in members.EnumerateArray())
        {
            var key = member.GetProperty("linkKey");
            if (member.GetProperty("isGhost").GetBoolean())
            {
                // A placeholder has no account behind it, so there is nothing to match on — which is
                // exactly why these are the rows a person has to pair by hand.
                Assert.Equal(JsonValueKind.Null, key.ValueKind);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(key.GetString()));
            }
        }
    }

    [Fact]
    public async Task One_account_carries_the_same_link_key_across_groups()
    {
        var (_, client) = await fixture.NewUserClientAsync("Linker");
        var flat = await client.SetupCzkGroupAsync("Flat");
        var trip = await client.SetupCzkGroupAsync("Trip");

        var inFlat = await LinkKeyOfSelfAsync(client, flat.GroupId, flat.Owner);
        var inTrip = await LinkKeyOfSelfAsync(client, trip.GroupId, trip.Owner);

        // The whole feature rests on this: two member rows, one person, said by the server so no human
        // has to.
        Assert.Equal(inFlat, inTrip);
    }

    [Fact]
    public async Task Two_accounts_never_share_a_link_key()
    {
        var (_, first) = await fixture.NewUserClientAsync("First");
        var (_, second) = await fixture.NewUserClientAsync("Second");

        var groupOfFirst = await first.SetupCzkGroupAsync("Flat");
        var groupOfSecond = await second.SetupCzkGroupAsync("Flat");

        var keyOfFirst = await LinkKeyOfSelfAsync(first, groupOfFirst.GroupId, groupOfFirst.Owner);
        var keyOfSecond = await LinkKeyOfSelfAsync(second, groupOfSecond.GroupId, groupOfSecond.Owner);

        Assert.NotEqual(keyOfFirst, keyOfSecond);
    }

    // --- configuration ---

    [Fact]
    public async Task A_fresh_account_has_nothing_pooled()
    {
        var (_, client) = await fixture.NewUserClientAsync("Fresh");

        var resp = await client.GetAsync("/v1/me/shared-ledger");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.ReadJsonAsync();
        Assert.Equal(0, body.GetProperty("groupIds").GetArrayLength());
        Assert.Equal(0, body.GetProperty("people").GetArrayLength());
        Assert.Equal(0, body.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Pooled_groups_and_pairings_survive_a_round_trip()
    {
        var (_, client) = await fixture.NewUserClientAsync("Pooler");
        var flat = await client.SetupCzkGroupAsync("Flat");
        var trip = await client.SetupCzkGroupAsync("Trip");
        var personId = Guid.CreateVersion7();

        var saved = await PutAsync(client, new
        {
            groupIds = new[] { flat.GroupId, trip.GroupId },
            people = new[] { new { id = personId, memberIds = new[] { flat.B, trip.B } } },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await (await client.GetAsync("/v1/me/shared-ledger")).ReadJsonAsync();
        Assert.Equal(2, body.GetProperty("groupIds").GetArrayLength());

        var people = body.GetProperty("people");
        Assert.Equal(1, people.GetArrayLength());
        Assert.Equal(personId, Guid.Parse(people[0].GetProperty("id").GetString()!));
        Assert.Equal(2, people[0].GetProperty("memberIds").GetArrayLength());
    }

    [Fact]
    public async Task A_full_replace_drops_what_it_leaves_out()
    {
        var (_, client) = await fixture.NewUserClientAsync("Pooler");
        var flat = await client.SetupCzkGroupAsync("Flat");
        var trip = await client.SetupCzkGroupAsync("Trip");

        await PutAsync(client, new
        {
            groupIds = new[] { flat.GroupId, trip.GroupId },
            people = new[] { new { id = Guid.CreateVersion7(), memberIds = new[] { flat.B, trip.B } } },
        });

        await PutAsync(client, new { groupIds = new[] { flat.GroupId }, people = Array.Empty<object>() });

        var body = await (await client.GetAsync("/v1/me/shared-ledger")).ReadJsonAsync();
        Assert.Equal(1, body.GetProperty("groupIds").GetArrayLength());
        Assert.Equal(0, body.GetProperty("people").GetArrayLength());
    }

    [Fact]
    public async Task A_person_holding_one_member_is_not_stored()
    {
        var (_, client) = await fixture.NewUserClientAsync("Pooler");
        var flat = await client.SetupCzkGroupAsync("Flat");

        // Not an error — every unpaired row already means this, so the row would say nothing.
        var resp = await PutAsync(client, new
        {
            groupIds = new[] { flat.GroupId },
            people = new[] { new { id = Guid.CreateVersion7(), memberIds = new[] { flat.B } } },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(0, (await resp.ReadJsonAsync()).GetProperty("people").GetArrayLength());
    }

    // --- refusals ---

    [Fact]
    public async Task One_person_cannot_hold_two_members_of_the_same_group()
    {
        var (_, client) = await fixture.NewUserClientAsync("Pooler");
        var flat = await client.SetupCzkGroupAsync("Flat");

        var resp = await PutAsync(client, new
        {
            groupIds = new[] { flat.GroupId },
            people = new[] { new { id = Guid.CreateVersion7(), memberIds = new[] { flat.B, flat.C } } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("shared_ledger_same_group", (await resp.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_member_cannot_belong_to_two_people()
    {
        var (_, client) = await fixture.NewUserClientAsync("Pooler");
        var flat = await client.SetupCzkGroupAsync("Flat");
        var trip = await client.SetupCzkGroupAsync("Trip");

        var resp = await PutAsync(client, new
        {
            groupIds = new[] { flat.GroupId, trip.GroupId },
            people = new[]
            {
                new { id = Guid.CreateVersion7(), memberIds = new[] { flat.B, trip.B } },
                new { id = Guid.CreateVersion7(), memberIds = new[] { flat.B, trip.C } },
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("shared_ledger_member_reused", (await resp.ReadJsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_group_the_caller_is_not_in_stays_invisible()
    {
        var (_, mine) = await fixture.NewUserClientAsync("Mine");
        var (_, theirs) = await fixture.NewUserClientAsync("Theirs");
        var stranger = await theirs.SetupCzkGroupAsync("Not yours");

        // D6: absent and forbidden are the same 404, so this endpoint cannot be used to find out
        // whether a group id exists.
        var resp = await PutAsync(mine, new { groupIds = new[] { stranger.GroupId }, people = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_member_the_caller_cannot_see_stays_invisible()
    {
        var (_, mine) = await fixture.NewUserClientAsync("Mine");
        var (_, theirs) = await fixture.NewUserClientAsync("Theirs");
        var ours = await mine.SetupCzkGroupAsync("Flat");
        var stranger = await theirs.SetupCzkGroupAsync("Not yours");

        var resp = await PutAsync(mine, new
        {
            groupIds = new[] { ours.GroupId },
            people = new[] { new { id = Guid.CreateVersion7(), memberIds = new[] { ours.B, stranger.B } } },
        });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_stale_precondition_is_refused_rather_than_flattened()
    {
        var (_, client) = await fixture.NewUserClientAsync("Pooler");
        var flat = await client.SetupCzkGroupAsync("Flat");
        var trip = await client.SetupCzkGroupAsync("Trip");

        var first = await PutAsync(client, new { groupIds = new[] { flat.GroupId }, people = Array.Empty<object>() });
        var staleTag = first.ETag();
        Assert.NotNull(staleTag);

        // A second device writes in between.
        await PutAsync(client, new { groupIds = new[] { flat.GroupId, trip.GroupId }, people = Array.Empty<object>() });

        var conflicted = await PutAsync(
            client,
            new { groupIds = Array.Empty<Guid>(), people = Array.Empty<object>() },
            staleTag);

        Assert.Equal(HttpStatusCode.PreconditionFailed, conflicted.StatusCode);

        // The write was refused outright, so the newer version is still what is stored.
        var body = await (await client.GetAsync("/v1/me/shared-ledger")).ReadJsonAsync();
        Assert.Equal(2, body.GetProperty("groupIds").GetArrayLength());
    }

    // --- helpers ---

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client, object body, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/me/shared-ledger")
        {
            Content = JsonContent.Create(body),
        };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        return await client.SendAsync(request);
    }

    private static async Task<string> LinkKeyOfSelfAsync(HttpClient client, Guid groupId, Guid memberId)
    {
        var members = await (await client.GetAsync($"/v1/groups/{groupId}/members")).ReadJsonAsync();

        foreach (var member in members.EnumerateArray())
        {
            if (Guid.Parse(member.GetProperty("id").GetString()!) == memberId)
                return member.GetProperty("linkKey").GetString()!;
        }

        throw new InvalidOperationException($"Member {memberId} is not in group {groupId}.");
    }
}

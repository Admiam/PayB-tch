using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Paybitch.Api.Tests.Support;

namespace Paybitch.Api.Tests;

/// <summary>
/// The §3.10 feed is only as real as the writes that populate it. Before these tests, every verb in
/// <c>ActivityVerbs</c> except <c>group.exported</c> and <c>member.role_changed</c> was unreachable:
/// no mutating handler called <see cref="Paybitch.Api.Features.Activity.IActivityWriter"/>, so
/// <c>GET /activity</c> answered with an empty page no matter what the group did — and the E5
/// notification chain, which reads <c>activity_log</c> through the notification cursor, had no input
/// at all.
/// </summary>
[Collection(ApiTestCollection.Name)]
public sealed class ActivityFeedTests(PostgresFixture fixture)
{
    private static object EqualExpenseBody(ApiHelpers.GroupSetup g, string clientId, string amount) => new
    {
        clientId,
        title = "Dinner",
        amount,
        currency = "CZK",
        paidBy = g.Owner.ToString(),
        date = ApiHelpers.TodayIso(),
        split = new
        {
            type = "equal",
            among = new[] { g.Owner.ToString(), g.B.ToString(), g.C.ToString() },
        },
    };

    private static async Task<IReadOnlyList<JsonElement>> FeedAsync(HttpClient client, Guid groupId)
    {
        var resp = await client.GetAsync($"/v1/groups/{groupId}/activity");
        resp.EnsureSuccessStatusCode();
        var page = await resp.ReadJsonAsync();
        return page.GetProperty("data").EnumerateArray().ToList();
    }

    private static JsonElement? FirstWithVerb(IReadOnlyList<JsonElement> feed, string verb)
    {
        foreach (var item in feed)
            if (item.GetProperty("verb").GetString() == verb)
                return item;
        return null;
    }

    [Fact]
    public async Task Creating_an_expense_writes_an_activity_row()
    {
        var (_, client) = await fixture.NewUserClientAsync();
        var g = await client.SetupCzkGroupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/v1/groups/{g.GroupId}/expenses", EqualExpenseBody(g, Guid.NewGuid().ToString(), "9000"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var expenseId = (await resp.ReadJsonAsync()).GetProperty("id").GetString();

        var created = FirstWithVerb(await FeedAsync(client, g.GroupId), "expense.created");
        Assert.NotNull(created);
        Assert.Equal("expense", created!.Value.GetProperty("targetType").GetString());
        Assert.Equal(expenseId, created.Value.GetProperty("targetId").GetString());
        // Hydrated at read time from the live row, never stored in the log.
        Assert.Equal("Dinner", created.Value.GetProperty("target").GetProperty("title").GetString());
    }

    /// <summary>
    /// D9: a replayed create returns the original expense and must NOT append a second row. The
    /// activity write sits after the replay short-circuit for exactly this reason.
    /// </summary>
    [Fact]
    public async Task Replayed_create_does_not_write_a_second_activity_row()
    {
        var (_, client) = await fixture.NewUserClientAsync();
        var g = await client.SetupCzkGroupAsync();
        var body = EqualExpenseBody(g, Guid.NewGuid().ToString(), "9000");

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync($"/v1/groups/{g.GroupId}/expenses", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync($"/v1/groups/{g.GroupId}/expenses", body)).StatusCode);

        var feed = await FeedAsync(client, g.GroupId);
        Assert.Equal(1, feed.Count(i => i.GetProperty("verb").GetString() == "expense.created"));
    }

    [Fact]
    public async Task Updating_an_expense_records_which_fields_changed()
    {
        var (_, client) = await fixture.NewUserClientAsync();
        var g = await client.SetupCzkGroupAsync();

        var create = await client.PostAsJsonAsync(
            $"/v1/groups/{g.GroupId}/expenses", EqualExpenseBody(g, Guid.NewGuid().ToString(), "9000"));
        var expenseId = (await create.ReadJsonAsync()).GetProperty("id").GetString();

        var update = new HttpRequestMessage(HttpMethod.Put, $"/v1/groups/{g.GroupId}/expenses/{expenseId}")
        {
            Content = JsonContent.Create(new
            {
                title = "Lunch",                    // changed
                amount = "12000",                   // changed
                currency = "CZK",
                paidBy = g.Owner.ToString(),
                date = ApiHelpers.TodayIso(),
                split = new
                {
                    type = "equal",
                    among = new[] { g.Owner.ToString(), g.B.ToString(), g.C.ToString() },
                },
            }),
        };
        update.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(update)).StatusCode);

        var updated = FirstWithVerb(await FeedAsync(client, g.GroupId), "expense.updated");
        Assert.NotNull(updated);

        var fields = updated!.Value.GetProperty("metadata").GetProperty("fields")
            .EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("title", fields);
        Assert.Contains("amount", fields);
        Assert.DoesNotContain("currency", fields);   // unchanged fields stay out

        // §3.10 / §4.3: metadata carries ids and field NAMES only — never a money value.
        var metadataJson = updated.Value.GetProperty("metadata").GetRawText();
        Assert.DoesNotContain("12000", metadataJson);
        Assert.DoesNotContain("9000", metadataJson);
    }

    [Fact]
    public async Task Deleting_an_expense_writes_a_tombstone_row()
    {
        var (_, client) = await fixture.NewUserClientAsync();
        var g = await client.SetupCzkGroupAsync();

        var create = await client.PostAsJsonAsync(
            $"/v1/groups/{g.GroupId}/expenses", EqualExpenseBody(g, Guid.NewGuid().ToString(), "9000"));
        var expenseId = (await create.ReadJsonAsync()).GetProperty("id").GetString();

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/v1/groups/{g.GroupId}/expenses/{expenseId}");
        delete.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);

        var deleted = FirstWithVerb(await FeedAsync(client, g.GroupId), "expense.deleted");
        Assert.NotNull(deleted);
        Assert.Equal(expenseId, deleted!.Value.GetProperty("targetId").GetString());
    }

    /// <summary>
    /// A rejected mutation must leave no trace. The activity row is staged on the same DbContext as
    /// the expense and its change_log entry, so a failed write rolls all three back together.
    /// </summary>
    [Fact]
    public async Task A_rejected_update_writes_no_activity_row()
    {
        var (_, client) = await fixture.NewUserClientAsync();
        var g = await client.SetupCzkGroupAsync();

        var create = await client.PostAsJsonAsync(
            $"/v1/groups/{g.GroupId}/expenses", EqualExpenseBody(g, Guid.NewGuid().ToString(), "9000"));
        var expenseId = (await create.ReadJsonAsync()).GetProperty("id").GetString();

        // Stale If-Match ⇒ 412, nothing applied.
        var stale = new HttpRequestMessage(HttpMethod.Put, $"/v1/groups/{g.GroupId}/expenses/{expenseId}")
        {
            Content = JsonContent.Create(new
            {
                title = "Lunch",
                amount = "12000",
                currency = "CZK",
                paidBy = g.Owner.ToString(),
                date = ApiHelpers.TodayIso(),
                split = new
                {
                    type = "equal",
                    among = new[] { g.Owner.ToString(), g.B.ToString(), g.C.ToString() },
                },
            }),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"99\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);

        var feed = await FeedAsync(client, g.GroupId);
        Assert.Null(FirstWithVerb(feed, "expense.updated"));
    }
}

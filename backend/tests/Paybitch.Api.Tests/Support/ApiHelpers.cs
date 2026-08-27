using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Paybitch.Api.Tests.Support;

/// <summary>
/// Thin helpers over the HTTP surface so each test reads as a ledger story, not JSON plumbing. Requests
/// go out as camelCase JSON (System.Net.Http.Json web defaults); responses are parsed into a detached
/// <see cref="JsonElement"/>.
/// </summary>
internal static class ApiHelpers
{
    /// <summary>Today (UTC) as the ISO date the expense/settlement date rules accept.</summary>
    internal static string TodayIso() =>
        DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Read the response body as a detached JSON element (survives document disposal).</summary>
    internal static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage resp)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    /// <summary>The strong ETag value including quotes, e.g. <c>"1"</c>, or null when absent.</summary>
    internal static string? ETag(this HttpResponseMessage resp) => resp.Headers.ETag?.Tag;

    // ---- group / member setup ----

    internal static Task<HttpResponseMessage> CreateGroupAsync(
        this HttpClient client, string name = "Trip", string currency = "CZK") =>
        client.PostAsJsonAsync("/v1/groups", new { name, defaultCurrency = currency, iconSymbol = (string?)null });

    /// <summary>The caller's own (non-ghost) member id in a group.</summary>
    internal static async Task<Guid> OwnerMemberIdAsync(this HttpClient client, Guid groupId)
    {
        var resp = await client.GetAsync($"/v1/groups/{groupId}/members");
        resp.EnsureSuccessStatusCode();
        var members = await resp.ReadJsonAsync();
        foreach (var m in members.EnumerateArray())
        {
            if (!m.GetProperty("isGhost").GetBoolean())
                return Guid.Parse(m.GetProperty("id").GetString()!);
        }

        throw new InvalidOperationException("No owner (non-ghost) member found in the group.");
    }

    /// <summary>Add a ghost participant and return its member id.</summary>
    internal static async Task<Guid> AddGhostMemberAsync(this HttpClient client, Guid groupId, string displayName)
    {
        var resp = await client.PostAsJsonAsync(
            $"/v1/groups/{groupId}/members",
            new { displayName, iconSymbol = (string?)null });
        resp.EnsureSuccessStatusCode();
        var body = await resp.ReadJsonAsync();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    /// <summary>A member set: the owner plus two ghosts (B, C) in a fresh CZK group.</summary>
    internal readonly record struct GroupSetup(Guid GroupId, Guid Owner, Guid B, Guid C);

    /// <summary>Create a CZK group with an owner and two ghost members and return their ids.</summary>
    internal static async Task<GroupSetup> SetupCzkGroupAsync(this HttpClient client, string name = "Trip")
    {
        var createResp = await client.CreateGroupAsync(name, "CZK");
        createResp.EnsureSuccessStatusCode();
        var group = await createResp.ReadJsonAsync();
        var groupId = Guid.Parse(group.GetProperty("id").GetString()!);

        var owner = await client.OwnerMemberIdAsync(groupId);
        var b = await client.AddGhostMemberAsync(groupId, "B");
        var c = await client.AddGhostMemberAsync(groupId, "C");
        return new GroupSetup(groupId, owner, b, c);
    }

    // ---- balances ----

    /// <summary>
    /// The signed net (minor-unit string) for a member in a currency bucket, or <c>null</c> when the
    /// member has no non-zero position (the API omits zero members / zero buckets).
    /// </summary>
    internal static string? NetOf(JsonElement balances, string currency, Guid memberId)
    {
        var id = memberId.ToString();
        foreach (var bucket in balances.GetProperty("byCurrency").EnumerateArray())
        {
            if (bucket.GetProperty("currency").GetString() != currency)
                continue;
            foreach (var b in bucket.GetProperty("balances").EnumerateArray())
            {
                if (b.GetProperty("memberId").GetString() == id)
                    return b.GetProperty("net").GetString();
            }
        }

        return null;
    }

    /// <summary>Sum of every member net in a currency bucket (must be 0 by the §3.3 invariant).</summary>
    internal static long BucketNetSum(JsonElement balances, string currency)
    {
        long sum = 0;
        foreach (var bucket in balances.GetProperty("byCurrency").EnumerateArray())
        {
            if (bucket.GetProperty("currency").GetString() != currency)
                continue;
            foreach (var b in bucket.GetProperty("balances").EnumerateArray())
                sum += long.Parse(b.GetProperty("net").GetString()!, CultureInfo.InvariantCulture);
        }

        return sum;
    }

    /// <summary>The simplified debt edges for a currency bucket (empty when the bucket is absent).</summary>
    internal static IReadOnlyList<JsonElement> SimplifiedEdges(JsonElement balances, string currency)
    {
        foreach (var bucket in balances.GetProperty("byCurrency").EnumerateArray())
        {
            if (bucket.GetProperty("currency").GetString() == currency)
                return bucket.GetProperty("simplified").EnumerateArray().ToList();
        }

        return [];
    }
}

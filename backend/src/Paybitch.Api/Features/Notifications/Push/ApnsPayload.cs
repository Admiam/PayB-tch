using System.Text.Json.Nodes;

namespace Paybitch.Api.Features.Notifications.Push;

/// <summary>
/// Builds the exact APNs JSON payload for a visible push (EXT-D5a, §5.4) — the single source of the
/// "no names/amounts on the wire" invariant. The alert is a generic name-free <c>loc-key</c> with NO
/// <c>loc-args</c>; the custom top-level fields are ids only (<c>kind</c>, <c>groupId</c>, <c>entityId</c>)
/// plus an optional numeric <c>count</c> for a summary.
/// </summary>
public static class ApnsPayload
{
    public static JsonObject Build(ApnsPush push)
    {
        var alert = new JsonObject { ["loc-key"] = push.LocKey };

        var aps = new JsonObject
        {
            ["mutable-content"] = 1,
            ["sound"] = "default",
            ["alert"] = alert,
        };

        var payload = new JsonObject
        {
            ["aps"] = aps,
            ["v"] = 1,
            ["kind"] = push.Kind,
            ["groupId"] = push.GroupId.ToString(),
        };

        if (push.EntityId is { } entityId)
            payload["entityId"] = entityId.ToString();

        if (push.Count is { } count)
            payload["count"] = count;   // a number, not PII — the NSE renders "N new …" locally

        return payload;
    }

    /// <summary>The serialized wire form (also what the dev sender logs / a test asserts against).</summary>
    public static string Serialize(ApnsPush push) => Build(push).ToJsonString();
}

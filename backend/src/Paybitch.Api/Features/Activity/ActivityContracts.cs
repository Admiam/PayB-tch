using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Paybitch.Api.Features.Activity;

/// <summary>
/// One §3.10 feed item. <c>actor</c> and <c>target</c> are hydrated from the live rows at read time
/// (never stored in the log) and are <c>null</c> when the actor was anonymized (D7) or the target row
/// is gone — the client then renders a generic line. <c>metadata</c> carries ids + field NAMES only.
/// </summary>
public sealed record ActivityItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("verb")] string Verb,
    [property: JsonPropertyName("actor")] ActorInfo? Actor,
    [property: JsonPropertyName("targetType")] string TargetType,
    [property: JsonPropertyName("targetId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetId,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Metadata,
    [property: JsonPropertyName("target"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TargetInfo? Target,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <summary>The acting user, hydrated from <c>users</c>. Null if the user was anonymized (actor_user cleared, D7).</summary>
public sealed record ActorInfo(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("displayName")] string DisplayName);

/// <summary>
/// Read-time display fields joined from the live target row (§3.10). Only the fields relevant to the
/// target type are populated; money stays D1 (string minor units + currency). Omitted keys are
/// absent from the wire.
/// </summary>
public sealed record TargetInfo(
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonPropertyName("displayName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName = null,
    [property: JsonPropertyName("amount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Amount = null,
    [property: JsonPropertyName("currency"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency = null,
    [property: JsonPropertyName("archived"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Archived = null,
    [property: JsonPropertyName("deleted")] bool Deleted = false);

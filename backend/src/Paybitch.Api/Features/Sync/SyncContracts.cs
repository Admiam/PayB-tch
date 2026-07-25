using System.Text.Json.Serialization;

namespace Paybitch.Api.Features.Sync;

/// <summary>
/// The §3.5.4 delta-sync envelope. Deliberately NOT the §3.4 <c>{ data, page }</c> wrapper — the
/// cursor here is durable sync state, not scroll state. <c>changes</c> is applied in <c>seq</c>
/// order as idempotent upserts; a tombstone carries <c>data: null</c>.
/// </summary>
public sealed record SyncResponse(
    [property: JsonPropertyName("changes")] IReadOnlyList<SyncChange> Changes,
    [property: JsonPropertyName("nextCursor")] string NextCursor,
    [property: JsonPropertyName("hasMore")] bool HasMore);

/// <summary>
/// One change-log entry rendered for the wire. <c>data</c> is the entity's full CURRENT representation
/// — the SAME record type the REST routes return (<c>ExpenseResponse</c>, <c>GroupResponse</c>,
/// <c>MemberResponse</c>, <c>SettlementResponse</c>, <see cref="SyncCategoryData"/>) so <c>/sync</c> and
/// REST are byte-identical — or <c>null</c> for a tombstone / access change. Typed <c>object?</c> so
/// System.Text.Json serializes the concrete payload's runtime shape.
/// </summary>
public sealed record SyncChange(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("data")] object? Data);

/// <summary>
/// <c>category</c> sync payload. The REST <c>CategoryResponse</c> (id, groupId, name, iconSymbol) is
/// intentionally extended here with <c>updatedAt</c> and <c>deleted</c>: the client applies categories
/// last-received-wins by <c>updatedAt</c> (they carry no <c>version</c>) and must keep soft-deleted
/// categories for historical expense rendering (§3.5.4). Field names for the shared columns match
/// <c>CategoryResponse</c> so both decode into the same client model.
/// </summary>
public sealed record SyncCategoryData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("iconSymbol"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IconSymbol,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("deleted")] bool Deleted);

using System.Text.Json.Serialization;

namespace Paybitch.Api.Common.Pagination;

/// <summary>
/// The §3.4 cursor pagination envelope: <c>{ data, page: { nextCursor, hasMore, limit } }</c>.
/// Cursor over offset because the ledger mutates mid-scroll. This is the LIST envelope; the durable
/// <c>/sync</c> stream uses its own wire format (§3.5.4), not this type.
/// </summary>
public sealed record Page<T>(
    [property: JsonPropertyName("data")] IReadOnlyList<T> Data,
    [property: JsonPropertyName("page")] PageInfo Meta)
{
    /// <summary>
    /// Build a page from a slice fetched with <c>limit + 1</c> rows: if the extra row is present there
    /// is another page, and <paramref name="cursorFor"/> is invoked on the last returned row to mint
    /// <c>nextCursor</c>.
    /// </summary>
    public static Page<T> From(IReadOnlyList<T> fetched, int limit, Func<T, string> cursorFor)
    {
        var hasMore = fetched.Count > limit;
        var data = hasMore ? fetched.Take(limit).ToArray() : fetched;
        var nextCursor = hasMore && data.Count > 0 ? cursorFor(data[^1]) : null;
        return new Page<T>(data, new PageInfo(nextCursor, hasMore, limit));
    }
}

/// <summary>The <c>page</c> block of the pagination envelope.</summary>
public sealed record PageInfo(
    [property: JsonPropertyName("nextCursor")] string? NextCursor,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("limit")] int Limit);

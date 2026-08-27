using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Activity;

/// <summary>
/// <c>GET /v1/groups/{groupId}/activity</c> — the §3.10 activity feed. D6 membership first (non-member
/// ⇒ 404); reads work on archived groups (history stays reachable, §3.7). Cursor envelope per §3.4,
/// newest first, hydrated at read time.
/// </summary>
public sealed class ActivityModule : IEndpointModule
{
    // No Appendix A tunable governs generic list page size; the §3.4 examples use 25. Max clamps abuse.
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/groups/{groupId}/activity", HandleAsync)
            .RequireGroupMembership()
            .WithName("GroupActivity")
            .WithSummary("Group activity feed (cursor, hydrated at read time).")
            .WithTags("Activity");
    }

    private static async Task<IResult> HandleAsync(
        Guid groupId,
        AppDbContext db,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var service = new ActivityService(db);
        var page = await service.ReadAsync(groupId, cursor, pageSize, ct);
        return Results.Ok(page);
    }
}

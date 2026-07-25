using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Common.Pagination;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Sync;

/// <summary>
/// <c>GET /v1/sync?since=&lt;cursor&gt;&amp;limit=n</c> — the §3.5 delta feed. Authenticated but NOT
/// group-scoped: one global cursor per user, filtered server-side to the caller's current
/// memberships plus their own <c>access</c> rows (§3.5.3).
/// </summary>
public sealed class SyncModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/sync", HandleAsync)
            .WithName("DeltaSync")
            .WithSummary("Delta sync — changes + tombstones since a cursor; omit `since` to mint a head cursor.")
            .WithTags("Sync");
    }

    private static async Task<IResult> HandleAsync(
        ICurrentUser currentUser,
        AppDbContext db,
        IOptions<OperationalConstants> options,
        string? since,
        int? limit,
        CancellationToken ct)
    {
        var ops = options.Value;
        var me = currentUser.UserId;
        var query = new SyncQueryService(db);

        // No `since` ⇒ cheap "mint me a cursor" call: head cursor, empty changes (§3.5.4). The client
        // then bootstraps via REST and resumes delta polling from this cursor (§3.5.5).
        if (string.IsNullOrEmpty(since))
        {
            var head = await query.GetHeadSeqAsync(ct);
            return Results.Ok(new SyncResponse([], Cursor.EncodeSeq(head), false));
        }

        // Opaque cursor. An undecodable `since` can't anchor a gap-free continuation, so — like a
        // pruned one — it forces a re-bootstrap (410) rather than risking silent data loss.
        if (!Cursor.TryDecodeSeq(since, out var after))
            return Problems.Gone(ProblemCodes.SyncCursorExpired);

        var pageSize = Math.Clamp(limit ?? ops.SyncPageSizeDefault, 1, ops.SyncPageSizeMax);

        var page = await query.ReadPageAsync(me, after, pageSize, ct);

        // §3.5.5: 410 iff decoded `since` < pruned_through_seq (== the row was pruned). `since` equal to
        // the watermark is still valid — everything above it is retained.
        if (after < page.PrunedThroughSeq)
            return Problems.Gone(ProblemCodes.SyncCursorExpired);

        var raw = page.Rows;
        var hasMore = raw.Count > pageSize;
        var pageRows = hasMore ? raw.Take(pageSize).ToList() : raw;

        // Empty page: echo `since` as nextCursor (§3.5.4).
        if (pageRows.Count == 0)
            return Results.Ok(new SyncResponse([], since, false));

        var hydrator = new SyncHydrator(db);
        var changes = await hydrator.HydrateAsync(pageRows, ct);

        var nextCursor = Cursor.EncodeSeq(pageRows[^1].Seq);
        return Results.Ok(new SyncResponse(changes, nextCursor, hasMore));
    }
}

using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Pagination;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Comments;

/// <summary>
/// Handlers for the E10a comment thread (§7). Every route is behind the D6 membership filter (404 for
/// non-members), rejects an archived group (409 <c>group_archived</c>, §3.7), and appends its
/// <c>comment</c> change_log row in the SAME transaction as the mutation (§7.5). Comments are money-inert
/// (§7.1) — no <c>amount</c>/<c>currency</c> anywhere. The author is server-derived from the token, never
/// the body (EXT-DC7). Full X1 contract: (groupId, clientId) create idempotency (D9), <c>version</c> +
/// <c>If-Match</c> (D8), soft-delete tombstone surfaced in <c>/sync</c>.
/// </summary>
public static class CommentEndpoints
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    private static readonly HashSet<string> AdminRoles = new(StringComparer.Ordinal) { "owner", "admin" };

    // ---------- LIST (§7.4 cursor, created_at ASC — chronological transcript) ----------

    public static async Task<IResult> ListAsync(
        string expenseId,
        HttpContext http,
        AppDbContext db,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        if (!Guid.TryParse(expenseId, out var eid))
            return Problems.NotFound();
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        DateTimeOffset? afterCreated = null;
        Guid? afterId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!Cursor.TryDecode(cursor, out var payload) || !TryParseListCursor(payload, out var c0, out var id0))
                return Problems.Validation(ProblemCodes.ValidationFailed, "cursor", "Malformed cursor.");
            afterCreated = c0;
            afterId = id0;
        }

        // Keyset over ix_comments_expense (created_at ASC, id ASC), live rows only (deleted_at IS NULL) —
        // a soft-deleted expense's pre-existing thread still lists (EXT-DC8). Row-value comparison uses
        // native uuid order for the id tiebreak.
        var hasCursor = afterCreated is not null;
        var sql = "SELECT id FROM comments WHERE group_id = @groupId AND expense_id = @expenseId AND deleted_at IS NULL"
                  + (hasCursor ? " AND (created_at, id) > (@afterCreated, @afterId)" : string.Empty)
                  + " ORDER BY created_at ASC, id ASC LIMIT @lim";
        object parameters = hasCursor
            ? new { groupId = m.GroupId, expenseId = eid, afterCreated = afterCreated!.Value, afterId = afterId!.Value, lim = take + 1 }
            : new { groupId = m.GroupId, expenseId = eid, lim = take + 1 };

        var conn = db.Database.GetDbConnection();
        var ids = (await conn.QueryAsync<Guid>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();

        var hasMore = ids.Count > take;
        var pageIds = hasMore ? ids.Take(take).ToList() : ids;
        if (pageIds.Count == 0)
            return Results.Ok(new Page<CommentResponse>([], new PageInfo(null, false, take)));

        var comments = await db.Comments.AsNoTracking().Where(c => pageIds.Contains(c.Id)).ToListAsync(ct);
        var byId = comments.ToDictionary(c => c.Id);
        var authors = await LoadAuthorsAsync(db, m.GroupId, comments.Select(c => c.AuthorUser).Distinct().ToList(), ct);

        var data = pageIds
            .Where(byId.ContainsKey)
            .Select(id =>
            {
                var c = byId[id];
                return CommentMapping.ToResponse(c, authors.TryGetValue(c.AuthorUser, out var a) ? a : null);
            })
            .ToList();

        var last = byId[pageIds[^1]];
        var nextCursor = hasMore ? Cursor.Encode($"{last.CreatedAt:O}|{last.Id}") : null;
        return Results.Ok(new Page<CommentResponse>(data, new PageInfo(nextCursor, hasMore, take)));
    }

    // ---------- CREATE (§7.4) ----------

    public static async Task<IResult> CreateAsync(
        string expenseId,
        CreateCommentRequest request,
        HttpContext http,
        AppDbContext db,
        IChangeLogWriter changeLog,
        IClock clock,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        var groupId = m.GroupId;
        if (!Guid.TryParse(expenseId, out var eid))
            return Problems.NotFound();

        if (await RejectIfArchivedAsync(db, groupId, ct) is { } archived)
            return archived;

        if (CommentMapping.ValidateBody(request.Body) is { } bodyError)
            return bodyError;

        // D9 idempotency: a create replaying the same (group, clientId) is diffed, never re-applied. This
        // is evaluated BEFORE the EXT-DC8 expense-live check so a replay stays idempotent (200) even after
        // its expense was soft-deleted — only a genuinely NEW comment hits the 404 below.
        if (await FindByClientIdAsync(db, groupId, request.ClientId!, ct) is { } existing)
            return await ReplayAsync(db, http, existing, request.Body!, ct);

        // EXT-DC8: a new comment on a soft-deleted (or absent) expense → 404; existing comments survive.
        var expenseLive = await db.Expenses.AsNoTracking()
            .AnyAsync(e => e.Id == eid && e.GroupId == groupId && e.DeletedAt == null, ct);
        if (!expenseLive)
            return Problems.NotFound();

        var now = clock.UtcNow;
        var comment = new Comment
        {
            Id = Guid.CreateVersion7(),
            GroupId = groupId,
            ExpenseId = eid,
            AuthorUser = m.UserId, // server-derived from the token (EXT-DC7) — proven member via D6
            ClientId = request.ClientId,
            Body = request.Body!,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Comments.Add(comment);
        changeLog.Append(groupId, ChangeLogEntityTypes.Comment, comment.Id, isDelete: false);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent create won the (group, clientId) slot — diff against the winner (D9).
            db.ChangeTracker.Clear();
            if (await FindByClientIdAsync(db, groupId, request.ClientId!, ct) is not { } raced)
                throw;
            return await ReplayAsync(db, http, raced, request.Body!, ct);
        }

        var author = await LoadAuthorAsync(db, groupId, comment.AuthorUser, ct);
        SetETag(http, comment.Version);
        return Results.Json(CommentMapping.ToResponse(comment, author), statusCode: StatusCodes.Status201Created);
    }

    // ---------- PATCH (author-only edit, no time limit — EXT-DC5) ----------

    public static async Task<IResult> PatchAsync(
        string commentId,
        PatchCommentRequest request,
        HttpContext http,
        AppDbContext db,
        IChangeLogWriter changeLog,
        IClock clock,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        var groupId = m.GroupId;
        if (!Guid.TryParse(commentId, out var cid))
            return Problems.NotFound();

        if (await RejectIfArchivedAsync(db, groupId, ct) is { } archived)
            return archived;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var comment = await LockCommentAsync(db, groupId, cid, ct);
        if (comment is null || comment.DeletedAt is not null)
            return Problems.NotFound();

        // Author-only (EXT-DC5): authorize BEFORE concurrency so a non-author never sees the version/rep.
        // An admin may tombstone (moderate) but never rewrite someone else's words.
        if (comment.AuthorUser != m.UserId)
            return Problems.Forbidden(CommentProblemCodes.CommentEditForbidden);

        var ifMatch = ReadIfMatch(http);
        if (!ifMatch.Present)
            return Problems.PreconditionRequired();
        if (ifMatch.Version != comment.Version)
            return Problems.VersionConflict(CommentMapping.ToResponse(comment, await LoadAuthorAsync(db, groupId, comment.AuthorUser, ct)));

        if (CommentMapping.ValidateBody(request.Body) is { } bodyError)
            return bodyError;

        comment.Body = request.Body!;
        comment.Version += 1;          // "edited" is surfaced via version > 1 (EXT-DC5)
        comment.UpdatedAt = clock.UtcNow;
        changeLog.Append(groupId, ChangeLogEntityTypes.Comment, cid, isDelete: false);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var author = await LoadAuthorAsync(db, groupId, comment.AuthorUser, ct);
        SetETag(http, comment.Version);
        return Results.Ok(CommentMapping.ToResponse(comment, author));
    }

    // ---------- DELETE (author OR admin tombstone — soft-delete, EXT-DC5) ----------

    public static async Task<IResult> DeleteAsync(
        string commentId,
        HttpContext http,
        AppDbContext db,
        IChangeLogWriter changeLog,
        IClock clock,
        CancellationToken ct)
    {
        var m = http.GetMembership();
        var groupId = m.GroupId;
        if (!Guid.TryParse(commentId, out var cid))
            return Problems.NotFound();

        if (await RejectIfArchivedAsync(db, groupId, ct) is { } archived)
            return archived;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var comment = await LockCommentAsync(db, groupId, cid, ct);
        if (comment is null || comment.DeletedAt is not null)
            return Problems.NotFound(); // replayed delete finds the tombstone → 404 (outbox treats as success)

        // Author OR group admin/owner may tombstone (EXT-DC5); the caller is a proven member so 403 is not a leak.
        if (comment.AuthorUser != m.UserId && !AdminRoles.Contains(m.Role))
            return Problems.Forbidden(CommentProblemCodes.CommentDeleteForbidden);

        var ifMatch = ReadIfMatch(http);
        if (!ifMatch.Present)
            return Problems.PreconditionRequired();
        if (ifMatch.Version != comment.Version)
            return Problems.VersionConflict(CommentMapping.ToResponse(comment, await LoadAuthorAsync(db, groupId, comment.AuthorUser, ct)));

        comment.DeletedAt = clock.UtcNow;
        comment.Version += 1;
        changeLog.Append(groupId, ChangeLogEntityTypes.Comment, cid, isDelete: true);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        SetETag(http, comment.Version);
        return Results.NoContent();
    }

    // ---------- shared helpers ----------

    private static async Task<IResult> ReplayAsync(
        AppDbContext db, HttpContext http, Comment existing, string requestBody, CancellationToken ct)
    {
        // §3.4 idempotent replay: identical canonical body → 200 + existing; divergent → 409 client_id_conflict.
        if (!string.Equals(existing.Body, requestBody, StringComparison.Ordinal))
            return Problems.ClientIdConflict(existing.Id.ToString());

        var author = await LoadAuthorAsync(db, existing.GroupId, existing.AuthorUser, ct);
        SetETag(http, existing.Version);
        return Results.Ok(CommentMapping.ToResponse(existing, author));
    }

    private static async Task<IResult?> RejectIfArchivedAsync(AppDbContext db, Guid groupId, CancellationToken ct)
    {
        var archivedAt = await db.Groups.AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => g.ArchivedAt)
            .FirstOrDefaultAsync(ct);
        return archivedAt is not null
            ? Problems.Create(StatusCodes.Status409Conflict, ProblemCodes.GroupArchived,
                detail: "Unarchive the group to make changes.")
            : null;
    }

    private static async Task<Comment?> FindByClientIdAsync(AppDbContext db, Guid groupId, string clientId, CancellationToken ct)
        => await db.Comments.AsNoTracking().FirstOrDefaultAsync(c => c.GroupId == groupId && c.ClientId == clientId, ct);

    private static async Task<Comment?> LockCommentAsync(AppDbContext db, Guid groupId, Guid commentId, CancellationToken ct)
    {
        // FOR UPDATE serializes concurrent PATCH/DELETE on the comment (D8); the row lock is held to COMMIT.
        var rows = await db.Comments
            .FromSql($"SELECT * FROM comments WHERE id = {commentId} AND group_id = {groupId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    private static async Task<GroupMember?> LoadAuthorAsync(AppDbContext db, Guid groupId, Guid authorUser, CancellationToken ct)
    {
        var map = await LoadAuthorsAsync(db, groupId, [authorUser], ct);
        return map.TryGetValue(authorUser, out var member) ? member : null;
    }

    /// <summary>
    /// Resolve each author's <c>group_members</c> row in this group (EXT-DC1). A member who has left has a
    /// row with <c>user_id</c> nulled and <c>former_user_id</c> set (§3.8), so we match on either; the
    /// current membership row wins when both exist for one author.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, GroupMember>> LoadAuthorsAsync(
        AppDbContext db, Guid groupId, IReadOnlyList<Guid> authorUsers, CancellationToken ct)
    {
        if (authorUsers.Count == 0)
            return new Dictionary<Guid, GroupMember>();

        var rows = await db.GroupMembers.AsNoTracking()
            .Where(x => x.GroupId == groupId
                        && ((x.UserId != null && authorUsers.Contains(x.UserId.Value))
                            || (x.FormerUserId != null && authorUsers.Contains(x.FormerUserId.Value))))
            .ToListAsync(ct);

        var map = new Dictionary<Guid, GroupMember>();
        foreach (var row in rows)
        {
            if (row.UserId is { } uid && authorUsers.Contains(uid))
                map[uid] = row;                                   // current membership wins (overwrites a former match)
            else if (row.FormerUserId is { } fid && authorUsers.Contains(fid) && !map.ContainsKey(fid))
                map[fid] = row;                                   // former member — only if no current row resolved
        }

        return map;
    }

    private static (bool Present, int Version) ReadIfMatch(HttpContext http)
    {
        var raw = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return (false, 0);
        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
            value = value[2..];
        value = value.Trim().Trim('"');
        return (true, int.TryParse(value, out var version) ? version : -1); // garbage → -1 never matches → 412
    }

    private static void SetETag(HttpContext http, int version) => http.Response.Headers.ETag = $"\"{version}\"";

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static bool TryParseListCursor(string payload, out DateTimeOffset created, out Guid id)
    {
        created = default;
        id = default;
        var sep = payload.LastIndexOf('|');
        if (sep <= 0)
            return false;
        return DateTimeOffset.TryParse(
                   payload[..sep], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created)
               && Guid.TryParse(payload[(sep + 1)..], out id);
    }
}

using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Pagination;
using Paybitch.Api.Common.Validation;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Settlements;

/// <summary>
/// Settlements (§3.3, §3.6): list · record · void. Per-currency only (D5). All three are member-gated
/// (D6); create/void additionally reject on an archived group (§3.7). Void = soft delete guarded by
/// <c>If-Match</c> (D8). Every mutation appends a <c>settlement</c> change_log row in the same
/// DbContext transaction (§3.5.2).
/// </summary>
public sealed class SettlementsModule : IEndpointModule
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/groups/{groupId:guid}/settlements", ListAsync)
            .RequireGroupMembership()
            .WithName("ListSettlements")
            .WithSummary("List a group's settlements, newest first (cursor paginated).")
            .WithTags("Settlements");

        app.MapPost("/groups/{groupId:guid}/settlements", CreateAsync)
            .RequireGroupMembership()
            .WithValidation()
            .WithName("RecordSettlement")
            .WithSummary("Record a settlement (idempotent on clientId).")
            .WithTags("Settlements");

        app.MapDelete("/groups/{groupId:guid}/settlements/{settlementId:guid}", VoidAsync)
            .RequireGroupMembership()
            .WithName("VoidSettlement")
            .WithSummary("Void (soft-delete) a settlement; If-Match required.")
            .WithTags("Settlements");
    }

    // --- GET /groups/{g}/settlements ------------------------------------------------------------

    private static async Task<IResult> ListAsync(
        Guid groupId,
        AppDbContext db,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var hasCursor = false;
        DateOnly curDate = default;
        Guid curId = default;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!TryDecodeCursor(cursor, out curDate, out curId))
                return Problems.Validation(ProblemCodes.ValidationFailed, "cursor", "Malformed cursor.");
            hasCursor = true;
        }

        const string sql = """
            SELECT id            AS "Id",
                   group_id      AS "GroupId",
                   client_id     AS "ClientId",
                   from_member   AS "FromMember",
                   to_member     AS "ToMember",
                   amount_minor  AS "AmountMinor",
                   currency      AS "Currency",
                   method        AS "Method",
                   settled_on    AS "SettledOn",
                   notes         AS "Notes",
                   version       AS "Version",
                   created_by    AS "CreatedBy",
                   created_at    AS "CreatedAt"
            FROM settlements
            WHERE group_id = @GroupId
              AND deleted_at IS NULL
              AND (@HasCursor = FALSE
                   OR settled_on < @CurDate
                   OR (settled_on = @CurDate AND id < @CurId))
            ORDER BY settled_on DESC, id DESC
            LIMIT @Limit
            """;

        var connection = db.Database.GetDbConnection();
        var command = new CommandDefinition(
            sql,
            new
            {
                GroupId = groupId,
                HasCursor = hasCursor,
                CurDate = curDate,
                CurId = curId,
                Limit = pageSize + 1, // fetch one extra to detect the next page (§3.4)
            },
            cancellationToken: ct);

        var rows = (await connection.QueryAsync<SettlementRow>(command)).ToList();
        var fetched = rows.Select(ToResponse).ToList();

        var page = Page<SettlementResponse>.From(
            fetched,
            pageSize,
            r => Cursor.Encode($"{r.SettledOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}|{r.Id}"));

        return Results.Ok(page);
    }

    // --- POST /groups/{g}/settlements -----------------------------------------------------------

    private static async Task<IResult> CreateAsync(
        Guid groupId,
        CreateSettlementRequest request,
        HttpContext http,
        AppDbContext db,
        IChangeLogWriter changeLog,
        CancellationToken ct)
    {
        var membership = http.GetMembership();

        if (await IsArchivedAsync(db, groupId, ct))
            return Problems.Conflict(ProblemCodes.GroupArchived);

        // Validated canonical positive integer ⇒ parses; keep the boundary the single source of truth.
        var amount = long.Parse(request.Amount, NumberStyles.None, CultureInfo.InvariantCulture);

        // Idempotency (D9): a replay of an existing (groupId, clientId) is diffed on canonical fields.
        var existing = await db.Settlements.AsNoTracking()
            .FirstOrDefaultAsync(s => s.GroupId == groupId && s.ClientId == request.ClientId, ct);
        if (existing is not null)
            return ResolveIdempotent(http, existing, amount, request);

        // §3.8.3 write-side belt: lock the referenced member rows FOR UPDATE (canonical id order ⇒
        // no deadlock) BEFORE validating member_deleted, so a concurrent admin member-remove is
        // serialized — remove-first ⇒ this write revalidates to 422 member_deleted; write-first ⇒
        // remove recomputes buckets to 409 balance_not_zero. The tx also serializes duplicate creates.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var lockedMembers = await LockMembersAsync(db, tx, groupId, request.FromMember, request.ToMember, ct);

        // from/to must both be live members OF THIS GROUP (§3.8.3 — no newly referencing a removed
        // member). self_settlement is already rejected at the boundary.
        if (ValidateMember(request.FromMember, "fromMember", lockedMembers) is { } fromProblem)
            return fromProblem;
        if (ValidateMember(request.ToMember, "toMember", lockedMembers) is { } toProblem)
            return toProblem;

        var entity = new Settlement
        {
            GroupId = groupId,
            ClientId = request.ClientId,
            FromMember = request.FromMember,
            ToMember = request.ToMember,
            AmountMinor = amount,
            Currency = request.Currency,
            Method = request.Method,
            SettledOn = request.SettledOn,
            Notes = request.Notes,
            Version = 1,
            CreatedBy = membership.UserId,
        };

        db.Settlements.Add(entity);
        changeLog.Append(groupId, ChangeLogEntityTypes.Settlement, entity.Id, isDelete: false);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent create with the same (groupId, clientId) won the UNIQUE race — reconcile.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var raced = await db.Settlements.AsNoTracking()
                .FirstOrDefaultAsync(s => s.GroupId == groupId && s.ClientId == request.ClientId, ct);
            if (raced is null)
                throw;
            return ResolveIdempotent(http, raced, amount, request);
        }

        SetETag(http, entity.Version);
        return Results.Created($"/v1/groups/{groupId}/settlements/{entity.Id}", ToResponse(entity));
    }

    /// <summary>
    /// Lock the settlement's referenced member rows <c>FOR UPDATE</c> in canonical <c>id</c> order on
    /// the caller's transaction (§3.8.3), returning each locked row's live/deleted state. A referenced
    /// id absent from the result is simply not a member of the group.
    /// </summary>
    private static async Task<IReadOnlyList<MemberSlot>> LockMembersAsync(
        AppDbContext db,
        IDbContextTransaction tx,
        Guid groupId,
        Guid fromMember,
        Guid toMember,
        CancellationToken ct)
    {
        const string sql = """
            SELECT id AS "Id", (deleted_at IS NOT NULL) AS "Deleted"
            FROM group_members
            WHERE group_id = @GroupId AND id = ANY(@Ids)
            ORDER BY id
            FOR UPDATE
            """;

        var ids = fromMember == toMember ? new[] { fromMember } : new[] { fromMember, toMember };
        var connection = db.Database.GetDbConnection();
        var command = new CommandDefinition(
            sql,
            new { GroupId = groupId, Ids = ids },
            transaction: tx.GetDbTransaction(),
            cancellationToken: ct);

        return (await connection.QueryAsync<MemberSlot>(command)).ToList();
    }

    /// <summary>Idempotency verdict for a replayed create: identical ⇒ 200 + record; divergent ⇒ 409.</summary>
    private static IResult ResolveIdempotent(
        HttpContext http,
        Settlement existing,
        long amount,
        CreateSettlementRequest request)
    {
        if (CanonicalMatch(existing, amount, request))
        {
            SetETag(http, existing.Version);
            return Results.Ok(ToResponse(existing));
        }

        return Problems.ClientIdConflict(existing.Id.ToString());
    }

    private static bool CanonicalMatch(Settlement e, long amount, CreateSettlementRequest r) =>
        e.FromMember == r.FromMember
        && e.ToMember == r.ToMember
        && e.AmountMinor == amount
        && string.Equals(e.Currency, r.Currency, StringComparison.Ordinal)
        && string.Equals(Normalize(e.Method), Normalize(r.Method), StringComparison.Ordinal)
        && e.SettledOn == r.SettledOn
        && string.Equals(Normalize(e.Notes), Normalize(r.Notes), StringComparison.Ordinal);

    // --- DELETE /groups/{g}/settlements/{s} (void) ----------------------------------------------

    private static async Task<IResult> VoidAsync(
        Guid groupId,
        Guid settlementId,
        HttpContext http,
        AppDbContext db,
        IChangeLogWriter changeLog,
        IClock clock,
        CancellationToken ct)
    {
        var membership = http.GetMembership();

        if (await IsArchivedAsync(db, groupId, ct))
            return Problems.Conflict(ProblemCodes.GroupArchived);

        var settlement = await db.Settlements
            .FirstOrDefaultAsync(s => s.Id == settlementId && s.GroupId == groupId, ct);
        if (settlement is null)
            return Problems.NotFound();

        // Void rights (§3.6): creator, either counterparty (caller's own member slot is from/to), or admin+.
        var isCounterparty = membership.MemberId == settlement.FromMember
                             || membership.MemberId == settlement.ToMember;
        var isCreator = settlement.CreatedBy is { } createdBy && createdBy == membership.UserId;
        var isAdmin = membership.Role is "owner" or "admin";
        if (!isCreator && !isCounterparty && !isAdmin)
            return Problems.Forbidden(ProblemCodes.SettlementVoidForbidden);

        // If-Match required (D8): absent ⇒ 428; stale/malformed ⇒ 412 with the current representation.
        var ifMatch = http.Request.Headers.IfMatch;
        if (ifMatch.Count == 0 || string.IsNullOrWhiteSpace(ifMatch[0]))
            return Problems.PreconditionRequired();
        var requestedVersion = ParseVersion(ifMatch[0]!);

        if (settlement.DeletedAt is not null)
        {
            // Already voided (no un-void, v1). A retry carrying the post-void version is success;
            // a stale (pre-void) version gets 412 with the current representation (deleted: true).
            return requestedVersion == settlement.Version
                ? Results.NoContent()
                : Problems.VersionConflict(ToResponse(settlement));
        }

        if (requestedVersion != settlement.Version)
            return Problems.VersionConflict(ToResponse(settlement));

        settlement.DeletedAt = clock.UtcNow;
        settlement.Version += 1; // bump once per soft-delete (§3.2b); UpdatedAt auto-stamped
        changeLog.Append(groupId, ChangeLogEntityTypes.Settlement, settlement.Id, isDelete: true);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // --- helpers --------------------------------------------------------------------------------

    private static async Task<bool> IsArchivedAsync(AppDbContext db, Guid groupId, CancellationToken ct) =>
        await db.Groups
            .Where(g => g.Id == groupId)
            .Select(g => g.ArchivedAt)
            .FirstOrDefaultAsync(ct) is not null;

    /// <summary>422 if <paramref name="memberId"/> is not a live member of the group; else <c>null</c>.</summary>
    private static IResult? ValidateMember(Guid memberId, string field, IReadOnlyList<MemberSlot> slots)
    {
        var slot = slots.FirstOrDefault(m => m.Id == memberId);
        if (slot is null)
            return Problems.Validation(ProblemCodes.ValidationFailed, field, "Member is not part of this group.");
        if (slot.Deleted)
            return Problems.Validation(ProblemCodes.MemberDeleted, field, "Member has been removed.");
        return null;
    }

    private static bool TryDecodeCursor(string cursor, out DateOnly date, out Guid id)
    {
        date = default;
        id = default;
        if (!Cursor.TryDecode(cursor, out var payload))
            return false;

        var parts = payload.Split('|');
        return parts.Length == 2
            && DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && Guid.TryParse(parts[1], out id);
    }

    private static int? ParseVersion(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
            value = value[2..].Trim();
        value = value.Trim('"');
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static void SetETag(HttpContext http, int version) =>
        http.Response.Headers.ETag = $"\"{version}\"";

    private static string? Normalize(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static SettlementResponse ToResponse(Settlement s) => new(
        s.Id.ToString(),
        s.GroupId.ToString(),
        s.ClientId,
        s.FromMember.ToString(),
        s.ToMember.ToString(),
        s.AmountMinor.ToString(CultureInfo.InvariantCulture),
        s.Currency,
        s.Method,
        s.SettledOn,
        s.Notes,
        s.Version,
        s.CreatedBy?.ToString(),
        s.CreatedAt,
        s.DeletedAt is not null);

    private static SettlementResponse ToResponse(SettlementRow r) => new(
        r.Id.ToString(),
        r.GroupId.ToString(),
        r.ClientId,
        r.FromMember.ToString(),
        r.ToMember.ToString(),
        r.AmountMinor.ToString(CultureInfo.InvariantCulture),
        r.Currency,
        r.Method,
        r.SettledOn,
        r.Notes,
        r.Version,
        r.CreatedBy?.ToString(),
        new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)),
        false); // list returns live rows only

    /// <summary>Live/soft-deleted state of a referenced group member, scoped to the group.</summary>
    private sealed record MemberSlot(Guid Id, bool Deleted);

    /// <summary>Dapper projection of a settlements row (aliased to PascalCase in the SQL).</summary>
    private sealed record SettlementRow(
        Guid Id,
        Guid GroupId,
        string? ClientId,
        Guid FromMember,
        Guid ToMember,
        long AmountMinor,
        string Currency,
        string? Method,
        DateOnly SettledOn,
        string? Notes,
        int Version,
        Guid? CreatedBy,
        DateTime CreatedAt);
}

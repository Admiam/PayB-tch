using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Common.Pagination;
using Paybitch.Api.Common.Validation;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Groups.Groups;

/// <summary>
/// Group aggregate endpoints (§3.1, §3.7). <c>GET /groups</c> is the home screen — one call renders it
/// (header + the caller's own per-currency nets, §3.12/G26). Archive is the <c>DELETE</c> verb
/// (reversible, idempotent, If-Match-exempt); unarchive rides <c>PATCH</c> (§3.7).
/// </summary>
public sealed class GroupsModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/groups", ListGroups)
            .WithName("ListGroups")
            .WithSummary("List the caller's groups with their own per-currency balance.")
            .WithTags("Groups");

        app.MapPost("/groups", CreateGroup)
            .WithValidation()
            .WithName("CreateGroup")
            .WithSummary("Create a group; the caller becomes its owner.")
            .WithTags("Groups");

        app.MapGet("/groups/{groupId}", GetGroup)
            .RequireGroupMembership()
            .WithName("GetGroup")
            .WithSummary("Group detail (members-only).")
            .WithTags("Groups");

        app.MapPatch("/groups/{groupId}", PatchGroup)
            .RequireGroupAdmin()
            .WithValidation()
            .WithName("PatchGroup")
            .WithSummary("Rename or unarchive a group (admin, If-Match).")
            .WithTags("Groups");

        app.MapDelete("/groups/{groupId}", ArchiveGroup)
            .RequireGroupAdmin()
            .WithName("ArchiveGroup")
            .WithSummary("Archive a group (admin, reversible, idempotent).")
            .WithTags("Groups");
    }

    // --- GET /groups ---
    private static async Task<IResult> ListGroups(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.GetUserId();
        var limit = ListQuery.ClampLimit(http.Request.Query["limit"]);
        var cursorRaw = http.Request.Query["cursor"].ToString();

        DateTimeOffset cursorCreated = default;
        Guid cursorId = default;
        var hasCursor = false;
        if (!string.IsNullOrEmpty(cursorRaw))
        {
            if (!Cursor.TryDecode(cursorRaw, out var payload) || !TryParseGroupCursor(payload, out cursorCreated, out cursorId))
                return Problems.ValidationFailed([new ProblemError("cursor", "Malformed cursor.")]);
            hasCursor = true;
        }

        var limitPlusOne = limit + 1;
        var fetched = hasCursor
            ? await db.Groups.FromSql(
                    $"""
                     SELECT g.* FROM groups g
                     WHERE g.deleted_at IS NULL
                       AND EXISTS (SELECT 1 FROM group_members gm
                                   WHERE gm.group_id = g.id AND gm.user_id = {userId} AND gm.deleted_at IS NULL)
                       AND (g.created_at, g.id) < ({cursorCreated}, {cursorId})
                     ORDER BY g.created_at DESC, g.id DESC
                     LIMIT {limitPlusOne}
                     """)
                .AsNoTracking().ToListAsync(ct)
            : await db.Groups.FromSql(
                    $"""
                     SELECT g.* FROM groups g
                     WHERE g.deleted_at IS NULL
                       AND EXISTS (SELECT 1 FROM group_members gm
                                   WHERE gm.group_id = g.id AND gm.user_id = {userId} AND gm.deleted_at IS NULL)
                     ORDER BY g.created_at DESC, g.id DESC
                     LIMIT {limitPlusOne}
                     """)
                .AsNoTracking().ToListAsync(ct);

        var hasMore = fetched.Count > limit;
        var pageGroups = hasMore ? fetched.Take(limit).ToList() : fetched;
        var pageIds = pageGroups.Select(g => g.Id).ToList();

        var counts = await db.GroupMembers
            .Where(m => pageIds.Contains(m.GroupId) && m.DeletedAt == null)
            .GroupBy(m => m.GroupId)
            .Select(x => new { GroupId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count, ct);

        var balances = await CallerBalancesAsync(db, userId, pageIds, ct);

        var items = pageGroups.Select(g => new GroupListItemResponse(
            g.Id, g.Name, g.IconSymbol, g.DefaultCurrency,
            counts.GetValueOrDefault(g.Id), g.ArchivedAt is not null, g.Version,
            balances.GetValueOrDefault(g.Id) ?? [])).ToList();

        string? nextCursor = null;
        if (hasMore && pageGroups.Count > 0)
        {
            var last = pageGroups[^1];
            nextCursor = Cursor.Encode($"{last.CreatedAt.UtcTicks}|{last.Id}");
        }

        return Results.Ok(new Page<GroupListItemResponse>(items, new PageInfo(nextCursor, hasMore, limit)));
    }

    /// <summary>The §G26 aggregate: caller's per-currency nets for the page's groups, zero buckets omitted.</summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<CurrencyNetResponse>>> CallerBalancesAsync(
        AppDbContext db, Guid userId, IReadOnlyList<Guid> pageIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<CurrencyNetResponse>>();
        if (pageIds.Count == 0)
            return result;

        var callerMembers = await db.GroupMembers
            .Where(m => m.UserId == userId && m.DeletedAt == null && pageIds.Contains(m.GroupId))
            .Select(m => new { m.Id, m.GroupId })
            .ToListAsync(ct);

        var memberToGroup = callerMembers.ToDictionary(m => m.Id, m => m.GroupId);
        var nets = await LedgerBalance.ComputeNetsAsync(db, memberToGroup.Keys.ToList(), ct);

        foreach (var (memberId, byCurrency) in nets)
        {
            var groupId = memberToGroup[memberId];
            result[groupId] = byCurrency
                .Where(kv => kv.Value != 0)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CurrencyNetResponse(kv.Key, kv.Value.ToString(CultureInfo.InvariantCulture)))
                .ToList();
        }

        return result;
    }

    // --- POST /groups ---
    private static async Task<IResult> CreateGroup(
        CreateGroupRequest req, HttpContext http, AppDbContext db,
        IChangeLogWriter changeLog, IClock clock, IOptions<OperationalConstants> opsOpt, CancellationToken ct)
    {
        var ops = opsOpt.Value;
        var userId = http.GetUserId();

        var groupCount = await MembershipOps.CountActiveMembershipsAsync(db, userId, ct);
        if (groupCount >= ops.GroupsPerUser)
            return Problems.Conflict(ProblemCodes.LimitExceeded);

        var displayName = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct);

        var group = new Group
        {
            Name = req.Name.Trim(),
            DefaultCurrency = req.DefaultCurrency,
            IconSymbol = req.IconSymbol,
            CreatedBy = userId,
            CreatedAt = clock.UtcNow,
            Version = 1,
        };
        db.Groups.Add(group);

        var member = new GroupMember
        {
            GroupId = group.Id,
            UserId = userId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Me" : displayName!,
            Role = MembershipOps.RoleOwner,
            CreatedAt = clock.UtcNow,
            Version = 1,
        };
        db.GroupMembers.Add(member);

        changeLog.Append(group.Id, ChangeLogEntityTypes.Group, group.Id, isDelete: false);
        changeLog.Append(group.Id, ChangeLogEntityTypes.Member, member.Id, isDelete: false);
        changeLog.AppendAccess(group.Id, userId, isRevoke: false);

        await db.SaveChangesAsync(ct);

        ConcurrencyHeaders.SetETag(http.Response, group.Version);
        return Results.Created($"/v1/groups/{group.Id}", GroupResponse.From(group, memberCount: 1));
    }

    // --- GET /groups/{groupId} ---
    private static async Task<IResult> GetGroup(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var group = await db.Groups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == membership.GroupId && g.DeletedAt == null, ct);
        if (group is null)
            return Problems.NotFound();

        var count = await MembershipOps.CountActiveMembersAsync(db, group.Id, ct);
        ConcurrencyHeaders.SetETag(http.Response, group.Version);
        return Results.Ok(GroupResponse.From(group, count));
    }

    // --- PATCH /groups/{groupId} ---
    private static async Task<IResult> PatchGroup(
        PatchGroupRequest req, HttpContext http, AppDbContext db,
        IChangeLogWriter changeLog, CancellationToken ct)
    {
        var membership = http.GetMembership();

        if (!ConcurrencyHeaders.TryReadIfMatch(http.Request, out var ifMatch))
            return Problems.PreconditionRequired();

        var group = await db.Groups
            .FirstOrDefaultAsync(g => g.Id == membership.GroupId && g.DeletedAt == null, ct);
        if (group is null)
            return Problems.NotFound();

        var count = await MembershipOps.CountActiveMembersAsync(db, group.Id, ct);

        if (group.Version != ifMatch)
            return Problems.VersionConflict(GroupResponse.From(group, count));

        var isArchived = group.ArchivedAt is not null;
        var wantsUnarchive = req.Archived == false;

        // Archived groups accept only the unarchive write (§3.7).
        if (isArchived && !wantsUnarchive)
            return GroupErrors.GroupArchived();

        var changed = false;
        if (req.Name is not null)
        {
            var trimmed = req.Name.Trim();
            if (trimmed != group.Name) { group.Name = trimmed; changed = true; }
        }
        if (wantsUnarchive && isArchived) { group.ArchivedAt = null; changed = true; }

        if (!changed)
        {
            ConcurrencyHeaders.SetETag(http.Response, group.Version);
            return Results.Ok(GroupResponse.From(group, count));
        }

        group.Version += 1;
        changeLog.Append(group.Id, ChangeLogEntityTypes.Group, group.Id, isDelete: false);
        await db.SaveChangesAsync(ct);

        ConcurrencyHeaders.SetETag(http.Response, group.Version);
        return Results.Ok(GroupResponse.From(group, count));
    }

    // --- DELETE /groups/{groupId} (archive) ---
    private static async Task<IResult> ArchiveGroup(
        HttpContext http, AppDbContext db, IChangeLogWriter changeLog, IClock clock, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var group = await db.Groups
            .FirstOrDefaultAsync(g => g.Id == membership.GroupId && g.DeletedAt == null, ct);
        if (group is null)
            return Problems.NotFound();

        // Idempotent: archiving an already-archived group is a no-op 204 (§3.7).
        if (group.ArchivedAt is not null)
            return Results.NoContent();

        group.ArchivedAt = clock.UtcNow;
        group.Version += 1;
        changeLog.Append(group.Id, ChangeLogEntityTypes.Group, group.Id, isDelete: false);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static bool TryParseGroupCursor(string payload, out DateTimeOffset created, out Guid id)
    {
        created = default;
        id = default;
        var sep = payload.IndexOf('|');
        if (sep <= 0 || sep == payload.Length - 1)
            return false;
        if (!long.TryParse(payload.AsSpan(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            return false;
        if (!Guid.TryParse(payload.AsSpan(sep + 1), out id))
            return false;
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            return false;
        created = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
}

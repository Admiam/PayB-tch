using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Comments;
using Paybitch.Api.Features.Expenses;
using Paybitch.Api.Features.Groups.Groups;
using Paybitch.Api.Features.Groups.Members;
using Paybitch.Api.Features.Settlements;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Sync;

/// <summary>
/// Turns the ordered change rows (§3.5.3) into wire <see cref="SyncChange"/>s. Non-delete rows are
/// hydrated to the entity's CURRENT representation — reusing the canonical REST DTOs so <c>/sync</c>
/// and REST emit identical shapes (§3.5.1: the expense representation IS the §3.2 one). Batched,
/// no-tracking EF reads include soft-deleted rows (the tombstone rides a separate is_delete row).
/// Delete rows and <c>access</c> rows carry <c>data: null</c> (§3.5.4). Order is preserved.
/// </summary>
internal sealed class SyncHydrator(AppDbContext db)
{
    public async Task<IReadOnlyList<SyncChange>> HydrateAsync(IReadOnlyList<RawChange> rows, CancellationToken ct)
    {
        var groups = await LoadGroupsAsync(IdsToHydrate(rows, ChangeLogEntityTypes.Group), ct);
        var members = await LoadMembersAsync(IdsToHydrate(rows, ChangeLogEntityTypes.Member), ct);
        var expenses = await LoadExpensesAsync(IdsToHydrate(rows, ChangeLogEntityTypes.Expense), ct);
        var settlements = await LoadSettlementsAsync(IdsToHydrate(rows, ChangeLogEntityTypes.Settlement), ct);
        var categories = await LoadCategoriesAsync(IdsToHydrate(rows, ChangeLogEntityTypes.Category), ct);
        var comments = await LoadCommentsAsync(IdsToHydrate(rows, ChangeLogEntityTypes.Comment), ct);

        var changes = new List<SyncChange>(rows.Count);
        foreach (var row in rows)
        {
            // access rows are addressed to the caller and describe a whole-group grant/revoke; the wire
            // id/groupId are the group, data is always null (§3.5.4).
            if (row.EntityType == ChangeLogEntityTypes.Access)
            {
                var gid = row.GroupId.ToString();
                changes.Add(new SyncChange(ChangeLogEntityTypes.Access, gid, gid, row.IsDelete, null));
                continue;
            }

            object? data = row.IsDelete
                ? null
                : row.EntityType switch
                {
                    ChangeLogEntityTypes.Group => Lookup(groups, row.EntityId),
                    ChangeLogEntityTypes.Member => Lookup(members, row.EntityId),
                    ChangeLogEntityTypes.Expense => Lookup(expenses, row.EntityId),
                    ChangeLogEntityTypes.Settlement => Lookup(settlements, row.EntityId),
                    ChangeLogEntityTypes.Category => Lookup(categories, row.EntityId),
                    ChangeLogEntityTypes.Comment => Lookup(comments, row.EntityId),
                    _ => null,
                };

            changes.Add(new SyncChange(
                row.EntityType,
                row.EntityId.ToString(),
                row.GroupId.ToString(),
                row.IsDelete,
                data));
        }

        return changes;
    }

    private static IReadOnlyList<Guid> IdsToHydrate(IReadOnlyList<RawChange> rows, string entityType) =>
        rows.Where(r => r.EntityType == entityType && !r.IsDelete)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();

    private static object? Lookup<T>(IReadOnlyDictionary<Guid, T> map, Guid id) where T : class =>
        map.TryGetValue(id, out var value) ? value : null;

    private async Task<IReadOnlyDictionary<Guid, GroupResponse>> LoadGroupsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return EmptyMap<GroupResponse>();

        var list = await db.Groups.AsNoTracking().Where(g => ids.Contains(g.Id)).ToListAsync(ct);

        // memberCount is part of the canonical group representation (§3.1) — one grouped count, no N+1.
        var counts = (await db.GroupMembers.AsNoTracking()
                .Where(m => ids.Contains(m.GroupId) && m.DeletedAt == null)
                .GroupBy(m => m.GroupId)
                .Select(g => new { GroupId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.GroupId, x => x.Count);

        return list.ToDictionary(
            g => g.Id,
            g => GroupResponse.From(g, counts.TryGetValue(g.Id, out var c) ? c : 0));
    }

    private async Task<IReadOnlyDictionary<Guid, MemberResponse>> LoadMembersAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return EmptyMap<MemberResponse>();
        var list = await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct);
        return list.ToDictionary(m => m.Id, MemberResponse.From);
    }

    private async Task<IReadOnlyDictionary<Guid, ExpenseResponse>> LoadExpensesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return EmptyMap<ExpenseResponse>();

        var list = await db.Expenses.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        var splits = await db.ExpenseSplits.AsNoTracking().Where(s => ids.Contains(s.ExpenseId)).ToListAsync(ct);
        var splitsByExpense = splits
            .GroupBy(s => s.ExpenseId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExpenseSplit>)g.ToList());

        return list.ToDictionary(
            e => e.Id,
            e => ExpenseMapping.ToResponse(
                e,
                splitsByExpense.TryGetValue(e.Id, out var s) ? s : Array.Empty<ExpenseSplit>()));
    }

    private async Task<IReadOnlyDictionary<Guid, SettlementResponse>> LoadSettlementsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return EmptyMap<SettlementResponse>();
        var list = await db.Settlements.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        return list.ToDictionary(s => s.Id, s => new SettlementResponse(
            s.Id.ToString(),
            s.GroupId.ToString(),
            s.ClientId,
            s.FromMember.ToString(),
            s.ToMember.ToString(),
            Minor(s.AmountMinor),
            s.Currency,
            s.Method,
            s.SettledOn,
            s.Notes,
            s.Version,
            s.CreatedBy?.ToString(),
            s.CreatedAt,
            s.DeletedAt is not null));
    }

    private async Task<IReadOnlyDictionary<Guid, SyncCategoryData>> LoadCategoriesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return EmptyMap<SyncCategoryData>();
        var list = await db.Categories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        return list.ToDictionary(c => c.Id, c => new SyncCategoryData(
            c.Id.ToString(),
            c.GroupId?.ToString() ?? string.Empty,
            c.Name,
            c.IconSymbol,
            c.UpdatedAt,
            c.DeletedAt is not null));
    }

    private async Task<IReadOnlyDictionary<Guid, CommentResponse>> LoadCommentsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return EmptyMap<CommentResponse>();

        var list = await db.Comments.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);

        // Resolve each comment's author member row (EXT-DC1): the row in the SAME group whose user_id — or,
        // once the author has left or been anonymized, former_user_id — is the comment's author_user. Batched
        // over the distinct (group, user) space so hydration stays N+1-free like the other entity loaders.
        var groupIds = list.Select(c => c.GroupId).Distinct().ToList();
        var authorUsers = list.Select(c => c.AuthorUser).Distinct().ToList();

        var authorRows = await db.GroupMembers.AsNoTracking()
            .Where(m => groupIds.Contains(m.GroupId)
                        && ((m.UserId != null && authorUsers.Contains(m.UserId.Value))
                            || (m.FormerUserId != null && authorUsers.Contains(m.FormerUserId.Value))))
            .ToListAsync(ct);

        var authorByGroupUser = new Dictionary<(Guid GroupId, Guid UserId), GroupMember>();
        foreach (var m in authorRows)
        {
            // A live link wins over a former-user row for the same (group, user) key.
            if (m.UserId is { } uid)
                authorByGroupUser[(m.GroupId, uid)] = m;
            else if (m.FormerUserId is { } fuid)
                authorByGroupUser.TryAdd((m.GroupId, fuid), m);
        }

        return list.ToDictionary(
            c => c.Id,
            c => CommentMapping.ToResponse(
                c,
                authorByGroupUser.TryGetValue((c.GroupId, c.AuthorUser), out var author) ? author : null));
    }

    private static string Minor(long minor) => minor.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<Guid, T> EmptyMap<T>() => new Dictionary<Guid, T>();
}

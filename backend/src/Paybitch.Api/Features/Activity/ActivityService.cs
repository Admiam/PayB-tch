using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Pagination;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Activity;

/// <summary>
/// Reads the §3.10 activity feed for a group: newest first (<c>created_at DESC, id DESC</c>; uuidv7 is
/// time-ordered), keyset-paginated (§3.4). The page is fetched with Dapper (native uuid tuple keyset),
/// then <c>actor</c> and <c>target</c> are hydrated from the LIVE rows — amounts are never stored in
/// the log, so they are joined at read time.
/// </summary>
internal sealed class ActivityService(AppDbContext db)
{
    // @hasCursor keeps the SQL/param shape constant whether or not a cursor is supplied.
    private const string PageSql = """
        SELECT id AS Id, actor_user AS ActorUser, verb AS Verb, target_type AS TargetType,
               target_id AS TargetId, metadata::text AS Metadata, created_at AS CreatedAt
        FROM activity_log
        WHERE group_id = @groupId
          AND (@hasCursor = FALSE OR (created_at, id) < (@cursorAt, @cursorId))
        ORDER BY created_at DESC, id DESC
        LIMIT @take;
        """;

    public async Task<Page<ActivityItem>> ReadAsync(Guid groupId, string? cursor, int limit, CancellationToken ct)
    {
        var rows = await ReadRowsAsync(groupId, cursor, limit, ct);
        var items = await HydrateAsync(rows, ct);
        return Page<ActivityItem>.From(items, limit, CursorFor);
    }

    private static string CursorFor(ActivityItem item) =>
        Cursor.Encode($"{item.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{item.Id}");

    private async Task<IReadOnlyList<ActivityRow>> ReadRowsAsync(Guid groupId, string? cursor, int limit, CancellationToken ct)
    {
        var hasCursor = TryDecodeCursor(cursor, out var cursorAt, out var cursorId);

        var conn = db.Database.GetDbConnection();
        var rows = await conn.QueryAsync<ActivityRow>(new CommandDefinition(
            PageSql,
            new { groupId, hasCursor, cursorAt, cursorId, take = limit + 1 },
            cancellationToken: ct));
        return rows.ToList();
    }

    private async Task<IReadOnlyList<ActivityItem>> HydrateAsync(IReadOnlyList<ActivityRow> rows, CancellationToken ct)
    {
        var actors = await LoadActorsAsync(rows, ct);
        var expenseTargets = await LoadExpenseTargetsAsync(TargetIds(rows, ActivityTargetTypes.Expense), ct);
        var settlementTargets = await LoadSettlementTargetsAsync(TargetIds(rows, ActivityTargetTypes.Settlement), ct);
        var memberTargets = await LoadMemberTargetsAsync(TargetIds(rows, ActivityTargetTypes.Member), ct);
        var groupTargets = await LoadGroupTargetsAsync(TargetIds(rows, ActivityTargetTypes.Group), ct);

        var items = new List<ActivityItem>(rows.Count);
        foreach (var row in rows)
        {
            var actor = row.ActorUser is { } uid && actors.TryGetValue(uid, out var a) ? a : null;

            var target = row.TargetId is not { } tid
                ? null
                : row.TargetType switch
                {
                    ActivityTargetTypes.Expense => Lookup(expenseTargets, tid),
                    ActivityTargetTypes.Settlement => Lookup(settlementTargets, tid),
                    ActivityTargetTypes.Member => Lookup(memberTargets, tid),
                    ActivityTargetTypes.Group => Lookup(groupTargets, tid),
                    _ => null, // invite (and any future type) has no live display row → generic line
                };

            items.Add(new ActivityItem(
                row.Id.ToString(),
                row.Verb,
                actor,
                row.TargetType,
                row.TargetId?.ToString(),
                ParseMetadata(row.Metadata),
                target,
                new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc))));
        }

        return items;
    }

    private async Task<IReadOnlyDictionary<Guid, ActorInfo>> LoadActorsAsync(IReadOnlyList<ActivityRow> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.ActorUser is not null).Select(r => r.ActorUser!.Value).Distinct().ToList();
        if (ids.Count == 0) return Empty<ActorInfo>();

        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.DeletedAt })
            .ToListAsync(ct);

        // An anonymized user has actor_user cleared (D7), so it never reaches here; a soft-deleted user
        // that slipped through hydrates to null (generic line), matching §3.10.
        return users.Where(u => u.DeletedAt is null)
            .ToDictionary(u => u.Id, u => new ActorInfo(u.Id.ToString(), u.DisplayName));
    }

    private async Task<IReadOnlyDictionary<Guid, TargetInfo>> LoadExpenseTargetsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Empty<TargetInfo>();
        var list = await db.Expenses.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.Title, e.AmountMinor, e.Currency, e.DeletedAt })
            .ToListAsync(ct);
        return list.ToDictionary(e => e.Id, e => new TargetInfo(
            Title: e.Title, Amount: Minor(e.AmountMinor), Currency: e.Currency, Deleted: e.DeletedAt is not null));
    }

    private async Task<IReadOnlyDictionary<Guid, TargetInfo>> LoadSettlementTargetsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Empty<TargetInfo>();
        var list = await db.Settlements.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.AmountMinor, s.Currency, s.DeletedAt })
            .ToListAsync(ct);
        return list.ToDictionary(s => s.Id, s => new TargetInfo(
            Amount: Minor(s.AmountMinor), Currency: s.Currency, Deleted: s.DeletedAt is not null));
    }

    private async Task<IReadOnlyDictionary<Guid, TargetInfo>> LoadMemberTargetsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Empty<TargetInfo>();
        var list = await db.GroupMembers.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new { m.Id, m.DisplayName, m.DeletedAt })
            .ToListAsync(ct);
        return list.ToDictionary(m => m.Id, m => new TargetInfo(
            DisplayName: m.DisplayName, Deleted: m.DeletedAt is not null));
    }

    private async Task<IReadOnlyDictionary<Guid, TargetInfo>> LoadGroupTargetsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Empty<TargetInfo>();
        var list = await db.Groups.AsNoTracking()
            .Where(g => ids.Contains(g.Id))
            .Select(g => new { g.Id, g.Name, g.ArchivedAt, g.DeletedAt })
            .ToListAsync(ct);
        return list.ToDictionary(g => g.Id, g => new TargetInfo(
            Name: g.Name, Archived: g.ArchivedAt is not null, Deleted: g.DeletedAt is not null));
    }

    private static IReadOnlyList<Guid> TargetIds(IReadOnlyList<ActivityRow> rows, string targetType) =>
        rows.Where(r => r.TargetType == targetType && r.TargetId is not null)
            .Select(r => r.TargetId!.Value)
            .Distinct()
            .ToList();

    private static TargetInfo? Lookup(IReadOnlyDictionary<Guid, TargetInfo> map, Guid id) =>
        map.TryGetValue(id, out var value) ? value : null;

    private static JsonNode? ParseMetadata(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return null;
        try
        {
            return JsonNode.Parse(metadata);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool TryDecodeCursor(string? cursor, out DateTime at, out Guid id)
    {
        at = DateTime.UnixEpoch; // Kind=Utc; unused when hasCursor is false
        id = Guid.Empty;

        if (string.IsNullOrEmpty(cursor) || !Cursor.TryDecode(cursor, out var payload))
            return false;

        var sep = payload.IndexOf('|');
        if (sep <= 0 || sep == payload.Length - 1)
            return false;

        if (!long.TryParse(payload.AsSpan(0, sep), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < 0 || ticks > DateTime.MaxValue.Ticks)
            return false;

        if (!Guid.TryParse(payload.AsSpan(sep + 1), out id))
            return false;

        at = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static string Minor(long minor) => minor.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<Guid, T> Empty<T>() => new Dictionary<Guid, T>();

    /// <summary>Dapper projection of an <c>activity_log</c> row. <c>CreatedAt</c> is read as
    /// <see cref="DateTime"/> — Npgsql's ADO-level default for <c>timestamptz</c> — and lifted to a
    /// UTC <see cref="DateTimeOffset"/> at the wire boundary.</summary>
    private sealed record ActivityRow(
        Guid Id,
        Guid? ActorUser,
        string Verb,
        string TargetType,
        Guid? TargetId,
        string? Metadata,
        DateTime CreatedAt);
}

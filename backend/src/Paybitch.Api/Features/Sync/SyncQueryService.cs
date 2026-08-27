using Dapper;
using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Sync;

/// <summary>
/// The §3.5.3 / §3.5.5 change-log read. Uses Dapper on the DbContext's connection for the shaped,
/// no-tracking delta page. The page SELECT and the 410 watermark check run as ONE statement (a
/// <c>change_log_watermark</c> row LEFT JOIN LATERAL the membership-filtered page) so a prune
/// committing mid-request can't slip rows past an in-flight page (§3.5.5 "one snapshot").
/// </summary>
internal sealed class SyncQueryService(AppDbContext db)
{
    /// <summary>The six entity types §3.5.1 flows through <c>/sync</c>. Extension types (recurring_rule,
    /// comment) are out of this v1 slice — their sync is added by their own DDL/hydration patch.</summary>
    private static readonly string[] SyncedTypes =
    [
        ChangeLogEntityTypes.Group, ChangeLogEntityTypes.Member, ChangeLogEntityTypes.Expense,
        ChangeLogEntityTypes.Settlement, ChangeLogEntityTypes.Category, ChangeLogEntityTypes.Access,
    ];

    // Head = max(seq), falling back to pruned_through_seq when change_log is empty (§3.5.4) — never 0.
    private const string HeadSql =
        "SELECT COALESCE((SELECT MAX(seq) FROM change_log), w.pruned_through_seq) " +
        "FROM change_log_watermark w WHERE w.one = TRUE;";

    // One snapshot: watermark row (always present) + up to @take membership-filtered change rows.
    private const string PageSql = """
        SELECT
            w.pruned_through_seq AS PrunedThroughSeq,
            c.seq                AS Seq,
            c.group_id           AS GroupId,
            c.entity_type        AS EntityType,
            c.entity_id          AS EntityId,
            c.is_delete          AS IsDelete
        FROM change_log_watermark w
        LEFT JOIN LATERAL (
            SELECT cl.seq, cl.group_id, cl.entity_type, cl.entity_id, cl.is_delete
            FROM change_log cl
            WHERE cl.seq > @after
              AND cl.entity_type = ANY(@syncedTypes)
              AND ( (cl.entity_type <> 'access'
                     AND cl.group_id IN (SELECT gm.group_id FROM group_members gm
                                         WHERE gm.user_id = @me AND gm.deleted_at IS NULL))
                 OR (cl.entity_type = 'access' AND cl.entity_id = @me) )
            ORDER BY cl.seq
            LIMIT @take
        ) c ON TRUE
        WHERE w.one = TRUE
        ORDER BY c.seq NULLS LAST;
        """;

    /// <summary>Mint the head cursor's <c>seq</c> for a no-<c>since</c> bootstrap call (§3.5.4).</summary>
    public async Task<long> GetHeadSeqAsync(CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(HeadSql, cancellationToken: ct));
    }

    /// <summary>Read one delta page plus the prune watermark in a single snapshot. <paramref name="limit"/>
    /// is the caller's page size; the query fetches <c>limit + 1</c> to set <c>hasMore</c>.</summary>
    public async Task<ChangePage> ReadPageAsync(Guid me, long after, int limit, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var rows = (await conn.QueryAsync<ChangePageRow>(new CommandDefinition(
            PageSql,
            new { after, me, syncedTypes = SyncedTypes, take = limit + 1 },
            cancellationToken: ct))).ToList();

        // The watermark row is always present (single-row table + LEFT JOIN), so rows is never empty.
        var prunedThroughSeq = rows.Count > 0 ? rows[0].PrunedThroughSeq : 0L;

        var changes = rows
            .Where(r => r.Seq is not null)
            .Select(r => new RawChange(r.Seq!.Value, r.GroupId!.Value, r.EntityType!, r.EntityId!.Value, r.IsDelete!.Value))
            .ToList();

        return new ChangePage(prunedThroughSeq, changes);
    }

    /// <summary>Dapper projection of the page statement; the <c>c.*</c> columns are null on an empty page.</summary>
    private sealed record ChangePageRow(
        long PrunedThroughSeq,
        long? Seq,
        Guid? GroupId,
        string? EntityType,
        Guid? EntityId,
        bool? IsDelete);
}

/// <summary>A single visible change-log row after the membership/access filter (§3.5.3).</summary>
internal sealed record RawChange(long Seq, Guid GroupId, string EntityType, Guid EntityId, bool IsDelete);

/// <summary>One snapshot: the prune boundary (§3.5.5) plus the ordered, filtered page rows.</summary>
internal sealed record ChangePage(long PrunedThroughSeq, IReadOnlyList<RawChange> Rows);

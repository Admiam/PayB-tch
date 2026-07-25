using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Expenses;
using Paybitch.Domain.Splitting;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;
using DomainCurrency = Paybitch.Domain.Currency;

namespace Paybitch.Api.Features.Recurring;

/// <summary>The result of one claim+fire pass over a single due rule (for the scheduler's telemetry).</summary>
public enum FireOutcome
{
    NoWork,           // no due rule was claimable this pass
    Fired,            // ≥1 occurrence materialized + rule advanced
    Advanced,         // rule advanced with no new materialization (e.g. self-replay only)
    AutoPaused,       // payer/member/group/backlog axis auto-paused the rule (EXT-D3e/g/i)
    OperationalError, // a foreign expense squats the deterministic clientId — rule left unadvanced (EXT-D3f)
}

/// <summary>
/// The money-correctness core of E3 (EXT-D3e/f/g/i/j): claim ONE due rule <c>FOR UPDATE SKIP LOCKED</c>,
/// check the payer / split-member / group / backlog auto-pause axes, materialize every missed occurrence
/// through the shared <see cref="ExpenseWriter"/> with a DETERMINISTIC <c>client_id</c> (so D9 absorbs a
/// double-fire), and advance <c>next_run_at</c>/<c>last_run_at</c>/<c>remaining_count</c>/<c>status</c> — all
/// in one transaction per rule. The scheduler drives it; every mutation appends its <c>recurring_rule</c> (and
/// per-occurrence <c>expense</c>) change_log row in the same transaction (§3.5.2).
/// </summary>
public sealed class RecurringFireService(ILogger<RecurringFireService> logger)
{
    // Claim the single most-overdue active rule; SKIP LOCKED lets N instances coordinate lock-free (EXT-D3j).
    private const string ClaimSql = """
        SELECT * FROM recurring_rules
        WHERE status = 'active' AND deleted_at IS NULL AND next_run_at <= {0}
        ORDER BY next_run_at
        FOR UPDATE SKIP LOCKED
        LIMIT 1
        """;

    /// <summary>
    /// Open a transaction, claim one due rule, process it, and commit. Returns <see cref="FireOutcome.NoWork"/>
    /// when nothing is due. An operational error rolls the transaction back so the rule is retried next pass —
    /// never a silent skip and never a double-charge.
    /// </summary>
    public async Task<FireOutcome> ProcessOneDueRuleAsync(
        AppDbContext db, IChangeLogWriter changeLog, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rule = await ClaimDueRuleAsync(db, now, ct);
        if (rule is null)
        {
            await tx.CommitAsync(ct);
            return FireOutcome.NoWork;
        }

        var outcome = await ProcessAsync(rule, db, changeLog, clock, now, ct);
        if (outcome == FireOutcome.OperationalError)
        {
            await tx.RollbackAsync(ct); // leave next_run_at untouched — retried next pass
            return outcome;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    private static async Task<RecurringRule?> ClaimDueRuleAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // ToList (not First) so EF runs the raw SQL verbatim and the FOR UPDATE SKIP LOCKED survives (mirrors
        // the expense aggregate lock). The {0} placeholder is bound as a DbParameter. The row lock is held to COMMIT.
        var rows = await db.RecurringRules
            .FromSqlRaw(ClaimSql, now)
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    private async Task<FireOutcome> ProcessAsync(
        RecurringRule rule, AppDbContext db, IChangeLogWriter changeLog, IClock clock, DateTimeOffset now, CancellationToken ct)
    {
        if (!RecurrenceEngine.TryResolveTimeZone(rule.Timezone, out var tz))
        {
            // Validated at the boundary, so this is a data-integrity anomaly: leave the rule for a human.
            logger.LogError("Recurring rule {RuleId} has an unresolvable timezone '{Tz}'; skipping", rule.Id, rule.Timezone);
            return FireOutcome.OperationalError;
        }
        var jitter = RecurrenceEngine.JitterMinutes(rule.Id);

        // --- Auto-pause axes (EXT-D3e/i): archived group → payer → pinned split member ---
        var archivedAt = await db.Groups.AsNoTracking()
            .Where(g => g.Id == rule.GroupId).Select(g => g.ArchivedAt).FirstOrDefaultAsync(ct);
        if (archivedAt is not null)
            return AutoPause(rule, changeLog, PauseReasons.GroupArchived);

        var payer = await db.GroupMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == rule.PaidBy, ct);
        if (payer is null || payer.DeletedAt is not null)
            return AutoPause(rule, changeLog, PauseReasons.PayerRemoved);

        var rows = await db.RecurringRuleSplits.AsNoTracking().Where(s => s.RuleId == rule.Id).ToListAsync(ct);
        var pinned = rows.Count > 0;

        IReadOnlyList<Guid> activeMemberIds;
        if (pinned)
        {
            var pinnedIds = rows.Select(r => r.GroupMemberId).ToHashSet();
            var livePinned = await db.GroupMembers.AsNoTracking()
                .CountAsync(m => pinnedIds.Contains(m.Id) && m.DeletedAt == null, ct);
            if (livePinned != pinnedIds.Count) // any pinned member soft-deleted or absent → pause (EXT-D3e)
                return AutoPause(rule, changeLog, PauseReasons.MemberRemoved);
            activeMemberIds = rows.Select(r => r.GroupMemberId).ToList();
        }
        else
        {
            activeMemberIds = await db.GroupMembers.AsNoTracking()
                .Where(m => m.GroupId == rule.GroupId && m.DeletedAt == null)
                .Select(m => m.Id).ToListAsync(ct); // dynamic-equal recomputes the active set every fire (EXT-D3d)
        }

        // --- Collect due occurrences from the current position; detect backlog + the next future run ---
        var due = new List<Occurrence>();
        DateTimeOffset? firstFuture = null;
        var backlog = false;
        foreach (var occ in RecurrenceEngine.EnumerateOccurrences(rule, tz, jitter))
        {
            if (rule.NextRunAt is { } nra && occ.RunsAt < nra)
                continue; // strictly before the current position — already fired or skipped
            if (occ.RunsAt <= now)
            {
                due.Add(occ);
                if (due.Count > RecurringLimits.CatchUpCap) { backlog = true; break; }
            }
            else
            {
                firstFuture = occ.RunsAt;
                break;
            }
        }

        if (backlog)
            return AutoPause(rule, changeLog, PauseReasons.BacklogExceeded); // zero expenses (§3.8)

        // remaining_count clamp: fire at most `remaining`, then end.
        var endByCount = false;
        if (rule.RemainingCount is int rc && due.Count >= rc)
        {
            due = due.Take(rc).ToList();
            endByCount = true;
        }

        // --- Materialize each due occurrence (idempotent by construction, EXT-D3f) ---
        var writer = new ExpenseWriter(db, changeLog, clock);
        var currency = DomainCurrency.FromCode(rule.Currency);
        SplitType? fireSplit = due.Count > 0 ? RecurringMapping.BuildFireSplit(rule, rows, activeMemberIds) : null;
        var fired = 0;

        foreach (var occ in due)
        {
            var clientId = FiredClientId(rule.Id, occ.Date);
            var existing = await db.Expenses.AsNoTracking()
                .FirstOrDefaultAsync(e => e.GroupId == rule.GroupId && e.ClientId == clientId, ct);
            if (existing is not null)
            {
                if (existing.RecurringRuleId == rule.Id)
                {
                    fired++; // self-replay of this same occurrence → already fired, count it and advance past it
                    continue;
                }
                // Any other row on the reserved key is impossible for user creates (^rec: rejected at the
                // expense boundary, §3.7): a telemetry event, NOT a silent advance and NOT a double-charge.
                logger.LogError(
                    "Recurring rule {RuleId}: occurrence {Date} clientId {ClientId} is held by non-rule expense {ExpenseId}; leaving rule unadvanced (operational error, EXT-D3f)",
                    rule.Id, occ.Date, clientId, existing.Id);
                return FireOutcome.OperationalError;
            }

            var cmd = new ExpenseWriteCommand(
                GroupId: rule.GroupId,
                ClientId: clientId,
                Title: rule.Title,
                AmountMinor: rule.AmountMinor,
                Currency: currency,
                PaidBy: rule.PaidBy,
                Date: occ.Date,
                Split: fireSplit!,
                CategoryId: rule.CategoryId,
                IconSymbol: rule.IconSymbol,
                Notes: rule.Notes,
                CreatedBy: rule.CreatedBy,
                RecurringRuleId: rule.Id);
            writer.Stage(cmd);
            fired++;
        }

        // --- Advance the rule (EXT-D3i) ---
        // A claimed rule always represents progress: it either fired ≥1 occurrence, rolled next_run_at to the
        // first future occurrence, or ended. So it always genuinely changed → always bump + emit a sync row.
        if (due.Count > 0)
            rule.LastRunAt = due[^1].RunsAt;
        if (rule.RemainingCount is int rc2)
            rule.RemainingCount = Math.Max(0, rc2 - due.Count);

        if (endByCount || rule.RemainingCount == 0 || firstFuture is null)
        {
            rule.Status = RuleStatus.Ended;
            rule.NextRunAt = null;
            rule.PauseReason = null;
        }
        else
        {
            rule.NextRunAt = firstFuture; // stays active
        }

        rule.Version += 1;
        changeLog.Append(rule.GroupId, ChangeLogEntityTypes.RecurringRule, rule.Id, isDelete: false);
        return fired > 0 ? FireOutcome.Fired : FireOutcome.Advanced;
    }

    /// <summary>Auto-pause: status→paused with a machine reason, keep <c>next_run_at</c> (resume rolls it forward).</summary>
    private static FireOutcome AutoPause(RecurringRule rule, IChangeLogWriter changeLog, string reason)
    {
        rule.Status = RuleStatus.Paused;
        rule.PauseReason = reason;
        rule.Version += 1;
        changeLog.Append(rule.GroupId, ChangeLogEntityTypes.RecurringRule, rule.Id, isDelete: false);
        return FireOutcome.AutoPaused;
    }

    /// <summary>The deterministic, server-owned fired-expense clientId (EXT-D3f): <c>rec:{ruleId}:{occurrenceDate}</c>.</summary>
    public static string FiredClientId(Guid ruleId, DateOnly occurrenceDate) =>
        $"{RecurringLimits.FiredClientIdPrefix}{ruleId}:{ExpenseWire.Date(occurrenceDate)}";
}

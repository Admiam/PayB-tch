using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Recurring;

/// <summary>One scheduled occurrence: its local calendar <see cref="Date"/> and the UTC <see cref="RunsAt"/> instant.</summary>
public readonly record struct Occurrence(DateOnly Date, DateTimeOffset RunsAt);

/// <summary>
/// The curated recurrence calculator (EXT-D3a/b/c). Pure and deterministic — no DbContext, no wall clock
/// except what the caller passes. Occurrence <b>dates</b> are a tz-independent calendar sequence anchored on
/// <c>starts_on</c> with a non-destructive end-of-month clamp (EXT-D3b); each date's UTC run <b>instant</b>
/// is local-midnight-in-tz + a deterministic per-rule jitter, so the local wall-clock anchor is stable while
/// the UTC instant floats with the DST offset (EXT-D3c).
///
/// Uses the framework's ICU-backed <see cref="TimeZoneInfo"/> for IANA tzdb ids (NodaTime is unavailable in
/// this build); .NET 10 resolves ids like <c>Europe/Prague</c> cross-platform.
/// </summary>
public static class RecurrenceEngine
{
    // Hard cap on the calendar walk so a pathological rule can never spin forever.
    private const int MaxCalendarSteps = 200_000;

    /// <summary>Validate + resolve an IANA tzdb id (rejects garbage that would stall the worker, EXT-D3c/§3.7).</summary>
    public static bool TryResolveTimeZone(string? ianaId, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(ianaId))
            return false;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var tz) && tz is not null)
        {
            timeZone = tz;
            return true;
        }
        return false;
    }

    /// <summary>Deterministic 0..15-minute jitter from the rule id (FNV-1a over the guid bytes), EXT-D3c.</summary>
    public static int JitterMinutes(Guid ruleId)
    {
        unchecked
        {
            uint acc = 2166136261u;
            foreach (var b in ruleId.ToByteArray())
                acc = (acc ^ b) * 16777619u;
            return (int)(acc % (uint)RecurringLimits.JitterMinutesModulo);
        }
    }

    /// <summary>The UTC run instant for an occurrence date: local midnight + jitter, converted through the tz.</summary>
    public static DateTimeOffset RunInstant(DateOnly occurrenceDate, TimeZoneInfo timeZone, int jitterMinutes)
    {
        var local = new DateTime(
            occurrenceDate.Year, occurrenceDate.Month, occurrenceDate.Day,
            0, jitterMinutes, 0, DateTimeKind.Unspecified);

        // Local midnight is virtually never inside a spring-forward gap (transitions are ~02:00–03:00), but
        // guard anyway: nudge past the gap so ConvertTimeToUtc never throws.
        var guard = 0;
        while (timeZone.IsInvalidTime(local) && guard++ < 4)
            local = local.AddHours(1);

        var utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero);
    }

    /// <summary>
    /// The ascending calendar sequence of occurrence dates for a rule: filtered to <c>&gt;= starts_on</c> and
    /// (if set) <c>&lt;= ends_on</c>. Lazy + bounded; the caller applies count / cursor limits.
    /// </summary>
    public static IEnumerable<DateOnly> EnumerateDates(RecurringRule rule)
    {
        for (var k = 0; k < MaxCalendarSteps; k++)
        {
            var date = DateAtIndex(rule, k);
            if (date < rule.StartsOn)
                continue; // leading clamp: e.g. by_month_day before starts_on in the anchor month
            if (rule.EndsOn is { } end && date > end)
                yield break;
            yield return date;
        }
    }

    /// <summary>The occurrence stream (date + UTC run instant) for a rule under a resolved tz + jitter.</summary>
    public static IEnumerable<Occurrence> EnumerateOccurrences(RecurringRule rule, TimeZoneInfo tz, int jitterMinutes)
    {
        foreach (var date in EnumerateDates(rule))
            yield return new Occurrence(date, RunInstant(date, tz, jitterMinutes));
    }

    /// <summary>
    /// First-schedule computation (EXT-D3g): the first occurrence date is <c>&gt;= greatest(starts_on, floor)</c>
    /// — a past <c>starts_on</c> sets the anchor, never a backfill. Returns null when no occurrence exists
    /// (end bound already passed or <c>remaining_count == 0</c>) ⇒ the rule is born <c>ended</c>.
    /// </summary>
    public static DateTimeOffset? FirstRunOnOrAfter(
        RecurringRule rule, TimeZoneInfo tz, int jitterMinutes, DateOnly floor)
    {
        if (rule.RemainingCount == 0)
            return null;

        foreach (var date in EnumerateDates(rule))
        {
            if (date >= floor)
                return RunInstant(date, tz, jitterMinutes);
        }
        return null;
    }

    /// <summary>
    /// Resume roll-forward (EXT-D3i): the first occurrence strictly after <paramref name="now"/> — a pause
    /// skips its window, it does not back-charge. Null ⇒ no future occurrence ⇒ the rule ends.
    /// </summary>
    public static DateTimeOffset? FirstRunStrictlyAfter(
        RecurringRule rule, TimeZoneInfo tz, int jitterMinutes, DateTimeOffset now)
    {
        foreach (var occ in EnumerateOccurrences(rule, tz, jitterMinutes))
        {
            if (occ.RunsAt > now)
                return occ.RunsAt;
        }
        return null;
    }

    private static DateOnly DateAtIndex(RecurringRule rule, int k) => rule.Freq switch
    {
        Freq.Weekly => WeeklyDate(rule, k),
        Freq.Monthly => MonthlyDate(rule, k),
        Freq.Yearly => YearlyDate(rule, k),
        _ => throw new InvalidOperationException($"Unknown freq '{rule.Freq}'."),
    };

    private static DateOnly WeeklyDate(RecurringRule rule, int k)
    {
        var isoTarget = rule.ByWeekday ?? 1;
        var baseDate = FirstOnOrAfterWeekday(rule.StartsOn, isoTarget);
        return baseDate.AddDays(checked(7 * rule.Interval * k));
    }

    private static DateOnly MonthlyDate(RecurringRule rule, int k)
    {
        var day = rule.ByMonthDay ?? 1;
        var anchor = new DateOnly(rule.StartsOn.Year, rule.StartsOn.Month, 1).AddMonths(rule.Interval * k);
        var clamped = Math.Min(day, DateTime.DaysInMonth(anchor.Year, anchor.Month)); // non-destructive clamp (EXT-D3b)
        return new DateOnly(anchor.Year, anchor.Month, clamped);
    }

    private static DateOnly YearlyDate(RecurringRule rule, int k)
    {
        var year = rule.StartsOn.Year + rule.Interval * k;
        var month = rule.StartsOn.Month;
        var clamped = Math.Min(rule.StartsOn.Day, DateTime.DaysInMonth(year, month)); // Feb-29 anchor → 28 in common years
        return new DateOnly(year, month, clamped);
    }

    /// <summary>The first date on/after <paramref name="from"/> whose ISO weekday (1=Mon..7=Sun) equals the target.</summary>
    private static DateOnly FirstOnOrAfterWeekday(DateOnly from, int isoWeekday)
    {
        var startIso = ((int)from.DayOfWeek + 6) % 7 + 1; // DayOfWeek: Sun=0..Sat=6 → ISO Mon=1..Sun=7
        var delta = (isoWeekday - startIso + 7) % 7;
        return from.AddDays(delta);
    }
}

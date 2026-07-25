namespace Paybitch.Api.Features.Platform.Jobs;

/// <summary>
/// Tunables for the E0 job drain loop (§0.4.2). Defaults run the app with zero config; a deployment
/// overrides any value via the <c>Jobs</c> configuration section (Appendix A). <c>max_attempts</c> is
/// intentionally NOT here — it lives per-row on <c>jobs.max_attempts</c> (default 8) so a single job
/// can carry its own poison budget.
/// </summary>
public sealed class JobRunnerOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Delay before the next claim after a job ran (busy cadence, ~1 s ± jitter).</summary>
    public TimeSpan BusyPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Delay before the next claim when the queue was empty (idle cadence, ~5 s ± jitter).</summary>
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A lease older than this makes a <c>running</c> job reclaimable (EXT-D0h) — restoring at-least-once
    /// across a worker kill / rolling deploy. Set comfortably above the longest handler runtime (export
    /// builds are the long pole) so a slow-but-alive job is never stolen.
    /// </summary>
    public TimeSpan LeaseTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Base unit of the exponential failure backoff (<c>run_at = now + min(2^attempts·base, cap)±jitter</c>).</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound of the failure backoff, so a high attempt count can't push <c>run_at</c> arbitrarily far.</summary>
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromHours(1);

    /// <summary>± fraction applied to poll delays and backoff to de-sync N instances (0.2 = ±20%).</summary>
    public double JitterFraction { get; set; } = 0.2;

    /// <summary>How often the drain loop emits the queue-health gauges (EXT-D0d).</summary>
    public TimeSpan GaugeLogInterval { get; set; } = TimeSpan.FromSeconds(60);
}

namespace Paybitch.Api.Features.Platform.Jobs;

/// <summary>
/// A per-<see cref="Kind"/> job processor (E0 §0.4.2). An extension implements this once for each
/// job kind it owns (e.g. <c>recurring.materialize</c>, <c>export.build</c>, <c>email.send</c>);
/// <c>AddPlatform</c> auto-registers every implementation in the API assembly, and
/// <see cref="JobRunner"/> dispatches a claimed job to the handler whose <see cref="Kind"/> matches.
/// </summary>
/// <remarks>
/// Delivery is <b>at-least-once</b> (X5): a handler MAY run more than once for the same job (a
/// worker crash mid-run, a stale-lease reclaim across a deploy). Handlers therefore MUST be
/// idempotent — e.g. money-creating work derives a deterministic <c>client_id</c> so a re-run
/// no-ops against the D9 unique constraint (EXT-D0c). A thrown exception reschedules the job with
/// backoff; past <c>max_attempts</c> it dead-letters (<c>status='dead'</c>, EXT-D0b).
/// </remarks>
public interface IJobHandler
{
    /// <summary>The <c>jobs.kind</c> discriminator this handler processes. Must be unique per handler.</summary>
    string Kind { get; }

    /// <summary>Process one claimed job. Throw to signal failure (⇒ backoff-retry or dead-letter).</summary>
    Task HandleAsync(JobContext job, CancellationToken ct);
}

/// <summary>
/// The immutable slice of a claimed <c>jobs</c> row handed to an <see cref="IJobHandler"/>.
/// <see cref="Payload"/> is the raw JSON (ids only — never money/PII beyond ids, §4.3);
/// <see cref="Attempts"/> is this run's attempt count (already incremented at claim time).
/// </summary>
public sealed record JobContext(Guid Id, string Kind, string Payload, int Attempts);

namespace Paybitch.Api.Features.Platform.Jobs;

/// <summary>
/// The <c>jobs.status</c> lifecycle values, mirroring the <c>ck_jobs_status</c> DB check constraint
/// (§0.4.2). A single home for these strings so the enqueue/claim/mark SQL can never drift from the
/// schema. <c>queued → running → succeeded</c>, or on failure <c>running → queued</c> (backoff-retry)
/// / <c>running → dead</c> (poison, past <c>max_attempts</c>).
/// </summary>
public static class JobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Dead = "dead";
}

/// <summary>Canonical <c>jobs.kind</c> discriminators owned by E0 itself. Extensions add their own.</summary>
public static class JobKinds
{
    /// <summary>Two-phase blob hard-delete (X4): DELETE + HEAD-404 verify, then stamp <c>blob_deletions.confirmed_at</c>.</summary>
    public const string BlobHardDelete = "blob.hard_delete";
}

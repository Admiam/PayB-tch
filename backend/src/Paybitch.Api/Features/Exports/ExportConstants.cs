namespace Paybitch.Api.Features.Exports;

/// <summary>
/// E4 export constants (§4). The <c>export.build</c> job kind (§0.4.2), the <c>export_results.kind</c>
/// allowlist (mirrors <c>ck_export_results_kind</c>), the artifact blob key prefix (X4 blob class
/// <c>export</c>), and the artifact TTL. Kept in one place so the endpoint, the validators, and the job
/// handlers can never drift from the DB CHECK constraints.
/// </summary>
public static class ExportKinds
{
    public const string Csv = "csv";
    public const string Pdf = "pdf";
    public const string Gdpr = "gdpr";

    /// <summary>The async kinds a member may request via <c>POST /groups/{g}/exports</c> (EXT-D4a/d).</summary>
    public static readonly IReadOnlySet<string> AsyncGroupKinds =
        new HashSet<string>(StringComparer.Ordinal) { Csv, Pdf };
}

/// <summary>The <c>export_results.content_type</c> allowlist (mirrors <c>ck_export_results_content_type</c>).</summary>
public static class ExportContentTypes
{
    public const string Csv = "text/csv";
    public const string Pdf = "application/pdf";
    public const string Json = "application/json";

    /// <summary>The content type an export of <paramref name="kind"/> produces.</summary>
    public static string ForKind(string kind) => kind switch
    {
        ExportKinds.Csv => Csv,
        ExportKinds.Pdf => Pdf,
        ExportKinds.Gdpr => Json,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown export kind."),
    };
}

/// <summary>The E0 <c>jobs.kind</c> discriminators E4 owns (§0.4.2). Extensions add their own beside <c>JobKinds</c>.</summary>
public static class ExportJobKinds
{
    /// <summary>Build a stored artifact (CSV/PDF/GDPR) into <see cref="Platform.Blob.IBlobStore"/> (EXT-D4d).</summary>
    public const string Build = "export.build";

    /// <summary>Delayed TTL reaper: mark an artifact <c>expired</c> and two-phase reclaim its blob (X4, §4.6).</summary>
    public const string Reap = "export.reap";
}

/// <summary>
/// E4 operational tunables (§4.7, Appendix A "proposed"). Local until the orchestrator folds them into
/// <see cref="Common.Options.OperationalConstants"/> and adds the per-surface rate-limit partitions.
/// </summary>
public static class ExportOperational
{
    /// <summary>Artifact TTL — Appendix A proposed: 24 h (blob lifecycle + two-phase delete, X4).</summary>
    public static readonly TimeSpan ArtifactTtl = TimeSpan.FromHours(24);

    /// <summary>Presigned download-URL TTL (fallback only; default path is the authenticated stream) — 5 min.</summary>
    public static readonly TimeSpan PresignedDownloadTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The <c>blob_deletions.reason</c> used when an artifact is reclaimed at TTL (allowed value). NOTE: the
    /// export artifact's <see cref="Platform.Blob.IBlobStore"/> object key is the <c>export_results.id</c>
    /// (a non-enumerable uuidv7) rendered via <c>Guid.ToString()</c> — the exact key scheme the E0
    /// <c>blob.hard_delete</c> handler reconstructs from <c>blob_deletions.storage_key</c>, so two-phase
    /// reclaim (X4) works with the platform handler unchanged.
    /// </summary>
    public const string TtlReclaimReason = "orphan";

    /// <summary>Truncation bound for the <c>export_results.error</c> column (never a stack trace / PII, §4.3).</summary>
    public const int ErrorMaxLength = 300;
}

/// <summary>
/// The additive <c>group.exported</c> activity verb (§3.10, EXT-D4f). Declared locally so E4 compiles
/// without editing the Activity slice's closed taxonomy; the orchestrator must add it to
/// <c>ActivityVerbs</c> / <c>ActivityVerbs.All</c> for read-time hydration + verification.
/// </summary>
public static class ExportActivity
{
    public const string GroupExported = "group.exported";
}

/// <summary>
/// E4-local problem <c>code</c>s not yet in the closed v1 catalog (<c>ProblemCodes</c>, owned by Common).
/// <c>Problems.Gone</c> accepts any string and falls back to the code as its title, so this works today;
/// the orchestrator should promote <see cref="ExportExpired"/> into <c>ProblemCodes</c> for the catalog.
/// </summary>
public static class ExportProblemCodes
{
    /// <summary>410 on a download after <c>expires_at</c> (§4.8: "download after expires_at → 410").</summary>
    public const string ExportExpired = "export_expired";
}

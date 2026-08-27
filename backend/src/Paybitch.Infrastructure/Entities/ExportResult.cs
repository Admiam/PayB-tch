namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E4 export_results — pollable/downloadable artifact record. Bytes live in IBlobStore (referenced by
/// key, X4). Ephemeral, self-scoped, never synced. group_id NULL ⇒ a GDPR /me export.
/// </summary>
public sealed class ExportResult
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid RequestedBy { get; set; }
    public Guid? GroupId { get; set; }
    public string Kind { get; set; } = string.Empty;          // 'csv' | 'pdf' | 'gdpr'
    public string ContentType { get; set; } = string.Empty;   // 'text/csv' | 'application/pdf' | 'application/json'
    public string Params { get; set; } = "{}";                // jsonb, scalars + ids only
    public string Status { get; set; } = "pending";           // 'pending' | 'ready' | 'failed' | 'expired'
    public string? StorageKey { get; set; }                   // opaque IBlobStore key; NULL until ready
    public long? ByteSize { get; set; }
    public byte[]? ContentHash { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

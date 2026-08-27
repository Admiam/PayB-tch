namespace Paybitch.Infrastructure.Entities;

/// <summary>
/// E7 blob_deletions — two-phase blob-deletion ledger (X4). A row = "the object at storage_key must die."
/// Written in the same tx as the tombstone / avatar overwrite / GDPR anonymize; E0 worker confirms.
/// </summary>
public sealed class BlobDeletion
{
    public Guid StorageKey { get; set; }                     // the object to hard-delete, PK
    public string Reason { get; set; } = string.Empty;      // 'attachment_purged' | 'avatar_replaced' | 'gdpr_erase' | 'orphan'
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConfirmedAt { get; set; }        // set on DELETE + HEAD-404 verify
    public int Attempts { get; set; }
}

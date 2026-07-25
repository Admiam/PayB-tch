using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class BlobDeletionConfiguration : IEntityTypeConfiguration<BlobDeletion>
{
    public void Configure(EntityTypeBuilder<BlobDeletion> b)
    {
        b.ToTable("blob_deletions", t =>
            t.HasCheckConstraint("ck_blob_deletions_reason",
                "reason IN ('attachment_purged','avatar_replaced','gdpr_erase','orphan')"));

        b.HasKey(x => x.StorageKey);
        b.Property(x => x.StorageKey).ValueGeneratedNever();
        b.Property(x => x.Reason).IsRequired();
        b.Property(x => x.RequestedAt).HasDefaultValueSql("now()");
        b.Property(x => x.Attempts).HasDefaultValue(0);

        b.HasIndex(x => x.RequestedAt)
            .HasDatabaseName("ix_blob_deletions_pending")
            .HasFilter("confirmed_at IS NULL");
    }
}

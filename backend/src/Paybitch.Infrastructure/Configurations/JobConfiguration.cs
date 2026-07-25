using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.ToTable("jobs", t =>
            t.HasCheckConstraint("ck_jobs_status",
                "status IN ('queued','running','succeeded','failed','dead')"));

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).IsRequired();
        b.Property(x => x.Payload).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        b.Property(x => x.Status).HasDefaultValue("queued");
        b.Property(x => x.RunAt).HasDefaultValueSql("now()");
        b.Property(x => x.Priority).HasColumnType("smallint").HasDefaultValue(100);
        b.Property(x => x.Attempts).HasDefaultValue(0);
        b.Property(x => x.MaxAttempts).HasDefaultValue(8);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasIndex(x => new { x.Priority, x.RunAt })
            .HasDatabaseName("ix_jobs_claim")
            .HasFilter("status = 'queued'");

        b.HasIndex(x => x.LockedAt)
            .HasDatabaseName("ix_jobs_reap")
            .HasFilter("status = 'running'");

        b.HasIndex(x => x.DedupeKey)
            .IsUnique()
            .HasDatabaseName("uq_jobs_dedupe")
            .HasFilter("dedupe_key IS NOT NULL AND status IN ('queued','running')");
    }
}

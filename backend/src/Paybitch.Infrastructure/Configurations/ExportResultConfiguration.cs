using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class ExportResultConfiguration : IEntityTypeConfiguration<ExportResult>
{
    public void Configure(EntityTypeBuilder<ExportResult> b)
    {
        b.ToTable("export_results", t =>
        {
            t.HasCheckConstraint("ck_export_results_kind", "kind IN ('csv','pdf','gdpr')");
            t.HasCheckConstraint("ck_export_results_content_type",
                "content_type IN ('text/csv','application/pdf','application/json')");
            t.HasCheckConstraint("ck_export_results_status",
                "status IN ('pending','ready','failed','expired')");
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).IsRequired();
        b.Property(x => x.ContentType).IsRequired();
        b.Property(x => x.Params).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        b.Property(x => x.Status).HasDefaultValue("pending");
        b.Property(x => x.RequestedAt).HasDefaultValueSql("now()");

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.RequestedBy)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.RequestedBy, x.RequestedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_export_results_user");

        // NOTE (migration): uq_export_results_inflight is a partial UNIQUE on
        // (requested_by, group_id, kind, md5(params::text)) WHERE status='pending'. The md5(params)
        // expression cannot be expressed via the EF fluent API — add it in the migration.
    }
}

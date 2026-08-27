using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class ChangeLogEntryConfiguration : IEntityTypeConfiguration<ChangeLogEntry>
{
    public void Configure(EntityTypeBuilder<ChangeLogEntry> b)
    {
        // Consolidated entity_type value set (Appendix A) — v1 + 'recurring_rule' + 'comment'.
        // 'attachment' is intentionally absent (E1 excluded from this build).
        b.ToTable("change_log", t =>
            t.HasCheckConstraint("ck_change_log_entity_type",
                "entity_type IN ('group','member','expense','settlement','category','access','recurring_rule','comment')"));

        b.HasKey(x => x.Seq);
        b.Property(x => x.Seq).UseSerialColumn();          // bigserial
        b.Property(x => x.EntityType).IsRequired();
        b.Property(x => x.IsDelete).HasDefaultValue(false);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.GroupId, x.Seq }).HasDatabaseName("ix_change_log_group");

        b.HasIndex(x => new { x.EntityId, x.Seq })
            .HasDatabaseName("ix_change_log_access")
            .HasFilter("entity_type = 'access'");
    }
}

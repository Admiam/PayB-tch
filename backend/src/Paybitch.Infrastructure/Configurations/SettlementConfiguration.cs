using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class SettlementConfiguration : IEntityTypeConfiguration<Settlement>
{
    public void Configure(EntityTypeBuilder<Settlement> b)
    {
        b.ToTable("settlements", t =>
        {
            t.HasCheckConstraint("ck_settlements_amount_positive", "amount_minor > 0");
            t.HasCheckConstraint("ck_settlements_distinct_members", "from_member <> to_member");
            t.HasCheckConstraint("ck_settlements_notes_length", "notes IS NULL OR length(notes) <= 2000");
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.Version).HasDefaultValue(1);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<Currency>().WithMany()
            .HasForeignKey(x => x.Currency)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<GroupMember>().WithMany()
            .HasForeignKey(x => x.FromMember)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<GroupMember>().WithMany()
            .HasForeignKey(x => x.ToMember)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.CreatedBy)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => new { x.GroupId, x.ClientId })
            .IsUnique()
            .HasDatabaseName("uq_settlements_group_client");

        b.HasIndex(x => new { x.GroupId, x.SettledOn })
            .IsDescending(false, true)
            .HasDatabaseName("ix_settlements_group")
            .HasFilter("deleted_at IS NULL");
    }
}

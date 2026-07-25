using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class GroupMemberConfiguration : IEntityTypeConfiguration<GroupMember>
{
    public void Configure(EntityTypeBuilder<GroupMember> b)
    {
        b.ToTable("group_members", t =>
        {
            t.HasCheckConstraint("ck_group_members_role",
                "role IN ('owner','admin','member')");
            // A ghost (user_id NULL) can never hold a privileged role (§3.8.2).
            t.HasCheckConstraint("ck_group_members_ghost_role",
                "user_id IS NOT NULL OR role = 'member'");
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.DisplayName).IsRequired();
        b.Property(x => x.Role).HasDefaultValue("member");
        b.Property(x => x.Version).HasDefaultValue(1);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.FormerUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // A real user appears once per group (NULL user_id = ghost, allowed many times).
        b.HasIndex(x => new { x.GroupId, x.UserId })
            .IsUnique()
            .HasDatabaseName("uq_group_members_group_user");

        b.HasIndex(x => x.GroupId)
            .HasDatabaseName("ix_group_members_group")
            .HasFilter("deleted_at IS NULL");

        b.HasIndex(x => x.UserId)
            .HasDatabaseName("ix_group_members_user")
            .HasFilter("user_id IS NOT NULL AND deleted_at IS NULL");
    }
}

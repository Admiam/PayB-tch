using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class InviteConfiguration : IEntityTypeConfiguration<Invite>
{
    public void Configure(EntityTypeBuilder<Invite> b)
    {
        b.ToTable("invites");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TokenHash).IsRequired();
        b.Property(x => x.Email).HasColumnType("citext");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<GroupMember>().WithMany()
            .HasForeignKey(x => x.MemberId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.InvitedBy)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => x.TokenHash)
            .IsUnique()
            .HasDatabaseName("uq_invites_token_hash");
    }
}

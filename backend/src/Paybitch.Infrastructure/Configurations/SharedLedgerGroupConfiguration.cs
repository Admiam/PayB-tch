using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class SharedLedgerGroupConfiguration : IEntityTypeConfiguration<SharedLedgerGroup>
{
    public void Configure(EntityTypeBuilder<SharedLedgerGroup> b)
    {
        b.ToTable("shared_ledger_groups");

        // The pair *is* the identity — a group is either in the user's shared view or it isn't, so a
        // surrogate key would only permit a contradiction the composite key makes unrepresentable.
        b.HasKey(x => new { x.UserId, x.GroupId });
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Cascade, not restrict: a deleted group cannot be in anyone's shared view, and leaving the
        // row behind would resurrect it if the id were ever reused.
        b.HasOne<Group>().WithMany()
            .HasForeignKey(x => x.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // No index on user_id alone: the composite primary key already leads with it.
    }
}

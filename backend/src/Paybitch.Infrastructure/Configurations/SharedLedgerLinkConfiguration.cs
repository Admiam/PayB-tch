using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class SharedLedgerLinkConfiguration : IEntityTypeConfiguration<SharedLedgerLink>
{
    public void Configure(EntityTypeBuilder<SharedLedgerLink> b)
    {
        b.ToTable("shared_ledger_links");

        // (user, member) is the key rather than (person, member): it is exactly the invariant worth
        // making unrepresentable — one member belongs to at most one person, per user. Storing a
        // surrogate id would let the same member sit in two people at once.
        b.HasKey(x => new { x.UserId, x.MemberId });
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Members are soft-deleted, so this cascade fires only on a real row removal; a pairing to a
        // member nobody can see any more is dead weight either way.
        b.HasOne<GroupMember>().WithMany()
            .HasForeignKey(x => x.MemberId)
            .OnDelete(DeleteBehavior.Cascade);

        // Reading a person means gathering its members, so the person id is the hot lookup.
        b.HasIndex(x => new { x.UserId, x.PersonId })
            .HasDatabaseName("ix_shared_ledger_links_person");
    }
}

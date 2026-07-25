using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class AuthIdentityConfiguration : IEntityTypeConfiguration<AuthIdentity>
{
    public void Configure(EntityTypeBuilder<AuthIdentity> b)
    {
        b.ToTable("auth_identities");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Provider).IsRequired();
        b.Property(x => x.Subject).IsRequired();
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.Provider, x.Subject })
            .IsUnique()
            .HasDatabaseName("uq_auth_identities_provider_subject");
    }
}

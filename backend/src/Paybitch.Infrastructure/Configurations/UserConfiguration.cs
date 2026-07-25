using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.DisplayName).IsRequired();
        b.Property(x => x.Email).HasColumnType("citext");
        b.Property(x => x.DefaultCurrency).HasMaxLength(3).IsFixedLength().HasDefaultValue("CZK");
        b.Property(x => x.Locale).HasDefaultValue("cs");
        b.Property(x => x.TokenEpoch).HasDefaultValue(0);
        b.Property(x => x.DigestOptIn).HasDefaultValue(false);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasOne<Currency>().WithMany()
            .HasForeignKey(x => x.DefaultCurrency)
            .OnDelete(DeleteBehavior.Restrict);

        // E6: email is a non-authoritative contact attribute — indexed, NOT unique.
        b.HasIndex(x => x.Email)
            .HasDatabaseName("ix_users_email")
            .HasFilter("email IS NOT NULL AND deleted_at IS NULL");
    }
}

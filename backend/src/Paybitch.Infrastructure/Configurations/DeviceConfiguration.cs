using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("devices", t =>
            t.HasCheckConstraint("ck_devices_kind", "kind IN ('apns','apns_live_activity')"));

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();   // CLIENT-generated stable device UUID
        b.Property(x => x.ApnsToken).IsRequired();
        b.Property(x => x.Platform).HasDefaultValue("ios");
        b.Property(x => x.Kind).HasDefaultValue("apns");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        b.HasOne<User>().WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.UserId, x.ApnsToken })
            .IsUnique()
            .HasDatabaseName("uq_devices_user_apns_token");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure.Configurations;

public sealed class NotificationCursorConfiguration : IEntityTypeConfiguration<NotificationCursor>
{
    public void Configure(EntityTypeBuilder<NotificationCursor> b)
    {
        b.ToTable("notification_cursor");

        b.HasKey(x => x.Worker);
        b.Property(x => x.Worker).ValueGeneratedNever();
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
    }
}

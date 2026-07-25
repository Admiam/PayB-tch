using Microsoft.EntityFrameworkCore;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure;

/// <summary>
/// The single schema spine for Paybitch. Snake_case mapping is applied via
/// <c>UseSnakeCaseNamingConvention()</c> on the options (see DI / design-time factory).
/// Optimistic concurrency (Version) is owned by handlers — never auto-bumped here.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // --- v1 tables ---
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuthIdentity> AuthIdentities => Set<AuthIdentity>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseSplit> ExpenseSplits => Set<ExpenseSplit>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<ActivityLogEntry> ActivityLog => Set<ActivityLogEntry>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<ChangeLogEntry> ChangeLog => Set<ChangeLogEntry>();
    public DbSet<ChangeLogWatermark> ChangeLogWatermarks => Set<ChangeLogWatermark>();

    // --- extension tables ---
    public DbSet<Job> Jobs => Set<Job>();                              // E0
    public DbSet<EmailSuppression> EmailSuppressions => Set<EmailSuppression>();  // E0
    public DbSet<RecurringRule> RecurringRules => Set<RecurringRule>();           // E3
    public DbSet<RecurringRuleSplit> RecurringRuleSplits => Set<RecurringRuleSplit>();  // E3
    public DbSet<ExportResult> ExportResults => Set<ExportResult>();             // E4
    public DbSet<NotificationPref> NotificationPrefs => Set<NotificationPref>(); // E5
    public DbSet<NotificationCursor> NotificationCursors => Set<NotificationCursor>();  // E5
    public DbSet<EmailLoginToken> EmailLoginTokens => Set<EmailLoginToken>();    // E6
    public DbSet<BlobDeletion> BlobDeletions => Set<BlobDeletion>();             // E7
    public DbSet<RateLimitCounter> RateLimitCounters => Set<RateLimitCounter>(); // E8
    public DbSet<Comment> Comments => Set<Comment>();                           // E10a

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Stamp UpdatedAt on inserted/modified rows implementing <see cref="IHasTimestamps"/>.</summary>
    private void StampTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IHasTimestamps>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }
    }
}

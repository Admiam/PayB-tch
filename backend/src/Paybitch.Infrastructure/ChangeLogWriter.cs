using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Infrastructure;

/// <summary>
/// Adds change_log rows to the tracked DbContext so they commit in the caller's transaction.
/// seq (bigserial) and created_at are DB-generated.
/// </summary>
public sealed class ChangeLogWriter(AppDbContext db) : IChangeLogWriter
{
    public void Append(Guid groupId, string entityType, Guid entityId, bool isDelete)
        => db.ChangeLog.Add(new ChangeLogEntry
        {
            GroupId = groupId,
            EntityType = entityType,
            EntityId = entityId,
            IsDelete = isDelete,
        });

    public void AppendAccess(Guid groupId, Guid userId, bool isRevoke)
        => Append(groupId, ChangeLogEntityTypes.Access, userId, isRevoke);
}

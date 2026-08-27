using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Options;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Activity;

/// <summary>
/// The write seam every mutating handler calls to emit a §3.10 activity row — in the SAME transaction
/// / DbContext as its mutation (and its <c>change_log</c> append). The caller owns SaveChanges, exactly
/// like <see cref="IChangeLogWriter"/>. One row per user-visible event; an idempotent create replay
/// (D9) must write NO second row (the caller decides that, not this writer).
/// </summary>
public interface IActivityWriter
{
    /// <summary>
    /// Append an activity row. <paramref name="actorUserId"/> is the acting <c>users.id</c> (null for a
    /// system action). <paramref name="metadata"/> holds ids + field NAMES only — never amounts or
    /// other money values (§3.10 / §4.3); it is serialized to jsonb and capped (Appendix A).
    /// </summary>
    void Write(
        Guid groupId,
        Guid? actorUserId,
        string verb,
        string targetType,
        Guid? targetId,
        object? metadata = null);
}

/// <summary>
/// <see cref="IActivityWriter"/> backed by the request-scoped <see cref="AppDbContext"/>. Registered
/// scoped (see <see cref="ActivityFeatureExtensions"/>); adds a tracked <see cref="ActivityLogEntry"/>
/// that commits with the caller's transaction.
/// </summary>
public sealed class ActivityWriter(AppDbContext db, IClock clock, IOptions<OperationalConstants> options) : IActivityWriter
{
    public void Write(
        Guid groupId,
        Guid? actorUserId,
        string verb,
        string targetType,
        Guid? targetId,
        object? metadata = null)
    {
        var json = SerializeMetadata(metadata);

        db.ActivityLog.Add(new ActivityLogEntry
        {
            Id = Guid.CreateVersion7(),
            GroupId = groupId,
            ActorUser = actorUserId,
            Verb = verb,
            TargetType = targetType,
            TargetId = targetId,
            Metadata = json,
            CreatedAt = clock.UtcNow,
        });
    }

    private string? SerializeMetadata(object? metadata)
    {
        if (metadata is null)
            return null;

        var json = JsonSerializer.Serialize(metadata);

        // metadata is server-written (§3.4): the ≤ 4 KB cap is an app-time invariant, not a 422. Being
        // field-name lists it is always tiny; overflow means a caller bug, so fail loudly.
        var bytes = Encoding.UTF8.GetByteCount(json);
        var cap = options.Value.ActivityLogMetadataCapBytes;
        if (bytes > cap)
            throw new InvalidOperationException(
                $"activity_log metadata is {bytes} bytes, exceeding the {cap}-byte cap (§3.10 / Appendix A).");

        return json;
    }
}

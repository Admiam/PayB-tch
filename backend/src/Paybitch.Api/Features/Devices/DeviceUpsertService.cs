using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Options;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Devices;

/// <summary>
/// The <c>PUT /me/devices/{id}</c> upsert (§4.5) — one transaction resolving BOTH conflict axes so
/// exactly one account receives pushes per physical device:
/// <list type="number">
///   <item><b>Token absorb:</b> delete any existing row — any user's — holding the same <c>apns_token</c>
///     (except this exact <c>{id}</c>+caller row), so a regenerated Keychain UUID can't 500 on
///     <c>UNIQUE (user_id, apns_token)</c>.</item>
///   <item><b>Id upsert:</b> a row with the same <c>{id}</c> is taken over by the caller and refreshed;
///     otherwise a new row is inserted, evicting the least-recently-updated when the per-user cap
///     (Appendix A) is already reached — registration never fails on the cap.</item>
/// </list>
/// </summary>
public sealed class DeviceUpsertService(AppDbContext db, IClock clock, IOptions<OperationalConstants> ops)
{
    public async Task<Device> UpsertAsync(Guid userId, Guid deviceId, RegisterDeviceRequest request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var apnsToken = request.ApnsToken!;
        var platform = string.IsNullOrWhiteSpace(request.Platform) ? "ios" : request.Platform!;
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? "apns" : request.Kind!;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. Absorb any stale row (any user) carrying this token, but not this exact target row.
        await db.Devices
            .Where(d => d.ApnsToken == apnsToken && !(d.Id == deviceId && d.UserId == userId))
            .ExecuteDeleteAsync(ct);

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is not null)
        {
            // 2a. Take over / refresh the existing row keyed on the client device UUID.
            device.UserId = userId;
            device.ApnsToken = apnsToken;
            device.Platform = platform;
            device.Kind = kind;
            device.UpdatedAt = now;
        }
        else
        {
            // 2b. New row — enforce the per-user device cap by evicting the LRU first (never fail).
            var count = await db.Devices.CountAsync(d => d.UserId == userId, ct);
            if (count >= ops.Value.DevicesPerUser)
            {
                var lruId = await db.Devices
                    .Where(d => d.UserId == userId)
                    .OrderBy(d => d.UpdatedAt)
                    .Select(d => d.Id)
                    .FirstAsync(ct);
                await db.Devices.Where(d => d.Id == lruId).ExecuteDeleteAsync(ct);
            }

            device = new Device
            {
                Id = deviceId,
                UserId = userId,
                ApnsToken = apnsToken,
                Platform = platform,
                Kind = kind,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Devices.Add(device);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return device;
    }
}

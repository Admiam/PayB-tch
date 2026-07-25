using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Common.Validation;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Devices;

/// <summary>
/// Device lifecycle (§3.1, §4.5): idempotent APNs-token upsert keyed on the client-generated stable
/// device UUID, and unregister (push opt-out). Account-deletion and logout-driven device purges are
/// owned by the auth/GDPR slice (§4.5 coordination table).
/// </summary>
public sealed class DevicesModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPut("/me/devices/{id:guid}", UpsertAsync)
            .WithValidation()
            .WithName("UpsertDevice")
            .WithSummary("Register or refresh a device's APNs token (idempotent upsert).")
            .WithTags("Devices");

        app.MapDelete("/me/devices/{id:guid}", DeleteAsync)
            .WithName("DeleteDevice")
            .WithSummary("Unregister a device (push opt-out).")
            .WithTags("Devices");
    }

    private static async Task<IResult> UpsertAsync(
        Guid id,
        RegisterDeviceRequest request,
        AppDbContext db,
        IClock clock,
        IOptions<OperationalConstants> ops,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var device = await new DeviceUpsertService(db, clock, ops)
            .UpsertAsync(currentUser.UserId, id, request, ct);

        return Results.Ok(new DeviceResponse(
            device.Id.ToString(), device.ApnsToken, device.Platform, device.Kind, device.UpdatedAt));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        // Idempotent: a missing row is still a 204 (push opt-out is a desired end-state, not a resource fetch).
        await db.Devices.Where(d => d.Id == id && d.UserId == currentUser.UserId).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}

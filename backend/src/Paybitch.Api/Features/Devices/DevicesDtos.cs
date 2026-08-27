namespace Paybitch.Api.Features.Devices;

/// <summary>
/// <c>PUT /me/devices/{id}</c> body (§4.5). <c>{id}</c> is the client-generated stable device UUID
/// (route). <see cref="Platform"/> defaults to <c>ios</c>, <see cref="Kind"/> to <c>apns</c>.
/// </summary>
public sealed record RegisterDeviceRequest(string? ApnsToken, string? Platform, string? Kind);

/// <summary>The upserted device row echoed back to the client.</summary>
public sealed record DeviceResponse(
    string Id,
    string ApnsToken,
    string Platform,
    string Kind,
    DateTimeOffset UpdatedAt);

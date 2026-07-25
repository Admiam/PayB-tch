using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Paybitch.Api.Common.Auth;

/// <summary>The authenticated caller, resolved from the validated access-token claims (<c>sub</c>, <c>epoch</c>).</summary>
public interface ICurrentUser
{
    /// <summary>True when the request carries a validated access token.</summary>
    bool IsAuthenticated { get; }

    /// <summary><c>users.id</c> from the <c>sub</c> claim. Throws when unauthenticated — safe on endpoints behind auth.</summary>
    Guid UserId { get; }

    /// <summary>The token's <c>epoch</c> claim (§4.1); 0 when unauthenticated.</summary>
    long Epoch { get; }
}

/// <summary>
/// <see cref="ICurrentUser"/> backed by <see cref="IHttpContextAccessor"/>. Registered scoped; reads
/// claims placed by JwtBearer after signature + epoch validation.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid UserId =>
        accessor.HttpContext?.TryGetUserId(out var id) == true
            ? id
            : throw new InvalidOperationException("No authenticated user on the current request.");

    public long Epoch =>
        Principal?.FindFirst(AuthClaims.Epoch)?.Value is { } raw
        && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            ? epoch
            : 0;
}

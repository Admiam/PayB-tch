using Microsoft.AspNetCore.Http;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Common.Auth;

/// <summary>Handler-side conveniences for the current user and the stashed group membership.</summary>
public static class HttpContextAuthExtensions
{
    internal const string MembershipItemKey = "paybitch.membership";

    /// <summary>The authenticated <c>users.id</c> (<c>sub</c> claim). Throws when unauthenticated.</summary>
    public static Guid GetUserId(this HttpContext context)
        => context.TryGetUserId(out var id)
            ? id
            : throw new InvalidOperationException("No authenticated user on the current request.");

    /// <summary>Try to read the authenticated <c>users.id</c> from the <c>sub</c> claim.</summary>
    public static bool TryGetUserId(this HttpContext context, out Guid userId)
    {
        userId = Guid.Empty;
        var sub = context.User.FindFirst(AuthClaims.Subject)?.Value;
        return sub is not null && Guid.TryParse(sub, out userId);
    }

    /// <summary>
    /// The <see cref="MembershipInfo"/> stashed by <c>.RequireGroupMembership()</c> /
    /// <c>.RequireGroupAdmin()</c> (§4.2). Throws if no membership filter ran — a handler that calls
    /// this must be behind one of those filters.
    /// </summary>
    public static MembershipInfo GetMembership(this HttpContext context)
        => context.Items[MembershipItemKey] as MembershipInfo
           ?? throw new InvalidOperationException(
               "No membership on the request — apply .RequireGroupMembership() or .RequireGroupAdmin() to this endpoint.");
}

using Microsoft.AspNetCore.Http;
using Paybitch.Api.Common.Errors;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Common.Auth;

/// <summary>
/// The single group authorization path (D6, §4.2) as an endpoint filter. Reads <c>{groupId}</c> from
/// the route, runs the indexed membership lookup, and:
/// <list type="bullet">
///   <item>not a member ⇒ <see cref="Problems.NotFound"/> (404, never 403/409 — no existence leak);</item>
///   <item>admin variant + role not <c>owner</c>/<c>admin</c> ⇒ <see cref="Problems.Forbidden"/> (403 <c>insufficient_role</c>);</item>
///   <item>otherwise stashes <see cref="MembershipInfo"/> in <c>HttpContext.Items</c> for the handler
///     (<see cref="HttpContextAuthExtensions.GetMembership"/>).</item>
/// </list>
/// The filter is stateless apart from the admin flag; it resolves <see cref="IGroupAccess"/> per call.
/// </summary>
public sealed class RequireMembershipFilter(bool adminOnly, string routeParam = "groupId") : IEndpointFilter
{
    private static readonly HashSet<string> AdminRoles = new(StringComparer.Ordinal) { "owner", "admin" };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (!http.TryGetUserId(out var userId))
            return Problems.Unauthenticated();

        if (http.Request.RouteValues[routeParam] is not string raw || !Guid.TryParse(raw, out var groupId))
            return Problems.NotFound(); // absent/malformed id is indistinguishable from "not a member" (D6)

        var groupAccess = http.RequestServices.GetRequiredService<IGroupAccess>();
        var membership = await groupAccess.GetMembershipAsync(groupId, userId, http.RequestAborted);
        if (membership is null)
            return Problems.NotFound();

        if (adminOnly && !AdminRoles.Contains(membership.Role))
            return Problems.Forbidden(ProblemCodes.InsufficientRole);

        http.Items[HttpContextAuthExtensions.MembershipItemKey] = membership;
        return await next(context);
    }
}

/// <summary>Endpoint-builder helpers that attach the D6 membership filter.</summary>
public static class MembershipFilterExtensions
{
    /// <summary>Require the caller be an active member of <c>{groupId}</c> (404 otherwise).</summary>
    public static TBuilder RequireGroupMembership<TBuilder>(this TBuilder builder, string routeParam = "groupId")
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new RequireMembershipFilter(adminOnly: false, routeParam));

    /// <summary>Require the caller be an <c>owner</c>/<c>admin</c> of <c>{groupId}</c> (404 non-member, 403 member without role).</summary>
    public static TBuilder RequireGroupAdmin<TBuilder>(this TBuilder builder, string routeParam = "groupId")
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new RequireMembershipFilter(adminOnly: true, routeParam));
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paybitch.Api.Common.Errors;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Groups.Support;

/// <summary>
/// The §3.7 archived-group write guard as an endpoint filter. Runs AFTER the D6 membership filter
/// (so it only ever sees a proven member) and rejects every mutation under an archived group with
/// <c>409 group_archived</c>. One code path, no per-handler drift.
///
/// Deliberately NOT applied to the token-authorized invite routes (<c>POST /invites/{token}/accept</c>)
/// — those check archived status inside the handler (§3.7), because no D6 filter runs there.
/// </summary>
public sealed class NotArchivedFilter(string routeParam = "groupId") : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (http.Request.RouteValues[routeParam] is string raw && Guid.TryParse(raw, out var groupId))
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var archivedAt = await db.Groups
                .Where(g => g.Id == groupId)
                .Select(g => g.ArchivedAt)
                .FirstOrDefaultAsync(http.RequestAborted);

            if (archivedAt is not null)
                return GroupErrors.GroupArchived();
        }

        return await next(context);
    }
}

/// <summary>Endpoint-builder helper attaching the §3.7 archived-write guard.</summary>
public static class NotArchivedFilterExtensions
{
    /// <summary>Reject any mutation under an archived <c>{groupId}</c> with <c>409 group_archived</c>.</summary>
    public static TBuilder RequireGroupNotArchived<TBuilder>(this TBuilder builder, string routeParam = "groupId")
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new NotArchivedFilter(routeParam));
}

/// <summary>Problem shapes specific to this slice that carry a <c>detail</c> the generic helpers don't.</summary>
public static class GroupErrors
{
    public static IResult GroupArchived()
        => Problems.Create(
            StatusCodes.Status409Conflict,
            ProblemCodes.GroupArchived,
            detail: "Unarchive the group to make changes.");
}

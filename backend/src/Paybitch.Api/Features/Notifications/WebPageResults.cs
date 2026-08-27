using Microsoft.AspNetCore.Http;

namespace Paybitch.Api.Features.Notifications;

/// <summary>
/// Shared helpers for the unauthenticated HTML surfaces (unsubscribe, invite landing). Every page is
/// <c>noindex</c> + <c>Referrer-Policy: no-referrer</c> + <c>no-store</c> so a token in the URL can't be
/// crawled, leaked via <c>Referer</c>, or cached (EXT-D9a-c / §5.7).
/// </summary>
public static class WebPageResults
{
    public static IResult Html(HttpResponse response, string html, int statusCode = StatusCodes.Status200OK)
    {
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        response.Headers.CacheControl = "no-store, max-age=0";
        response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Content(html, "text/html; charset=utf-8", contentEncoding: null, statusCode: statusCode);
    }
}

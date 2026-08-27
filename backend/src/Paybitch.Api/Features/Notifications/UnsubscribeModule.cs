using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.RateLimiting;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Notifications;

/// <summary>
/// One-click digest unsubscribe (EXT-D5h, §5.4/§5.7). Unauthenticated, signed-token only.
/// <list type="bullet">
///   <item><c>GET /unsubscribe?token=</c> is a SAFE, no-state confirmation page — so Gmail/Apple
///     link-prefetch bots can't silently opt everyone out.</item>
///   <item><c>POST /unsubscribe?token=</c> (RFC 8058 <c>List-Unsubscribe-Post</c>) performs the opt-out:
///     it sets <c>users.digest_opt_in = false</c> and NEVER writes <c>email_suppressions</c> (EXT-D5i —
///     a forged/valid unsubscribe can't suppress a victim's transactional mail). Idempotent.</item>
/// </list>
/// Enumeration-safe (X6): an invalid/tampered token and a valid one both render a uniform page that
/// reveals no address or account existence.
/// </summary>
public sealed class UnsubscribeModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/unsubscribe", GetAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.InvitePreview)
            .WithName("UnsubscribeConfirm")
            .WithSummary("Digest unsubscribe confirmation page (no state change).")
            .WithTags("Notifications");

        app.MapPost("/unsubscribe", PostAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.InvitePreview)
            .WithName("UnsubscribeOneClick")
            .WithSummary("RFC 8058 one-click digest opt-out (sets digest_opt_in = false).")
            .WithTags("Notifications");
    }

    // --- GET /unsubscribe?token= (safe confirmation, no state change) ---
    private static IResult GetAsync(
        HttpContext http, string? token, IConfiguration config, ILoggerFactory loggerFactory)
    {
        var secrets = new NotificationSecrets(config, loggerFactory.CreateLogger<NotificationSecrets>());
        var payload = UnsubscribeToken.Verify(secrets, token);
        if (payload is null)
            return WebPageResults.Html(http.Response, UnsubscribePages.Invalid(), StatusCodes.Status400BadRequest);

        // No state change on GET (prefetch-safe): render a form that POSTs the same token back.
        return WebPageResults.Html(http.Response, UnsubscribePages.Confirm(token!));
    }

    // --- POST /unsubscribe?token= (the actual opt-out) ---
    private static async Task<IResult> PostAsync(
        HttpContext http, string? token, AppDbContext db, IConfiguration config,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var secrets = new NotificationSecrets(config, loggerFactory.CreateLogger<NotificationSecrets>());
        var payload = UnsubscribeToken.Verify(secrets, token);
        if (payload is null)
            return WebPageResults.Html(http.Response, UnsubscribePages.Invalid(), StatusCodes.Status400BadRequest);

        // Consent flip ONLY — never an email_suppressions write (EXT-D5i). Idempotent; a missing user is
        // still a 200 (X6: no existence oracle).
        await db.Users
            .Where(u => u.Id == payload.UserId && u.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DigestOptIn, false), ct);

        return WebPageResults.Html(http.Response, UnsubscribePages.Done());
    }
}

/// <summary>Minimal static HTML for the unsubscribe surface — no user-controlled content to escape.</summary>
internal static class UnsubscribePages
{
    public static string Confirm(string token) =>
        Page("Unsubscribe from Paybitch digests",
            $"""
             <p>Click below to stop receiving Paybitch summary digest emails. Your account and transactional
             emails (sign-in links, invites) are unaffected.</p>
             <form method="post" action="/v1/unsubscribe?token={System.Net.WebUtility.HtmlEncode(token)}">
               <input type="hidden" name="List-Unsubscribe" value="One-Click" />
               <button type="submit">Unsubscribe</button>
             </form>
             """);

    public static string Done() =>
        Page("You're unsubscribed",
            "<p>You will no longer receive Paybitch digest emails. Sign-in links and invites still work.</p>");

    public static string Invalid() =>
        Page("Link unavailable",
            "<p>This unsubscribe link is invalid or has expired.</p>");

    private static string Page(string title, string body) =>
        $"""
         <!doctype html>
         <html lang="en"><head><meta charset="utf-8" />
         <meta name="viewport" content="width=device-width, initial-scale=1" />
         <meta name="robots" content="noindex, nofollow" />
         <title>{System.Net.WebUtility.HtmlEncode(title)}</title></head>
         <body style="font-family:system-ui,sans-serif;max-width:32rem;margin:3rem auto;padding:0 1rem">
         <h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>
         {body}
         </body></html>
         """;
}

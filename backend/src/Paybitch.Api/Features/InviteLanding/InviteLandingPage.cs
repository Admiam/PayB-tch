using System.Globalization;
using System.Net;

namespace Paybitch.Api.Features.InviteLanding;

/// <summary>
/// Renders the E9a landing HTML (EXT-D9a-c). One tiny static page served by the same .NET app — no new
/// service. Every user-controlled value (group/inviter name) is HTML-escaped (stored-XSS defense); the
/// unavailable page is a single constant so revoked/expired/unknown are byte-identical (no oracle).
/// </summary>
public static class InviteLandingPage
{
    public static string Preview(string groupName, string? inviterName, DateTimeOffset expiresAt, string storeUrl)
    {
        var group = WebUtility.HtmlEncode(groupName);
        var inviter = WebUtility.HtmlEncode(inviterName ?? "Someone");
        var expiry = WebUtility.HtmlEncode(expiresAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var store = WebUtility.HtmlEncode(storeUrl);

        var body =
            $"""
             <h1>You're invited to <em>{group}</em></h1>
             <p>{inviter} invited you to join the group <strong>{group}</strong> on Paybitch.</p>
             <p class="muted">This invite is valid until {expiry}.</p>
             <p><a class="cta" href="{store}">Get Paybitch</a></p>
             <p class="muted">Open this link on your iPhone with Paybitch installed to jump straight into the group.</p>
             """;
        return Shell("You're invited to Paybitch", body);
    }

    public static string Unavailable(string storeUrl)
    {
        var store = WebUtility.HtmlEncode(storeUrl);
        var body =
            $"""
             <h1>Invite unavailable</h1>
             <p>This invite link is no longer valid. Ask whoever invited you to send a new one.</p>
             <p><a class="cta" href="{store}">Get Paybitch</a></p>
             """;
        return Shell("Invite unavailable", body);
    }

    // Plain (non-interpolated) CSS so the braces are literal — injected as an interpolation hole below,
    // which keeps the wrapping interpolated raw string free of any literal-brace ambiguity.
    private const string Css =
        """
        body{font-family:system-ui,-apple-system,sans-serif;max-width:32rem;margin:3rem auto;padding:0 1.25rem;color:#111;line-height:1.5}
        h1{font-size:1.6rem;margin:0 0 1rem}
        .muted{color:#666;font-size:.95rem}
        .cta{display:inline-block;padding:.7rem 1.2rem;background:#111;color:#fff;text-decoration:none;border-radius:8px;font-weight:600}
        @media (prefers-color-scheme:dark){body{background:#111;color:#eee}.muted{color:#aaa}.cta{background:#fff;color:#111}}
        """;

    private static string Shell(string title, string body) =>
        $"""
         <!doctype html>
         <html lang="en"><head>
         <meta charset="utf-8" />
         <meta name="viewport" content="width=device-width, initial-scale=1" />
         <meta name="robots" content="noindex, nofollow" />
         <meta name="referrer" content="no-referrer" />
         <title>{WebUtility.HtmlEncode(title)}</title>
         <style>{Css}</style>
         </head>
         <body>{body}</body></html>
         """;
}

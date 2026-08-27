using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.RateLimiting;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Api.Features.Notifications;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.InviteLanding;

/// <summary>
/// E9a — the unauthenticated invite LANDING page (EXT-D9a-*, §5). A public, crawlable, shareable URL, so
/// it is deliberately NARROWER than the §3.8.1 JSON preview: group name + inviter + expiry + an App Store
/// button ONLY — no member list, no balances, no ghost nets (D6 tightened). Resolves the v1 invite token
/// by hash (never logged); unknown / expired / revoked all render one byte-identical "invite unavailable"
/// page (no oracle). <c>noindex</c> + <c>Referrer-Policy: no-referrer</c>; user-controlled names escaped.
/// </summary>
public sealed class InviteLandingModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/i/{token}", LandingAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.InvitePreview)
            .WithName("InviteLanding")
            .WithSummary("Unauthenticated invite landing page (HTML).")
            .WithTags("InviteLanding");
    }

    private static async Task<IResult> LandingAsync(
        string token, HttpContext http, AppDbContext db, IClock clock, IConfiguration config, CancellationToken ct)
    {
        var storeUrl = config["App:AppStoreUrl"] ?? "https://apps.apple.com/app/paybitch";

        // Resolve by hash — the token itself is never logged or echoed (EXT-D5f).
        var hash = InviteTokens.Hash(token);
        var invite = await db.Invites.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);

        // Unknown / expired: uniform unavailable page (no oracle). §3.12 already makes revoked ≡ never-issued.
        if (invite is null || invite.ExpiresAt <= clock.UtcNow)
            return WebPageResults.Html(http.Response, InviteLandingPage.Unavailable(storeUrl));

        var group = await db.Groups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == invite.GroupId && g.DeletedAt == null, ct);
        if (group is null)
            return WebPageResults.Html(http.Response, InviteLandingPage.Unavailable(storeUrl));

        var inviterName = invite.InvitedBy is { } inviter
            ? await db.Users.AsNoTracking().Where(u => u.Id == inviter).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;

        var html = InviteLandingPage.Preview(group.Name, inviterName, invite.ExpiresAt, storeUrl);
        return WebPageResults.Html(http.Response, html);
    }
}

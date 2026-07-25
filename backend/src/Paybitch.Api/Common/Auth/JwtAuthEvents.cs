using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Common.Auth;

/// <summary>
/// JwtBearer event handlers: <c>token_epoch</c> enforcement (§4.1) after signature/lifetime
/// validation, and problem+json for challenge/forbidden so auth failures speak the same RFC 9457
/// contract as the rest of the API. The epoch is read through a per-instance <see cref="IMemoryCache"/>
/// (TTL ≤ 60 s, Appendix A) and the check is direction-aware to avoid the mobile-retry self-DoS.
/// </summary>
public static class JwtAuthEvents
{
    private const string ErrorCodeItemKey = "paybitch.auth_error_code";

    public static JwtBearerEvents Create() => new()
    {
        OnTokenValidated = OnTokenValidatedAsync,
        OnChallenge = OnChallengeAsync,
        OnForbidden = OnForbiddenAsync,
    };

    private static async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var http = context.HttpContext;

        if (context.Principal is not { } principal || !TryReadClaims(principal, out var userId, out var claimEpoch))
        {
            Reject(context, ProblemCodes.Unauthenticated);
            return;
        }

        var services = http.RequestServices;
        var cache = services.GetRequiredService<IMemoryCache>();
        var ttl = services.GetRequiredService<IOptions<OperationalConstants>>().Value.TokenEpochCacheTtl;
        var cacheKey = CacheKey(userId);

        var cached = cache.TryGetValue(cacheKey, out long value)
            ? value
            : Cache(cache, cacheKey, await ReadEpochAsync(services, userId, http.RequestAborted), ttl);

        if (claimEpoch < cached)
        {
            // logout-all / erase / theft response bumped the epoch — client refreshes.
            Reject(context, ProblemCodes.TokenEpochStale);
            return;
        }

        if (claimEpoch > cached)
        {
            // The cache is stale (a fresh token outran a not-yet-expired cache entry). Re-read live.
            var fresh = Cache(cache, cacheKey, await ReadEpochAsync(services, userId, http.RequestAborted), ttl);
            if (claimEpoch < fresh)
                Reject(context, ProblemCodes.TokenEpochStale);
        }
        // claimEpoch == cached (or == fresh): token is current.
    }

    private static Task OnChallengeAsync(JwtBearerChallengeContext context)
    {
        // Suppress the default WWW-Authenticate challenge; emit problem+json instead.
        context.HandleResponse();
        var code = context.HttpContext.Items[ErrorCodeItemKey] as string ?? ProblemCodes.Unauthenticated;
        return Problems.Unauthenticated(code).ExecuteAsync(context.HttpContext);
    }

    private static Task OnForbiddenAsync(ForbiddenContext context)
        => Problems.Forbidden(ProblemCodes.InsufficientRole).ExecuteAsync(context.HttpContext);

    private static void Reject(TokenValidatedContext context, string code)
    {
        context.HttpContext.Items[ErrorCodeItemKey] = code;
        context.Fail(code);
    }

    private static bool TryReadClaims(ClaimsPrincipal principal, out Guid userId, out long epoch)
    {
        userId = Guid.Empty;
        epoch = 0;
        var sub = principal.FindFirst(AuthClaims.Subject)?.Value;
        var epochRaw = principal.FindFirst(AuthClaims.Epoch)?.Value;
        return sub is not null
            && Guid.TryParse(sub, out userId)
            && epochRaw is not null
            && long.TryParse(epochRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch);
    }

    private static async Task<long> ReadEpochAsync(IServiceProvider services, Guid userId, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var epoch = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => (long?)u.TokenEpoch)
            .FirstOrDefaultAsync(ct);
        // Missing user ⇒ force a stale verdict (any claim < MaxValue); refresh then fails too.
        return epoch ?? long.MaxValue;
    }

    private static long Cache(IMemoryCache cache, string key, long value, TimeSpan ttl)
    {
        cache.Set(key, value, ttl);
        return value;
    }

    private static string CacheKey(Guid userId) => $"token_epoch:{userId}";
}

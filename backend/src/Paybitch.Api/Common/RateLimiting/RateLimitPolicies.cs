using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;

namespace Paybitch.Api.Common.RateLimiting;

/// <summary>
/// Named ASP.NET Core rate-limiter policies (§4.3, Appendix A). A feature slice opts an endpoint in
/// with <c>.RequireRateLimiting(RateLimitPolicies.Auth)</c>. Partitions are keyed off the CLIENT ip
/// (resolved after <c>UseForwardedHeaders</c> — trusted-proxy config is mandatory, else the key is
/// spoofable) or the authenticated <c>sub</c>. In-memory ⇒ single-instance; move to a shared store
/// when scaling out (§4.3).
///
/// Note: provisioning's fine-grained per-Apple-<c>sub</c> cap is enforced in the auth handler against
/// the DB; the coarse per-IP policy here is the network-edge backstop.
/// </summary>
public static class RateLimitPolicies
{
    public const string Auth = "auth";                     // per IP — /auth/*
    public const string Provisioning = "provisioning";     // per IP — first /auth/apple per identity (backstop)
    public const string InviteCreate = "invite-create";    // per user — invite create
    public const string InvitePreview = "invite-preview";  // per IP — unauthenticated invite preview

    /// <summary>Register all named policies plus the shared 429 problem+json rejection writer.</summary>
    public static IServiceCollection AddApiRateLimiter(this IServiceCollection services, OperationalConstants ops)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra)
                    ? ra
                    : null;
                await Problems.TooManyRequests(retryAfter).ExecuteAsync(context.HttpContext);
            };

            options.AddPolicy(Auth, http =>
                FixedWindow(ClientIp(http), ops.RateLimitAuthPerIpPerMinute, TimeSpan.FromMinutes(1)));

            options.AddPolicy(Provisioning, http =>
                FixedWindow(ClientIp(http), ops.RateLimitProvisioningPerIdentityPerDay, TimeSpan.FromDays(1)));

            options.AddPolicy(InvitePreview, http =>
                FixedWindow(ClientIp(http), ops.RateLimitInvitePreviewPerIpPerMinute, TimeSpan.FromMinutes(1)));

            options.AddPolicy(InviteCreate, http =>
                FixedWindow(UserOrIp(http), ops.RateLimitInviteCreatePerUserPerDay, TimeSpan.FromDays(1)));
        });

        return services;
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permitLimit, TimeSpan window)
        => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });

    private static string ClientIp(HttpContext http)
        => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string UserOrIp(HttpContext http)
        => http.TryGetUserId(out var id) ? $"user:{id}" : $"ip:{ClientIp(http)}";
}

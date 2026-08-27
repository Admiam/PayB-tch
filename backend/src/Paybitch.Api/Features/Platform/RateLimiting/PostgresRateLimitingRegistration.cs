using Microsoft.Extensions.DependencyInjection;

namespace Paybitch.Api.Features.Platform.RateLimiting;

/// <summary>
/// Opt-in wiring for the E8 multi-instance rate limiter (EXT-D8b). <b>Not</b> called in v1: a single instance
/// is correct with the in-memory <c>System.Threading.RateLimiting</c> limiter (§4.3). Call this — and only
/// this — <b>before the second app instance serves traffic</b>, together with moving the <c>token_epoch</c>
/// read to the shared store (§4.1 "with the limiter, not before").
/// </summary>
/// <remarks>
/// <para><b>Swap-in — two lines, applied at convergence (do not commit them until EXT-D8b actually fires):</b></para>
/// <list type="number">
///   <item>
///     In <c>Program.cs</c>, after <c>AddApiRateLimiter(ops)</c>, add:
///     <code>builder.Services.AddPostgresRateLimiting(builder.Configuration);</code>
///     and supply the shared secret out-of-band: <c>RateLimiting__PartitionHmacKey=&lt;32+ char random&gt;</c>
///     (identical on every instance; boot fails fast if empty).
///   </item>
///   <item>
///     In <c>Common/RateLimiting/RateLimitPolicies.cs</c>, replace the in-memory partition factory
///     <code>FixedWindow(ClientIp(http), limit, window)</code>
///     with the distributed one, tagging each surface/dimension exactly as Appendix A partitions them:
///     <code>PostgresPartitionRateLimiter.For(http, "auth",     "ip",   ClientIp(http),  limit, window)</code>
///     <code>PostgresPartitionRateLimiter.For(http, "provision","ip",   ClientIp(http),  limit, window)</code>
///     <code>PostgresPartitionRateLimiter.For(http, "invite-preview","ip", ClientIp(http), limit, window)</code>
///     <code>PostgresPartitionRateLimiter.For(http, "invite-create","user", UserOrIp(http), limit, window)</code>
///     The <c>OnRejected</c> writer, the 429 <c>rate_limited</c> body, and <c>Retry-After</c> are unchanged —
///     the limiter surfaces <c>MetadataName.RetryAfter</c> exactly as the fixed-window limiter did.
///   </item>
/// </list>
/// <para>
/// The GC handler (<see cref="RateLimitGcHandler"/>) is auto-discovered by <c>AddPlatform</c> regardless; this
/// method only starts the scheduler that enqueues it, so the counter table stays empty until the swap is live.
/// </para>
/// </remarks>
public static class PostgresRateLimitingRegistration
{
    public static IServiceCollection AddPostgresRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        services
            .AddOptions<PostgresRateLimitOptions>()
            .Bind(config.GetSection(PostgresRateLimitOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.PartitionHmacKey),
                "RateLimiting:PartitionHmacKey is required when the Postgres rate limiter is enabled (EXT-D8b) " +
                "and MUST be identical across all instances.")
            .Validate(
                o => o.GcRetention > TimeSpan.FromDays(1),
                "RateLimiting:GcRetention must exceed the widest Appendix A window (1 day) so live daily " +
                "windows are never pruned early.")
            .ValidateOnStart();

        services.AddSingleton<PostgresRateLimitStore>();
        services.AddHostedService<RateLimitGcScheduler>();
        return services;
    }
}

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Platform.RateLimiting;

/// <summary>
/// Adapts <see cref="PostgresRateLimitStore"/> to <c>System.Threading.RateLimiting</c> so the existing named
/// ASP.NET policies (<c>Common/RateLimiting/RateLimitPolicies.cs</c>) can be pointed at the distributed store
/// with a one-expression swap (EXT-D8b). Instead of
/// <c>RateLimitPartition.GetFixedWindowLimiter(key, …)</c> (in-memory, per-instance), the policy factory
/// returns <see cref="For"/> — a partition backed by this limiter, whose window state lives in Postgres and
/// is therefore shared across every instance.
/// </summary>
/// <remarks>
/// The framework caches one limiter instance per partition key and drains idle ones via
/// <see cref="IdleDuration"/>; all real state is the DB row, so the cached instance is a tiny stateless shim.
/// The middleware acquires through <see cref="AcquireAsyncCore"/> (DB is async-only); the synchronous
/// <see cref="AttemptAcquireCore"/> path — which the ASP.NET rate-limiting middleware does not use — fails
/// <b>open</b> so an unexpected sync call degrades to v1's no-limit behaviour rather than 429-ing everything.
/// </remarks>
public static class PostgresPartitionRateLimiter
{
    /// <summary>
    /// Build a distributed replacement for a fixed-window partition. Drop-in for the v1
    /// <c>FixedWindow(key, limit, window)</c> helper: pass the same client key (trusted-proxy-resolved IP or
    /// <c>user:{id}</c>) plus the surface/dimension tags used to namespace it in the shared counter table.
    /// </summary>
    public static RateLimitPartition<string> For(
        HttpContext http, string surface, string dimension, string value, int permitLimit, TimeSpan window)
    {
        var partitionKey = $"{surface}:{dimension}:{value}";
        var store = http.RequestServices.GetRequiredService<PostgresRateLimitStore>();
        var clock = http.RequestServices.GetRequiredService<IClock>();
        return RateLimitPartition.Get(
            partitionKey,
            key => new Limiter(store, clock, key, permitLimit, window));
    }

    private sealed class Limiter(
        PostgresRateLimitStore store, IClock clock, string partitionKey, int permitLimit, TimeSpan window)
        : RateLimiter
    {
        private DateTimeOffset _lastUse = clock.UtcNow;

        // Lets the framework evict this shim once its partition key goes quiet (state is in the DB regardless).
        public override TimeSpan? IdleDuration => clock.UtcNow - _lastUse;

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken ct)
        {
            _lastUse = clock.UtcNow;
            var decision = await store.AcquireAsync(partitionKey, permitLimit, window, ct);
            return new Lease(decision.IsAllowed, decision.RetryAfter);
        }

        // Not on the ASP.NET middleware path; fail open (see class remarks).
        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            _lastUse = clock.UtcNow;
            return new Lease(isAcquired: true, retryAfter: null);
        }

        protected override void Dispose(bool disposing) { }
    }

    private sealed class Lease(bool isAcquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => isAcquired;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? Enumerable.Empty<string>() : new[] { MetadataName.RetryAfter.Name };

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing) { }
    }
}

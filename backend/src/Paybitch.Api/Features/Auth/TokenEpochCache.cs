using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Options;

namespace Paybitch.Api.Features.Auth;

/// <summary>
/// Writes an authoritative <c>token_epoch</c> value into the same per-instance <see cref="IMemoryCache"/>
/// that <c>JwtAuthEvents</c> reads on every request (§4.1: "any instance that bumps the epoch … writes
/// the value it just read/wrote into its own cache entry in the same operation"). Keeping the cache in
/// sync on a bump makes a single-instance deploy converge immediately instead of after the TTL.
///
/// ⚠️ Coordination: the cache-key shape (<c>token_epoch:{userId}</c>) mirrors
/// <c>JwtAuthEvents.CacheKey</c> (a private detail of a Common file this slice must not edit). If that
/// key ever changes, update it here too — correctness degrades gracefully to "stale until TTL", never
/// to a wrong verdict.
/// </summary>
public sealed class TokenEpochCache(IMemoryCache cache, IOptions<OperationalConstants> ops)
{
    private static string Key(Guid userId) => $"token_epoch:{userId}";

    /// <summary>Publish the just-written epoch for a user into this instance's cache (TTL per Appendix A).</summary>
    public void Set(Guid userId, long epoch)
        => cache.Set(Key(userId), epoch, ops.Value.TokenEpochCacheTtl);
}

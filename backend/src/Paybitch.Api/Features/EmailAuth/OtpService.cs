using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Platform.Email;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.EmailAuth;

/// <summary>The <c>email_login_tokens.purpose</c> values (§6.3 CHECK). Per-purpose binding stops a <c>login</c> code being replayed into a <c>link</c> (EXT-D6e).</summary>
internal static class OtpPurposes
{
    public const string Login = "login";
    public const string Link = "link";
    public const string VerifyChange = "verify_change";
}

/// <summary>Outcome of a verify: success carries the matched row's (normalized) email and its bound user.</summary>
internal sealed record OtpVerifyResult(bool Succeeded, string? Email, Guid? UserId)
{
    public static readonly OtpVerifyResult Failure = new(false, null, null);
    public static OtpVerifyResult Ok(string email, Guid? userId) => new(true, email, userId);
}

/// <summary>
/// The E6 OTP engine (§6.3/§6.7): mint (hashed-at-rest, TTL, purpose-bound) and verify (single-use
/// conditional consume + per-code attempt cap). Composed inline from DI primitives by the module — no
/// Program.cs registration — mirroring how <see cref="Paybitch.Api.Features.Auth.RefreshTokenService"/>
/// is assembled. It touches only <c>email_login_tokens</c> and enqueues the prune sweep; it never issues
/// sessions or provisions users (the module owns that after a successful verify).
/// </summary>
internal sealed class OtpService(
    AppDbContext db,
    IClock clock,
    EmailAuthConfig config,
    IEmailSender emailSender,
    IJobQueue jobs,
    int perEmailDailyMintCap,
    ILogger logger)
{
    /// <summary>
    /// Mint an OTP for <paramref name="normalizedEmail"/> and send it — the enumeration-safe workhorse
    /// behind every <c>…/start</c> (§6.7). Constant-shape: the caller ALWAYS returns
    /// <c>200 {"status":"sent"}</c> regardless of what happens here. Suppression is handled inside
    /// <see cref="IEmailSender.SendAsync"/> (hard-suppressed ⇒ silent no-op), never leaked.
    /// <paramref name="enforceDailyCap"/> gates only new-address provisioning (the <c>login</c> flow),
    /// mirroring §4.3's per-identity/day cap.
    /// </summary>
    public async Task StartAsync(
        string normalizedEmail, string purpose, Guid? userId, IPAddress? ip, string locale,
        bool enforceDailyCap, CancellationToken ct)
    {
        if (!config.HasConfiguredPepper)
            logger.LogWarning("EmailAuth:Pepper is not configured — OTP hashes are not offline-leak protected (dev only).");

        if (enforceDailyCap)
        {
            var since = clock.UtcNow - TimeSpan.FromDays(1);
            var minted = await db.EmailLoginTokens.CountAsync(
                t => t.Email == normalizedEmail && t.Purpose == purpose && t.CreatedAt >= since, ct);
            if (minted >= perEmailDailyMintCap)
            {
                // Provisioning/mint cap tripped — skip silently so existence stays unprobeable (EXT-D6f).
                logger.LogInformation("OTP mint cap reached for purpose {Purpose}; skipping send.", purpose);
                await EnqueuePruneAsync(ct);
                return;
            }
        }

        var code = GenerateCode();
        db.EmailLoginTokens.Add(new EmailLoginToken
        {
            Email = normalizedEmail,
            CodeHash = HashCode(code),
            Purpose = purpose,
            UserId = userId,
            ExpiresAt = clock.UtcNow + config.OtpTtl,
            CreatedIp = ip,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        var message = OtpEmailFactory.BuildCode(purpose, normalizedEmail, code, locale, config.UniversalLinkBase);
        await emailSender.SendAsync(message, ct); // silently no-ops when hard-suppressed (EXT-D5i)

        await EnqueuePruneAsync(ct);
    }

    /// <summary>
    /// Verify a presented code (§6.7). Scoped by <paramref name="email"/> (login) or
    /// <paramref name="boundUserId"/> (link/change). Success is an ATOMIC conditional consume
    /// (<c>UPDATE … WHERE consumed_at IS NULL</c>) so a double-submit / race yields exactly one winner;
    /// a wrong code burns an attempt on the newest live code and invalidates it at the cap. Every failure
    /// mode collapses to <see cref="OtpVerifyResult.Failure"/> — the caller maps it to the ONE uniform
    /// <c>401 invalid_or_expired_code</c> (EXT-D6f).
    /// </summary>
    public async Task<OtpVerifyResult> VerifyAsync(
        string purpose, string code, string? email, Guid? boundUserId, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var query = db.EmailLoginTokens
            .Where(t => t.Purpose == purpose && t.ConsumedAt == null && t.ExpiresAt > now);
        if (email is not null)
            query = query.Where(t => t.Email == email);
        if (boundUserId is not null)
            query = query.Where(t => t.UserId == boundUserId);

        var candidates = await query
            .OrderByDescending(t => t.CreatedAt)
            .Take(MaxCandidates)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return OtpVerifyResult.Failure;

        var presented = HashCode(code);
        var match = candidates.FirstOrDefault(t =>
            t.Attempts < config.MaxVerifyAttempts && CryptographicOperations.FixedTimeEquals(t.CodeHash, presented));

        if (match is not null)
        {
            var affected = await db.EmailLoginTokens
                .Where(t => t.Id == match.Id && t.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);
            return affected == 1 ? OtpVerifyResult.Ok(match.Email, match.UserId) : OtpVerifyResult.Failure;
        }

        // Wrong code: burn one attempt on the newest live code; invalidate it once the cap is reached.
        var newest = candidates[0];
        var nextAttempts = newest.Attempts + 1;
        if (nextAttempts >= config.MaxVerifyAttempts)
        {
            await db.EmailLoginTokens
                .Where(t => t.Id == newest.Id && t.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Attempts, nextAttempts)
                    .SetProperty(t => t.ConsumedAt, now), ct);
        }
        else
        {
            await db.EmailLoginTokens
                .Where(t => t.Id == newest.Id && t.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Attempts, nextAttempts), ct);
        }

        return OtpVerifyResult.Failure;
    }

    private Task EnqueuePruneAsync(CancellationToken ct)
        // Dedupe on the kind: at most one prune queued/running at a time. Traffic-driven pruning keeps
        // created_ip (PII) from outliving the TTL (§6.3); an E0 cron may also drive it (see convergence note).
        => jobs.EnqueueAsync(EmailAuthJobKinds.EmailTokenPrune, EmptyPayload,
            dedupeKey: EmailAuthJobKinds.EmailTokenPrune, runAt: null, ct);

    private static readonly object EmptyPayload = new();

    private const int MaxCandidates = 10; // bound the in-memory hash comparison per verify

    private static string GenerateCode()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    // SHA-256(pepper ‖ code) — EXT-D6e. The pepper (app secret) makes a DB-only leak non-reversible.
    private byte[] HashCode(string code)
    {
        var pepper = config.PepperBytes;
        var codeBytes = Encoding.UTF8.GetBytes(code);
        var buffer = new byte[pepper.Length + codeBytes.Length];
        Buffer.BlockCopy(pepper, 0, buffer, 0, pepper.Length);
        Buffer.BlockCopy(codeBytes, 0, buffer, pepper.Length, codeBytes.Length);
        return SHA256.HashData(buffer);
    }
}

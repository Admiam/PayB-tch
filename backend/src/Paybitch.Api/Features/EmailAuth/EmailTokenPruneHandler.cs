using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.EmailAuth;

/// <summary>
/// Drains the <c>email_token.prune</c> job (§6.3): an idempotent sweep that DELETEs OTP rows that are
/// past their TTL (plus a small grace) or already single-use-consumed, so a token's <c>created_ip</c>
/// (PII) never outlives its short life (§6.6). Auto-registered by the E0 assembly scan
/// (<c>PlatformServiceRegistration</c>) — no Program.cs edit. Safe to re-run: a second pass simply finds
/// nothing left to delete (at-least-once, X5).
/// </summary>
public sealed class EmailTokenPruneHandler(
    AppDbContext db,
    IClock clock,
    IConfiguration configuration,
    ILogger<EmailTokenPruneHandler> logger) : IJobHandler
{
    public string Kind => EmailAuthJobKinds.EmailTokenPrune;

    public async Task HandleAsync(JobContext job, CancellationToken ct)
    {
        var config = EmailAuthConfig.From(configuration);
        var cutoff = clock.UtcNow - config.PruneGrace;

        var deleted = await db.EmailLoginTokens
            .Where(t => t.ExpiresAt < cutoff || t.ConsumedAt != null)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
            logger.LogInformation("email_token.prune reclaimed {Count} expired/consumed OTP rows.", deleted);
    }
}

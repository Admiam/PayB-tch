using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Notifications;

/// <summary>
/// Provider bounce/complaint webhook (EXT-D5d/i, §5.4/§5.7). Unauthenticated but signature-gated: the
/// provider's signature is verified BEFORE any mutation, and the payload's email is never trusted without
/// it (else an unsigned request could suppress an arbitrary address = a targeted mail-DoS). A valid hard
/// event upserts <c>email_suppressions(email, reason)</c> — the ONLY population path for that table
/// besides manual admin (EXT-D5i). A digest opt-out never lands here.
/// </summary>
public sealed class EmailWebhookModule : IEndpointModule
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> HardReasons = new(StringComparer.OrdinalIgnoreCase) { "bounce", "complaint" };

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        // No IP rate-limiter here: bounce/complaint deliveries are batchy (would trip a per-IP window) and
        // the endpoint is already signature-gated. A dedicated high-ceiling partition is a convergence note.
        app.MapPost("/webhooks/email/{provider}", HandleAsync)
            .AllowAnonymous()
            .WithName("EmailProviderWebhook")
            .WithSummary("Bounce/complaint webhook → email_suppressions (provider-signature verified).")
            .WithTags("Notifications");
    }

    private static async Task<IResult> HandleAsync(
        string provider, HttpContext http, AppDbContext db, IClock clock,
        IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Paybitch.Notifications.EmailWebhook");

        // Buffer the raw body — the signature is over the exact bytes, so read once, verify, then parse.
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        var rawBody = buffer.ToArray();

        var secrets = new NotificationSecrets(config, loggerFactory.CreateLogger<NotificationSecrets>());
        var signature = http.Request.Headers[EmailWebhookVerifier.SignatureHeader].ToString();
        if (!EmailWebhookVerifier.Verify(secrets, rawBody, signature))
        {
            logger.LogWarning("Rejected unsigned/invalid {Provider} email webhook", provider);
            return Problems.Forbidden(ProblemCodes.InsufficientRole);
        }

        var events = ParseEvents(rawBody);
        var upserted = 0;
        foreach (var (email, reason) in events)
        {
            if (string.IsNullOrWhiteSpace(email) || !HardReasons.Contains(reason))
                continue;

            var normalizedReason = reason.ToLowerInvariant();
            var exists = await db.EmailSuppressions.AnyAsync(s => s.Email == email, ct);
            if (exists)
                continue; // citext PK: idempotent — first hard signal wins

            db.EmailSuppressions.Add(new EmailSuppression
            {
                Email = email.Trim(),
                Reason = normalizedReason,
                CreatedAt = clock.UtcNow,
            });
            upserted++;
        }

        if (upserted > 0)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent webhook won the citext PK race — the suppression exists either way (idempotent).
            }
        }

        return Results.Ok(new { received = events.Count, suppressed = upserted });
    }

    /// <summary>
    /// Extract (email, reason) pairs. Accepts a flat dev shape and an SNS <c>Message</c>-wrapped SES
    /// notification. Only <c>bounce</c>/<c>complaint</c> map to a hard suppression.
    /// </summary>
    private static IReadOnlyList<(string? Email, string Reason)> ParseEvents(byte[] rawBody)
    {
        var results = new List<(string?, string)>();
        try
        {
            var flat = JsonSerializer.Deserialize<FlatEvent>(rawBody, JsonOptions);
            if (flat is { Email: not null, Reason: not null })
                results.Add((flat.Email, flat.Reason));

            // SNS envelope: the SES notification JSON is a STRING in "Message".
            var envelope = JsonSerializer.Deserialize<SnsEnvelope>(rawBody, JsonOptions);
            if (!string.IsNullOrWhiteSpace(envelope?.Message))
            {
                var ses = JsonSerializer.Deserialize<SesNotification>(envelope.Message, JsonOptions);
                var reason = ses?.NotificationType?.ToLowerInvariant();
                if (reason is "bounce" or "complaint" && ses?.Mail?.Destination is { } dests)
                    foreach (var d in dests)
                        results.Add((d, reason));
            }
        }
        catch (JsonException)
        {
            // Unparseable body after a VALID signature: nothing to suppress, return empty (200, received=0).
        }

        return results;
    }

    private sealed record FlatEvent(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record SnsEnvelope([property: JsonPropertyName("Message")] string? Message);

    private sealed record SesNotification(
        [property: JsonPropertyName("notificationType")] string? NotificationType,
        [property: JsonPropertyName("mail")] SesMail? Mail);

    private sealed record SesMail([property: JsonPropertyName("destination")] List<string>? Destination);
}

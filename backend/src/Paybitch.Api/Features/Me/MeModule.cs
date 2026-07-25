using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Common.Validation;
using Paybitch.Api.Features.Auth;
using Paybitch.Api.Features.Auth.Apple;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Domain;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Me;

/// <summary>
/// The authenticated self-service surface (§3.1, §4.4): read/update the profile, GDPR export, and the
/// GDPR anonymize-delete. The heavy lifting lives in <see cref="AccountAnonymizer"/> and
/// <see cref="MeExportBuilder"/>, composed inline from DI primitives (no Program.cs registration).
/// </summary>
public sealed class MeModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/me", GetAsync)
            .WithName("GetMe").WithSummary("Get the authenticated user's profile.").WithTags("Me");

        app.MapPatch("/me", PatchAsync)
            .WithValidation()
            .WithName("UpdateMe").WithSummary("Update display name, locale, or default currency.").WithTags("Me");

        app.MapDelete("/me", DeleteAsync)
            .WithName("DeleteMe").WithSummary("GDPR account deletion (anonymize).").WithTags("Me");

        app.MapGet("/me/export", ExportAsync)
            .WithName("ExportMe").WithSummary("GDPR data export (Art. 15/20) — synchronous JSON dump.").WithTags("Me");
    }

    private static async Task<IResult> GetAsync(
        AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == currentUser.UserId && u.DeletedAt == null, ct);
        if (user is null)
            return Problems.NotFound();

        return Results.Ok(new MeResponse(
            user.Id.ToString(), user.DisplayName, user.Email,
            user.DefaultCurrency, user.Locale, user.CreatedAt));
    }

    private static async Task<IResult> PatchAsync(
        UpdateMeRequest request, AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        // §3.12 over-post guard: image-URL fields are reserved, not writable.
        if (request.AvatarUrl is not null || request.ImageUrl is not null)
            return Problems.Validation(ProblemCodes.ImmutableField, "avatarUrl", "Image URLs are not writable in v1.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.UserId && u.DeletedAt == null, ct);
        if (user is null)
            return Problems.NotFound();

        if (request.DisplayName is not null)
            user.DisplayName = request.DisplayName.Trim();

        if (request.Locale is not null)
            user.Locale = request.Locale.Trim();

        if (request.DefaultCurrency is not null)
        {
            try
            {
                user.DefaultCurrency = Currency.FromCode(request.DefaultCurrency).Code;
            }
            catch (UnsupportedCurrencyException)
            {
                return Problems.Validation(
                    ProblemCodes.UnsupportedCurrency, "defaultCurrency",
                    "Currency is not supported in v1.");
            }
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new MeResponse(
            user.Id.ToString(), user.DisplayName, user.Email,
            user.DefaultCurrency, user.Locale, user.CreatedAt));
    }

    private static async Task<IResult> ExportAsync(
        AppDbContext db, IClock clock, ICurrentUser currentUser, HttpResponse response, CancellationToken ct)
    {
        var payload = await new MeExportBuilder(db, clock).BuildAsync(currentUser.UserId, ct);
        if (payload is null)
            return Problems.NotFound();

        response.Headers.ContentDisposition = "attachment; filename=\"paybitch-export.json\"";
        return Results.Json(payload, contentType: "application/json");
    }

    private static async Task<IResult> DeleteAsync(
        AppDbContext db,
        IChangeLogWriter changeLog,
        IClock clock,
        IOptions<OperationalConstants> ops,
        IMemoryCache memoryCache,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IJobQueue jobQueue,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var epochCache = new TokenEpochCache(memoryCache, ops);
        var anonymizer = new AccountAnonymizer(db, changeLog, clock, epochCache, jobQueue);

        var outcome = await anonymizer.RunAsync(userId, ct);
        if (!outcome.Found)
            return Problems.NotFound();

        await TryRevokeAppleTokenAsync(db, configuration, clock, outcome, loggerFactory, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Step 9 (§4.4): a best-effort inline Apple revoke after commit. Durable retries are owned by the
    /// outbox worker (the queued <c>apple.revoke</c> job); a success here simply deletes that job, whose
    /// payload is itself credential material. Never throws into the response — the 204 already stands.
    /// </summary>
    private static async Task TryRevokeAppleTokenAsync(
        AppDbContext db, IConfiguration configuration, IClock clock,
        AnonymizeOutcome outcome, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (outcome.AppleRefreshTokenEnc is null)
            return;

        var appleOptions = AppleAuthOptions.From(configuration);
        var key = AppleTokenCipher.TryResolveKey(appleOptions);
        if (!appleOptions.CanExchangeCode || key is null)
            return; // worker will handle it from the outbox job

        var plaintext = AppleTokenCipher.Decrypt(outcome.AppleRefreshTokenEnc, key);
        if (plaintext is null)
            return;

        try
        {
            var exchange = new AppleCodeExchange(appleOptions, clock);
            if (await exchange.RevokeAsync(plaintext, ct) && outcome.RevokeJobId is { } jobId)
                await db.Jobs.Where(j => j.Id == jobId).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Paybitch.Me.Delete")
                .LogWarning(ex, "Apple revoke failed post-commit; the outbox job retains the revocation.");
        }
    }
}

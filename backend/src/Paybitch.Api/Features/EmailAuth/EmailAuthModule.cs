using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Common.RateLimiting;
using Paybitch.Api.Common.Validation;
using Paybitch.Api.Features.Auth;
using Paybitch.Api.Features.Auth.Apple;
using Paybitch.Api.Features.Platform.Email;
using Paybitch.Api.Features.Platform.Jobs;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.EmailAuth;

/// <summary>
/// E6 email auth (§6) — passwordless OTP as a SECOND identity provider beside Sign in with Apple, plus
/// the authenticated identity-management surface (link / unlink / email change). Sessions are the SAME
/// access-JWT + rotating opaque-refresh family as SIWA (EXT-D6g): this slice reuses
/// <see cref="RefreshTokenService"/> / <see cref="JwtTokenService"/> / <see cref="TokenEpochCache"/>
/// wholesale and adds no new session type. All services are composed inline from DI primitives — no
/// Program.cs registration (mirrors <c>AuthModule</c> / <c>MeModule</c>).
/// </summary>
public sealed class EmailAuthModule : IEndpointModule
{
    private const string ProviderEmail = "email";
    private const string ProviderApple = "apple";
    private const string DefaultLocale = "cs";
    private const string DefaultCurrency = "CZK";
    private const string LoggerCategory = "Paybitch.EmailAuth";

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        // --- Unauthenticated OTP exchange (X6): own rate-limit partition, uniform responses, hashed tokens ---
        app.MapPost("/auth/email/start", StartEmailAsync)
            .AllowAnonymous()
            .WithValidation()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("EmailAuthStart")
            .WithSummary("Request a passwordless login OTP for an address (enumeration-safe uniform 200).")
            .WithTags("EmailAuth");

        app.MapPost("/auth/email/verify", VerifyEmailAsync)
            .AllowAnonymous()
            .WithValidation()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("EmailAuthVerify")
            .WithSummary("Consume an OTP → session; provisions a new user if the address is new.")
            .WithTags("EmailAuth");

        // --- Authenticated identity management (self-scoped; the session IS the subject, §6.7) ---
        app.MapGet("/me/identities", GetIdentitiesAsync)
            .WithName("GetIdentities")
            .WithSummary("List the caller's linked identities (provider + masked subject).")
            .WithTags("EmailAuth");

        app.MapPost("/me/identities/link/start", LinkStartAsync)
            .WithValidation()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("IdentityLinkStart")
            .WithSummary("Send an OTP to a target email to attach it to the caller (side-B proof).")
            .WithTags("EmailAuth");

        app.MapPost("/me/identities/link/verify", LinkVerifyAsync)
            .WithValidation()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("IdentityLinkVerify")
            .WithSummary("Consume the OTP → attach an email identity to the caller.")
            .WithTags("EmailAuth");

        app.MapDelete("/me/identities/{provider}", UnlinkIdentityAsync)
            .WithName("UnlinkIdentity")
            .WithSummary("Unlink an identity; refuses the last one (409 last_identity).")
            .WithTags("EmailAuth");

        app.MapPost("/me/email/change/start", ChangeStartAsync)
            .WithValidation()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("EmailChangeStart")
            .WithSummary("Send an OTP to a NEW contact address.")
            .WithTags("EmailAuth");

        app.MapPost("/me/email/change/verify", ChangeVerifyAsync)
            .WithValidation()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("EmailChangeVerify")
            .WithSummary("Consume the OTP → set users.email, bump token_epoch, notify the old address.")
            .WithTags("EmailAuth");
    }

    // ---------------------------------------------------------------------------------------------
    // /auth/email/start — enumeration-safe: ALWAYS 200 {"status":"sent"} unless the address is a relay.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> StartEmailAsync(
        EmailStartRequest request, AppDbContext db, IClock clock, IOptions<OperationalConstants> ops,
        IEmailSender emailSender, IJobQueue jobs, IConfiguration configuration, ILoggerFactory lf,
        HttpContext http, CancellationToken ct)
    {
        var normalized = EmailNormalization.Normalize(request.Email!);
        if (EmailNormalization.IsAppleRelay(normalized))
            return Problems.Create(StatusCodes.Status422UnprocessableEntity, EmailAuthProblemCodes.RelayEmailNotAllowed);

        var otp = BuildOtp(db, clock, ops.Value, emailSender, jobs, configuration, lf);
        await otp.StartAsync(normalized, OtpPurposes.Login, userId: null, ClientIp(http), DefaultLocale,
            enforceDailyCap: true, ct);

        return Results.Ok(SentResponse.Sent);
    }

    // ---------------------------------------------------------------------------------------------
    // /auth/email/verify — consume the code, then provision-or-login and issue the v1 §4.1 session.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> VerifyEmailAsync(
        EmailVerifyRequest request, AppDbContext db, IClock clock, IOptions<OperationalConstants> ops,
        JwtTokenService jwtTokens, IMemoryCache memoryCache, IEmailSender emailSender, IJobQueue jobs,
        IConfiguration configuration, ILoggerFactory lf, CancellationToken ct)
    {
        var normalized = EmailNormalization.Normalize(request.Email!);
        var otp = BuildOtp(db, clock, ops.Value, emailSender, jobs, configuration, lf);

        var result = await otp.VerifyAsync(OtpPurposes.Login, request.Code!, normalized, boundUserId: null, ct);
        if (!result.Succeeded)
            return Problems.Unauthenticated(EmailAuthProblemCodes.InvalidOrExpiredCode);

        var (user, isNewUser) = await ResolveEmailUserAsync(db, clock, result.Email!, ct);

        var epochCache = new TokenEpochCache(memoryCache, ops);
        var refresh = new RefreshTokenService(db, clock, ops, jwtTokens, epochCache);
        var pair = await refresh.IssueNewSessionAsync(user.Id, user.TokenEpoch, ct);

        return Results.Ok(new AuthSessionResponse(
            pair.AccessToken, pair.RefreshToken, pair.ExpiresIn,
            new AuthUser(user.Id.ToString(), user.DisplayName, user.Email,
                user.DefaultCurrency, user.Locale, isNewUser)));
    }

    // ---------------------------------------------------------------------------------------------
    // GET /me/identities
    // ---------------------------------------------------------------------------------------------
    private static Task<IResult> GetIdentitiesAsync(
        AppDbContext db, ICurrentUser currentUser, CancellationToken ct)
        => IdentitiesResultAsync(db, currentUser.UserId, ct);

    // ---------------------------------------------------------------------------------------------
    // POST /me/identities/link/start — OTP to the target address, bound to the caller (side-A binding).
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> LinkStartAsync(
        LinkStartRequest request, AppDbContext db, IClock clock, IOptions<OperationalConstants> ops,
        IEmailSender emailSender, IJobQueue jobs, IConfiguration configuration, ILoggerFactory lf,
        ICurrentUser currentUser, HttpContext http, CancellationToken ct)
    {
        var normalized = EmailNormalization.Normalize(request.Email!);
        if (EmailNormalization.IsAppleRelay(normalized))
            return Problems.Create(StatusCodes.Status422UnprocessableEntity, EmailAuthProblemCodes.RelayEmailNotAllowed);

        var locale = await ReadLocaleAsync(db, currentUser.UserId, ct);
        var otp = BuildOtp(db, clock, ops.Value, emailSender, jobs, configuration, lf);
        await otp.StartAsync(normalized, OtpPurposes.Link, currentUser.UserId, ClientIp(http), locale,
            enforceDailyCap: false, ct);

        return Results.Ok(SentResponse.Sent);
    }

    // ---------------------------------------------------------------------------------------------
    // POST /me/identities/link/verify — consume the OTP (bound to caller), attach the email identity.
    // NEVER auto-merge: if the address already backs ANOTHER user → 409 identity_taken (EXT-D6c/d).
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> LinkVerifyAsync(
        LinkVerifyRequest request, AppDbContext db, IClock clock, IOptions<OperationalConstants> ops,
        IEmailSender emailSender, IJobQueue jobs, IConfiguration configuration, ILoggerFactory lf,
        ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var otp = BuildOtp(db, clock, ops.Value, emailSender, jobs, configuration, lf);

        var result = await otp.VerifyAsync(OtpPurposes.Link, request.Code!, email: null, boundUserId: userId, ct);
        if (!result.Succeeded)
            return Problems.Unauthenticated(EmailAuthProblemCodes.InvalidOrExpiredCode);

        var subject = result.Email!;
        var existing = await db.AuthIdentities
            .FirstOrDefaultAsync(a => a.Provider == ProviderEmail && a.Subject == subject, ct);
        if (existing is not null)
        {
            if (existing.UserId != userId)
                return Problems.Conflict(EmailAuthProblemCodes.IdentityTaken); // never auto-merge
            return await IdentitiesResultAsync(db, userId, ct); // already linked — idempotent
        }

        db.AuthIdentities.Add(new AuthIdentity { UserId = userId, Provider = ProviderEmail, Subject = subject });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the UNIQUE(provider, subject) race to a concurrent link of the same address.
            db.ChangeTracker.Clear();
            var winner = await db.AuthIdentities
                .FirstOrDefaultAsync(a => a.Provider == ProviderEmail && a.Subject == subject, ct);
            if (winner is not null && winner.UserId != userId)
                return Problems.Conflict(EmailAuthProblemCodes.IdentityTaken);
        }

        return await IdentitiesResultAsync(db, userId, ct);
    }

    // ---------------------------------------------------------------------------------------------
    // DELETE /me/identities/{provider} — refuse the last identity; hard-delete + bump epoch; Apple revokes upstream.
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> UnlinkIdentityAsync(
        string provider, AppDbContext db, IClock clock, IOptions<OperationalConstants> ops,
        IMemoryCache memoryCache, IConfiguration configuration, ILoggerFactory lf,
        ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId;

        var identities = await db.AuthIdentities.Where(a => a.UserId == userId).ToListAsync(ct);
        var target = identities.FirstOrDefault(a => a.Provider == provider);
        if (target is null)
            return Problems.NotFound();
        if (identities.Count <= 1)
            return Problems.Conflict(EmailAuthProblemCodes.LastIdentity); // self-lockout guard (EXT-D6i)

        var appleTokenEnc = provider == ProviderApple ? target.AppleRefreshTokenEnc : null;
        long newEpoch;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.AuthIdentities.Where(a => a.Id == target.Id).ExecuteDeleteAsync(ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return Problems.NotFound();
            user.TokenEpoch += 1;
            await db.SaveChangesAsync(ct);
            newEpoch = user.TokenEpoch;

            await tx.CommitAsync(ct);
        }

        new TokenEpochCache(memoryCache, ops).Set(userId, newEpoch);

        // Apple parity (App Review 5.1.1(v)): discard the upstream grant, best-effort post-commit.
        if (appleTokenEnc is not null)
            await TryRevokeAppleAsync(configuration, clock, appleTokenEnc, lf, ct);

        return Results.NoContent();
    }

    // ---------------------------------------------------------------------------------------------
    // POST /me/email/change/start — OTP to the NEW address, bound to the caller (purpose verify_change).
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> ChangeStartAsync(
        EmailChangeStartRequest request, AppDbContext db, IClock clock, IOptions<OperationalConstants> ops,
        IEmailSender emailSender, IJobQueue jobs, IConfiguration configuration, ILoggerFactory lf,
        ICurrentUser currentUser, HttpContext http, CancellationToken ct)
    {
        var normalized = EmailNormalization.Normalize(request.Email!);
        if (EmailNormalization.IsAppleRelay(normalized))
            return Problems.Create(StatusCodes.Status422UnprocessableEntity, EmailAuthProblemCodes.RelayEmailNotAllowed);

        var locale = await ReadLocaleAsync(db, currentUser.UserId, ct);
        var otp = BuildOtp(db, clock, ops.Value, emailSender, jobs, configuration, lf);
        await otp.StartAsync(normalized, OtpPurposes.VerifyChange, currentUser.UserId, ClientIp(http), locale,
            enforceDailyCap: false, ct);

        return Results.Ok(SentResponse.Sent);
    }

    // ---------------------------------------------------------------------------------------------
    // POST /me/email/change/verify — set users.email + email_verified_at, bump epoch, move the email
    // identity's subject, and send a grace notice to the OLD address (EXT-D6h).
    // ---------------------------------------------------------------------------------------------
    private static async Task<IResult> ChangeVerifyAsync(
        EmailChangeVerifyRequest request, AppDbContext db, IClock clock, IOptions<OperationalConstants> ops,
        IMemoryCache memoryCache, IEmailSender emailSender, IJobQueue jobs, IConfiguration configuration,
        ILoggerFactory lf, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var otp = BuildOtp(db, clock, ops.Value, emailSender, jobs, configuration, lf);

        var result = await otp.VerifyAsync(OtpPurposes.VerifyChange, request.Code!, email: null, boundUserId: userId, ct);
        if (!result.Succeeded)
            return Problems.Unauthenticated(EmailAuthProblemCodes.InvalidOrExpiredCode);

        var newEmail = result.Email!;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);
        if (user is null)
            return Problems.NotFound();

        // Moving the contact email onto an address that is already ANOTHER user's email credential is a
        // subject collision — refuse rather than merge (EXT-D6b/c).
        var collision = await db.AuthIdentities
            .AnyAsync(a => a.Provider == ProviderEmail && a.Subject == newEmail && a.UserId != userId, ct);
        if (collision)
            return Problems.Conflict(EmailAuthProblemCodes.IdentityTaken);

        var oldEmail = user.Email;
        var now = clock.UtcNow;

        user.Email = newEmail;
        user.EmailVerifiedAt = now;
        user.TokenEpoch += 1; // the account's contact/credential moved — re-establish sessions (EXT-D6h)

        var emailIdentity = await db.AuthIdentities
            .FirstOrDefaultAsync(a => a.UserId == userId && a.Provider == ProviderEmail, ct);
        if (emailIdentity is not null)
            emailIdentity.Subject = newEmail;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Problems.Conflict(EmailAuthProblemCodes.IdentityTaken);
        }

        new TokenEpochCache(memoryCache, ops).Set(userId, user.TokenEpoch);

        // Grace security-notice to the OLD address — informational, never a gate (EXT-D6h).
        if (!string.IsNullOrWhiteSpace(oldEmail) &&
            !string.Equals(oldEmail, newEmail, StringComparison.OrdinalIgnoreCase))
        {
            await emailSender.SendAsync(OtpEmailFactory.BuildChangeNotice(oldEmail!, user.Locale), ct);
        }

        return Results.Ok(new EmailChangeResponse(newEmail, EmailVerified: true));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static OtpService BuildOtp(
        AppDbContext db, IClock clock, OperationalConstants ops, IEmailSender emailSender, IJobQueue jobs,
        IConfiguration configuration, ILoggerFactory lf)
        => new(db, clock, EmailAuthConfig.From(configuration), emailSender, jobs,
            perEmailDailyMintCap: ops.RateLimitProvisioningPerIdentityPerDay,
            lf.CreateLogger(LoggerCategory));

    /// <summary>Find the email identity's user or provision a fresh one (§4.1 shape, minus the fullName tier — EXT-D6g).</summary>
    private static async Task<(User User, bool IsNewUser)> ResolveEmailUserAsync(
        AppDbContext db, IClock clock, string subject, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var identity = await db.AuthIdentities
            .FirstOrDefaultAsync(a => a.Provider == ProviderEmail && a.Subject == subject, ct);
        if (identity is not null)
        {
            var existing = await db.Users.FirstAsync(u => u.Id == identity.UserId, ct);
            // Verifying an OTP proves control of the mailbox — stamp verification, adopt as contact if unset.
            var changed = false;
            if (existing.EmailVerifiedAt is null) { existing.EmailVerifiedAt = now; changed = true; }
            if (string.IsNullOrWhiteSpace(existing.Email)) { existing.Email = subject; changed = true; }
            if (changed) await db.SaveChangesAsync(ct);
            return (existing, false);
        }

        var user = new User
        {
            DisplayName = ResolveDisplayName(subject),
            Email = subject,
            EmailVerifiedAt = now,
            Locale = DefaultLocale,
            DefaultCurrency = DefaultCurrency,
        };
        db.Users.Add(user);
        db.AuthIdentities.Add(new AuthIdentity { UserId = user.Id, Provider = ProviderEmail, Subject = subject });

        try
        {
            await db.SaveChangesAsync(ct);
            return (user, true);
        }
        catch (DbUpdateException)
        {
            // Concurrent first-login for the same address won the UNIQUE(provider, subject) race.
            db.ChangeTracker.Clear();
            var winner = await db.AuthIdentities.FirstAsync(a => a.Provider == ProviderEmail && a.Subject == subject, ct);
            var existing = await db.Users.FirstAsync(u => u.Id == winner.UserId, ct);
            return (existing, false);
        }
    }

    // §4.1 precedence minus row-1 fullName (EXT-D6g): email local-part → localized default.
    private static string ResolveDisplayName(string email)
    {
        var localPart = email.Split('@', 2)[0];
        return string.IsNullOrWhiteSpace(localPart) ? AuthLocalization.DefaultDisplayName(DefaultLocale) : localPart;
    }

    private static async Task<IResult> IdentitiesResultAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        var rows = await db.AuthIdentities
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Provider, a.Subject, a.CreatedAt })
            .ToListAsync(ct);

        var identities = rows
            .Select(r => new IdentitySummary(r.Provider, EmailNormalization.MaskSubject(r.Subject), r.CreatedAt))
            .ToList();
        return Results.Ok(new IdentitiesResponse(identities));
    }

    private static async Task<string> ReadLocaleAsync(AppDbContext db, Guid userId, CancellationToken ct)
        => await db.Users.Where(u => u.Id == userId).Select(u => u.Locale).FirstOrDefaultAsync(ct) ?? DefaultLocale;

    private static System.Net.IPAddress? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress;

    private static async Task TryRevokeAppleAsync(
        IConfiguration configuration, IClock clock, byte[] appleTokenEnc, ILoggerFactory lf, CancellationToken ct)
    {
        var appleOptions = AppleAuthOptions.From(configuration);
        var key = AppleTokenCipher.TryResolveKey(appleOptions);
        if (!appleOptions.CanExchangeCode || key is null)
            return; // no signing material configured — nothing to revoke against

        var plaintext = AppleTokenCipher.Decrypt(appleTokenEnc, key);
        if (plaintext is null)
            return;

        try
        {
            await new AppleCodeExchange(appleOptions, clock).RevokeAsync(plaintext, ct);
        }
        catch (Exception ex)
        {
            lf.CreateLogger(LoggerCategory).LogWarning(ex, "Apple upstream revoke on unlink failed (non-fatal).");
        }
    }
}

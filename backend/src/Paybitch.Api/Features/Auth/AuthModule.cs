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
using Paybitch.Api.Features.Auth.Apple;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Auth;

/// <summary>
/// The unauthenticated auth-exchange surface (§3.1, §4.1): Apple sign-in, refresh rotation, and the
/// authenticated logout. Services that this slice owns (<see cref="RefreshTokenService"/>,
/// <see cref="TokenEpochCache"/>, <see cref="AppleCodeExchange"/>) are composed inline from DI
/// primitives — the slice adds no Program.cs registration.
/// </summary>
public sealed class AuthModule : IEndpointModule
{
    private const int NotImplemented = StatusCodes.Status501NotImplemented;
    private const string AppleNotConfiguredCode = "apple_not_configured";

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/apple", AppleAsync)
            .AllowAnonymous()
            .WithValidation()
            .RequireRateLimiting(RateLimitPolicies.Provisioning)
            .WithName("AuthApple")
            .WithSummary("Exchange an Apple identity token for a session (first call provisions the user).")
            .WithTags("Auth");

        app.MapPost("/auth/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithValidation()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithName("AuthRefresh")
            .WithSummary("Rotate a refresh token into a new access + refresh pair.")
            .WithTags("Auth");

        app.MapPost("/auth/logout", LogoutAsync)
            .WithName("AuthLogout")
            .WithSummary("Revoke the refresh token; optionally end all sessions and/or drop a device.")
            .WithTags("Auth");
    }

    private static async Task<IResult> AppleAsync(
        AppleSignInRequest request,
        AppDbContext db,
        IClock clock,
        IOptions<OperationalConstants> ops,
        JwtTokenService jwtTokens,
        IMemoryCache memoryCache,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var appleOptions = AppleAuthOptions.From(configuration);
        var logger = loggerFactory.CreateLogger("Paybitch.Auth.Apple");

        // Dev-without-Apple gate: keep the full validation path below intact, but refuse cleanly (§4.1).
        if (!appleOptions.CanValidateIdentity)
        {
            return Problems.Create(
                NotImplemented, AppleNotConfiguredCode,
                title: "Sign in with Apple is not configured",
                detail: "The server has no Apple bundle id configured, so identity tokens cannot be verified.");
        }

        var identity = await AppleIdentityTokenValidator.ValidateAsync(
            request.IdentityToken!, request.Nonce!, appleOptions.BundleId!, ct);
        if (!identity.IsValid)
            return Problems.Unauthenticated(ProblemCodes.AppleTokenInvalid);

        var subject = identity.Subject!;
        var (user, isNewUser) = await ResolveUserAsync(db, subject, identity.Email, request.FullName, ct);

        // Redeem the authorization code for Apple revocation material — best-effort, non-fatal (§4.1).
        await TryRedeemAndStoreAppleTokenAsync(db, appleOptions, clock, subject, request.AuthorizationCode!, logger, ct);

        var epochCache = new TokenEpochCache(memoryCache, ops);
        var refresh = new RefreshTokenService(db, clock, ops, jwtTokens, epochCache);
        var pair = await refresh.IssueNewSessionAsync(user.Id, user.TokenEpoch, ct);

        return Results.Ok(new AuthSessionResponse(
            pair.AccessToken, pair.RefreshToken, pair.ExpiresIn,
            new AuthUser(
                user.Id.ToString(), user.DisplayName, user.Email,
                user.DefaultCurrency, user.Locale, isNewUser)));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        AppDbContext db,
        IClock clock,
        IOptions<OperationalConstants> ops,
        JwtTokenService jwtTokens,
        IMemoryCache memoryCache,
        CancellationToken ct)
    {
        var epochCache = new TokenEpochCache(memoryCache, ops);
        var refresh = new RefreshTokenService(db, clock, ops, jwtTokens, epochCache);

        var result = await refresh.RotateAsync(request.RefreshToken!, ct);
        return result is { Succeeded: true, Pair: { } pair }
            ? Results.Ok(new TokenPairResponse(pair.AccessToken, pair.RefreshToken, pair.ExpiresIn))
            : Problems.Unauthenticated();
    }

    private static async Task<IResult> LogoutAsync(
        LogoutRequest? request,
        AppDbContext db,
        IClock clock,
        IOptions<OperationalConstants> ops,
        IMemoryCache memoryCache,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var body = request ?? new LogoutRequest(null, null, false);
        var userId = currentUser.UserId;
        var now = clock.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (!string.IsNullOrEmpty(body.RefreshToken))
        {
            var hash = OpaqueToken.Hash(body.RefreshToken);
            await db.RefreshTokens
                .Where(t => t.TokenHash == hash && t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        }

        if (body.AllSessions)
        {
            await db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is not null)
            {
                user.TokenEpoch += 1;
                await db.SaveChangesAsync(ct);
                new TokenEpochCache(memoryCache, ops).Set(userId, user.TokenEpoch);
            }

            await db.Devices.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);
        }
        else if (body.DeviceId is { } deviceId)
        {
            await db.Devices.Where(d => d.Id == deviceId && d.UserId == userId).ExecuteDeleteAsync(ct);
        }

        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Look up the Apple identity or provision a fresh user. <c>display_name</c> is resolved from the
    /// §4.1 precedence ONLY on first provisioning — a later login ignores <c>fullName</c> (no rename).
    /// </summary>
    private static async Task<(User User, bool IsNewUser)> ResolveUserAsync(
        AppDbContext db, string subject, string? email, AppleFullName? fullName, CancellationToken ct)
    {
        var identity = await db.AuthIdentities
            .FirstOrDefaultAsync(a => a.Provider == "apple" && a.Subject == subject, ct);
        if (identity is not null)
        {
            var existing = await db.Users.FirstAsync(u => u.Id == identity.UserId, ct);
            return (existing, false);
        }

        var user = new User
        {
            DisplayName = ResolveDisplayName(fullName, email, locale: "cs"),
            Email = email,
            Locale = "cs",
            DefaultCurrency = "CZK",
        };
        db.Users.Add(user);
        db.AuthIdentities.Add(new AuthIdentity { UserId = user.Id, Provider = "apple", Subject = subject });

        try
        {
            await db.SaveChangesAsync(ct);
            return (user, true);
        }
        catch (DbUpdateException)
        {
            // Concurrent first-login for the same Apple sub won the unique (provider, subject) race.
            db.ChangeTracker.Clear();
            var winner = await db.AuthIdentities.FirstAsync(a => a.Provider == "apple" && a.Subject == subject, ct);
            var existing = await db.Users.FirstAsync(u => u.Id == winner.UserId, ct);
            return (existing, false);
        }
    }

    // §4.1 precedence: fullName → email local-part → localized default.
    private static string ResolveDisplayName(AppleFullName? fullName, string? email, string locale)
    {
        var composed = string.Join(' ',
                new[] { fullName?.GivenName, fullName?.FamilyName }.Where(p => !string.IsNullOrWhiteSpace(p)))
            .Trim();
        if (!string.IsNullOrWhiteSpace(composed))
            return composed;

        var localPart = email?.Split('@', 2)[0];
        if (!string.IsNullOrWhiteSpace(localPart))
            return localPart;

        return AuthLocalization.DefaultDisplayName(locale);
    }

    private static async Task TryRedeemAndStoreAppleTokenAsync(
        AppDbContext db, AppleAuthOptions appleOptions, IClock clock,
        string subject, string authorizationCode, ILogger logger, CancellationToken ct)
    {
        if (!appleOptions.CanExchangeCode)
            return;

        try
        {
            var exchange = new AppleCodeExchange(appleOptions, clock);
            var appleRefreshToken = await exchange.RedeemAsync(authorizationCode, ct);
            if (appleRefreshToken is null)
                return;

            var key = AppleTokenCipher.TryResolveKey(appleOptions);
            if (key is null)
            {
                logger.LogWarning("Apple refresh token redeemed but no encryption key configured; not persisting it.");
                return;
            }

            var encrypted = AppleTokenCipher.Encrypt(appleRefreshToken, key);
            await db.AuthIdentities
                .Where(a => a.Provider == "apple" && a.Subject == subject)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppleRefreshTokenEnc, encrypted), ct);
        }
        catch (Exception ex)
        {
            // Single-use code with ~5 min TTL — no retry-later; a failure keeps any prior token current (§4.1).
            logger.LogWarning(ex, "Apple authorization-code redemption failed (non-fatal).");
        }
    }
}

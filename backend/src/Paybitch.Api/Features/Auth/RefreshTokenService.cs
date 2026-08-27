using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Options;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Auth;

/// <summary>An issued session: the access JWT, the opaque refresh token (plaintext, returned once), and TTL.</summary>
public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>Outcome of a rotation: either a fresh <see cref="TokenPair"/>, or an auth failure (401).</summary>
public sealed record RotationResult(bool Succeeded, TokenPair? Pair)
{
    public static RotationResult Success(TokenPair pair) => new(true, pair);
    public static RotationResult Unauthorized() => new(false, null);
}

/// <summary>
/// Issues and rotates the opaque refresh-token family (§4.1). Rotation is an <b>atomic conditional</b>
/// <c>UPDATE … WHERE revoked_at IS NULL</c> (losing the row-level race ⇒ someone else rotated it), with
/// a <b>grace window</b> so a mobile retry of a just-rotated token gets a working successor instead of
/// tripping theft detection, and <b>reuse detection</b>: an older revoked token (outside grace, or a
/// force-revoked one) kills the whole family and bumps <c>token_epoch</c>.
/// </summary>
public sealed class RefreshTokenService(
    AppDbContext db,
    IClock clock,
    IOptions<OperationalConstants> ops,
    JwtTokenService jwtTokens,
    TokenEpochCache epochCache)
{
    private OperationalConstants Ops => ops.Value;

    /// <summary>Mint a brand-new family (first login / re-auth) and persist it; returns the session pair.</summary>
    public async Task<TokenPair> IssueNewSessionAsync(Guid userId, long epoch, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var familyId = Guid.CreateVersion7();
        var pair = TrackInFamily(Guid.CreateVersion7(), userId, familyId, familyStart: now, now, epoch);
        await db.SaveChangesAsync(ct);
        return pair;
    }

    /// <summary>Rotate a presented refresh token → a new pair, or a 401 verdict (unknown/expired/theft).</summary>
    public async Task<RotationResult> RotateAsync(string presentedToken, CancellationToken ct)
    {
        var hash = OpaqueToken.Hash(presentedToken);
        var now = clock.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await RotateCoreAsync(hash, now, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<RotationResult> RotateCoreAsync(byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null)
            return RotationResult.Unauthorized(); // unknown token — never existed or already pruned

        var familyStart = await db.RefreshTokens
            .Where(t => t.FamilyId == row.FamilyId)
            .MinAsync(t => t.CreatedAt, ct);

        // Absolute family lifetime — dies regardless of activity (re-auth via Apple).
        if (familyStart + Ops.RefreshTokenAbsoluteLifetime <= now)
        {
            await RevokeFamilyAsync(row.FamilyId, now, ct);
            return RotationResult.Unauthorized();
        }

        if (row.RevokedAt is not null)
            return await ResolveRevokedAsync(row.UserId, row.FamilyId, familyStart, now, row.RevokedAt.Value, row.ReplacedBy, ct);

        // Sliding expiry on the presented (still-live) token.
        if (row.ExpiresAt <= now)
            return RotationResult.Unauthorized();

        // Atomic conditional revoke: only the caller that flips revoked_at NULL→now wins the row lock,
        // which it then holds until commit — so the intermediate "revoked but replaced_by NULL" state
        // below is never visible to a concurrent rotation (it blocks on this row).
        var affected = await db.RefreshTokens
            .Where(t => t.Id == row.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        if (affected == 0)
        {
            // A concurrent rotation beat us; re-read to grace-or-theft on the freshly-revoked row.
            var current = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == row.Id, ct);
            return current?.RevokedAt is { } revoked
                ? await ResolveRevokedAsync(row.UserId, row.FamilyId, familyStart, now, revoked, current.ReplacedBy, ct)
                : RotationResult.Unauthorized();
        }

        // Insert the successor FIRST (its id must exist before replaced_by can reference it — the
        // replaced_by FK targets refresh_tokens.id and is not deferrable), then link the old row to it.
        var epoch = await ReadEpochAsync(row.UserId, ct);
        var successorId = Guid.CreateVersion7();
        var pair = TrackInFamily(successorId, row.UserId, row.FamilyId, familyStart, now, epoch);
        await db.SaveChangesAsync(ct);
        await db.RefreshTokens
            .Where(t => t.Id == row.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReplacedBy, successorId), ct);
        return RotationResult.Success(pair);
    }

    private async Task<RotationResult> ResolveRevokedAsync(
        Guid userId, Guid familyId, DateTimeOffset familyStart, DateTimeOffset now,
        DateTimeOffset revokedAt, Guid? replacedBy, CancellationToken ct)
    {
        var withinGrace = now - revokedAt <= Ops.RefreshRotationGraceWindow;
        if (withinGrace && replacedBy is not null)
        {
            // GRACE (§4.1): a just-rotated token retried within N seconds. We cannot reproduce the
            // successor's opaque value (only its hash is stored), so mint a fresh in-family successor
            // for the retrying client — the family stays alive, no false-positive theft.
            var epoch = await ReadEpochAsync(userId, ct);
            var pair = TrackInFamily(Guid.CreateVersion7(), userId, familyId, familyStart, now, epoch);
            await db.SaveChangesAsync(ct);
            return RotationResult.Success(pair);
        }

        // THEFT: an old revoked token (outside grace) or a force-revoked one is being reused.
        await RevokeFamilyAsync(familyId, now, ct);
        await BumpEpochAsync(userId, now, ct);
        return RotationResult.Unauthorized();
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct)
        => db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private async Task BumpEpochAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return;
        user.TokenEpoch += 1;
        await db.SaveChangesAsync(ct);
        epochCache.Set(userId, user.TokenEpoch);
    }

    private async Task<long> ReadEpochAsync(Guid userId, CancellationToken ct)
        => await db.Users.Where(u => u.Id == userId).Select(u => (long?)u.TokenEpoch).FirstOrDefaultAsync(ct) ?? 0;

    // Add (but do not save) a new refresh row in a family, issue the access token, return the pair.
    private TokenPair TrackInFamily(Guid id, Guid userId, Guid familyId, DateTimeOffset familyStart, DateTimeOffset now, long epoch)
    {
        var (token, hash) = OpaqueToken.Generate();
        var sliding = now + Ops.RefreshTokenSlidingLifetime;
        var absolute = familyStart + Ops.RefreshTokenAbsoluteLifetime;
        var expiresAt = sliding < absolute ? sliding : absolute;

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = id,
            UserId = userId,
            TokenHash = hash,
            FamilyId = familyId,
            ExpiresAt = expiresAt,
        });

        var access = jwtTokens.IssueAccessToken(userId, epoch);
        return new TokenPair(access.Value, token, access.ExpiresInSeconds);
    }
}

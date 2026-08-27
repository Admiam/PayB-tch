namespace Paybitch.Api.Features.EmailAuth;

/// <summary>
/// <c>jobs.kind</c> discriminators owned by E6 (extensions add their own beyond E0's
/// <c>Paybitch.Api.Features.Platform.Jobs.JobKinds</c>, §0.4.2). Kept in the slice because the Platform
/// <c>JobKinds</c> catalog is a shared file this slice must not edit.
/// </summary>
internal static class EmailAuthJobKinds
{
    /// <summary>Idempotent sweep that DELETEs expired/consumed <c>email_login_tokens</c> so <c>created_ip</c> (PII) never outlives the TTL (§6.3).</summary>
    public const string EmailTokenPrune = "email_token.prune";
}

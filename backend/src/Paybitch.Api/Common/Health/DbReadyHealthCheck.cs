using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Common.Health;

/// <summary>
/// Readiness probe (§6): can the app reach Postgres? Tagged <c>ready</c> so <c>/health/ready</c>
/// includes it while <c>/health</c> (liveness) stays a dependency-free "process is up".
/// </summary>
public sealed class DbReadyHealthCheck(AppDbContext db) : IHealthCheck
{
    public const string Name = "postgres";
    public const string ReadyTag = "ready";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connectivity check failed.", ex);
        }
    }
}

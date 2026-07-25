using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Paybitch.Infrastructure;

/// <summary>
/// Lets `dotnet ef migrations add` build an <see cref="AppDbContext"/> against this project directly.
/// Reads the connection string from env PAYBITCH_DB, falling back to a localhost dev string.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string DevConnectionString =
        "Host=localhost;Port=5432;Database=paybitch;Username=paybitch;Password=paybitch";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAYBITCH_DB") ?? DevConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Infrastructure;

/// <summary>Composition root for the infrastructure layer.</summary>
public static class DependencyInjection
{
    private const string DevConnectionString =
        "Host=localhost;Port=5432;Database=paybitch;Username=paybitch;Password=paybitch";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString =
            config.GetConnectionString("Postgres")
            ?? Environment.GetEnvironmentVariable("PAYBITCH_DB")
            ?? DevConnectionString;

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IChangeLogWriter, ChangeLogWriter>();
        services.AddScoped<IGroupAccess, GroupAccess>();

        return services;
    }
}

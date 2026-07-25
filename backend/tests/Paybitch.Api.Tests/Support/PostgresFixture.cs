using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Paybitch.Api.Common.Auth;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Entities;
using Testcontainers.PostgreSql;

namespace Paybitch.Api.Tests.Support;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> that points the app at a throwaway Postgres
/// container (config override <c>ConnectionStrings:Postgres</c>) and strips the background
/// <see cref="IHostedService"/> workers so tests exercise only the HTTP paths deterministically. The
/// ES256 dev key + <c>JwtTokenService</c> singleton are shared with JwtBearer, so a token minted from
/// <see cref="Services"/> validates against the same running app.
/// </summary>
public sealed class PaybitchApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Kept for anything that reads the connection string from IConfiguration at *runtime*
        // (e.g. PostgresRateLimitStore). It is NOT enough on its own — see ConfigureTestServices.
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
            }));

        builder.ConfigureTestServices(services =>
        {
            // AddInfrastructure resolves the connection string EAGERLY into the AddDbContext closure
            // (Infrastructure/DependencyInjection.cs) while Program.cs runs. Under minimal hosting the
            // ConfigureAppConfiguration callback above has not been applied at that point, so the app
            // would bind to appsettings.Development.json (Port=5544) and silently talk to the developer's
            // database instead of this fixture's container. ConfigureTestServices runs after every
            // application registration, so re-registering here is timing-proof.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention());

            // The recurring / notification / job / rate-limit-gc pollers are irrelevant to these HTTP
            // tests and only add DB churn; remove them so the run is deterministic.
            services.RemoveAll<IHostedService>();
        });
    }
}

/// <summary>
/// Boots a real <c>postgres:16</c> via Testcontainers, then a <see cref="PaybitchApiFactory"/> against
/// it, and runs EF migrations once (the app does NOT migrate on startup). Shared by every integration
/// test class through <see cref="ApiTestCollection"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .Build();

    public PaybitchApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Factory = new PaybitchApiFactory(_container.GetConnectionString());

        // Fixture owns schema creation: resolve AppDbContext from the running app and migrate.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Insert a fresh user (epoch 0) and return its id.</summary>
    public async Task<Guid> SeedUserAsync(string displayName = "User")
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            DisplayName = displayName,
            DefaultCurrency = "CZK",
            Locale = "cs",
            TokenEpoch = 0,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Mint a valid ES256 access token for a user via the app's own token service.</summary>
    public string IssueToken(Guid userId, long epoch = 0)
    {
        var tokens = Factory.Services.GetRequiredService<JwtTokenService>();
        return tokens.IssueAccessToken(userId, epoch).Value;
    }

    /// <summary>An <see cref="HttpClient"/> carrying no <c>Authorization</c> header.</summary>
    public HttpClient CreateAnonymousClient() => Factory.CreateClient();

    /// <summary>Seed a new user and return an <see cref="HttpClient"/> authenticated as that user.</summary>
    public async Task<(Guid UserId, HttpClient Client)> NewUserClientAsync(string displayName = "User")
    {
        var userId = await SeedUserAsync(displayName);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueToken(userId));
        return (userId, client);
    }
}

/// <summary>
/// Groups every HTTP integration test onto the one shared <see cref="PostgresFixture"/> — the container
/// starts once and the classes run sequentially (no cross-test DB races), each isolated by minting its
/// own user + group with fresh ids.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiTestCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres-api";
}

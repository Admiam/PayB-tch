using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Health;
using Paybitch.Api.Common.Options;
using Paybitch.Api.Common.RateLimiting;
using Paybitch.Api.Common.Validation;
using Paybitch.Api.Features.Activity;
using Paybitch.Api.Features.Notifications.Fanout;
using Paybitch.Api.Features.Platform;
using Paybitch.Api.Features.Recurring;
using Paybitch.Infrastructure;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

// Bootstrap logger — captures failures during host construction before the full pipeline is up.
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // --- Structured JSON logging (§6) ---
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(new CompactJsonFormatter()));

    // --- Operational constants + JWT options (Appendix A, §4.1) ---
    builder.Services
        .AddOptions<OperationalConstants>()
        .Bind(builder.Configuration.GetSection(OperationalConstants.SectionName))
        .ValidateOnStart();
    builder.Services
        .AddOptions<JwtOptions>()
        .Bind(builder.Configuration.GetSection(JwtOptions.SectionName));

    // Eager copy for pipeline wiring (Kestrel limit, rate-limiter permits) that runs before DI is built.
    var ops = builder.Configuration.GetSection(OperationalConstants.SectionName).Get<OperationalConstants>()
              ?? new OperationalConstants();

    // --- Request body cap ⇒ 413 payload_too_large (§5, Appendix A) ---
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = ops.RequestBodyMaxBytes);

    // --- Trusted-proxy forwarded headers (MANDATORY, §4.3) — else the rate-limit partition key is spoofable ---
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        var proxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        if (proxies.Length > 0)
        {
            // Explicit trust list configured — replace the loopback-only default.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var proxy in proxies)
                if (IPAddress.TryParse(proxy, out var ip))
                    options.KnownProxies.Add(ip);
        }
        // No proxies configured ⇒ defaults (loopback only): X-Forwarded-* from other hosts is untrusted.
    });

    // --- Infrastructure (AppDbContext, IClock, IGroupAccess, IChangeLogWriter) ---
    builder.Services.AddInfrastructure(builder.Configuration);

    // --- E0 platform primitives (§0.4): jobs substrate, IBlobStore, IEmailSender ---
    builder.Services.AddPlatform(builder.Configuration);

    // --- Activity write seam (§3.10): plain scoped service, no assembly-scan convention, so it must be
    // registered explicitly here or handlers that inject IActivityWriter (Exports) can't bind. ---
    builder.Services.AddActivityWriter();

    // --- Background schedulers (hosted services are not assembly-scanned; must be wired explicitly) ---
    // E3 recurring materialization worker.
    builder.Services.AddRecurringScheduler();
    // E5 notification fan-out scheduler (seeds + self-heals the notification.fanout job loop).
    builder.Services.AddHostedService<NotificationFanoutScheduler>();

    // --- Auth (§4.1): ES256 keys, token issuance, current-user accessor, epoch cache ---
    builder.Services.AddSingleton<Es256KeyProvider>();
    builder.Services.AddSingleton<JwtTokenService>();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, CurrentUser>();
    builder.Services.AddMemoryCache();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer();
    builder.Services
        .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<Es256KeyProvider, IOptions<JwtOptions>>((jwtBearer, keys, jwt) =>
        {
            jwtBearer.MapInboundClaims = false; // keep raw claim names (sub, epoch)
            jwtBearer.TokenValidationParameters = keys.CreateValidationParameters(jwt.Value);
            jwtBearer.Events = JwtAuthEvents.Create();
        });
    builder.Services.AddAuthorization();

    // --- Rate limiting (§4.3, Appendix A) ---
    builder.Services.AddApiRateLimiter(ops);

    // --- Validation (FluentValidation as an endpoint filter, §5) ---
    builder.Services.AddApiValidators();

    // --- RFC 9457 problem+json (§3.4, §5) ---
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // --- Health: liveness (/health) + readiness (/health/ready with a DB check) (§6) ---
    builder.Services
        .AddHealthChecks()
        .AddCheck<DbReadyHealthCheck>(DbReadyHealthCheck.Name, tags: [DbReadyHealthCheck.ReadyTag]);

    // --- OpenAPI document (surfaced via Scalar in Development only, §5) ---
    builder.Services.AddOpenApi();

    // --- CORS: no policy registered — deny-all by default (§4.3). The only client is native iOS. ---

    var app = builder.Build();

    app.UseForwardedHeaders();
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    if (!app.Environment.IsDevelopment())
        app.UseHsts();

    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    // Scalar UI + OpenAPI JSON — Development only; production never maps them (§5).
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    // Split health probes (§6).
    app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains(DbReadyHealthCheck.ReadyTag),
    });

    // Feature slices self-register under /v1 (secure-by-default; opt out with .AllowAnonymous()).
    app.MapEndpointModules();

    app.Run();
}
// The host-factory resolver (WebApplicationFactory integration tests) unwinds a fully-built host by
// throwing a control-flow sentinel from inside builder.Build(); it must pass through this catch, not be
// reported as a fatal crash — otherwise the factory times out waiting for the host.
catch (Exception ex) when (ex.GetType().Name is not ("StopTheHostException" or "HostAbortedException"))
{
    Log.Fatal(ex, "Paybitch API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed for <c>WebApplicationFactory&lt;Program&gt;</c> integration tests.</summary>
public partial class Program { }

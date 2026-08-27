using System.Reflection;
using Paybitch.Api.Features.Platform.Blob;
using Paybitch.Api.Features.Platform.Email;
using Paybitch.Api.Features.Platform.Jobs;

namespace Paybitch.Api.Features.Platform;

/// <summary>
/// Composition root for the E0 platform primitives (§0.4): the jobs substrate, <see cref="IBlobStore"/>,
/// and <see cref="IEmailSender"/>. Wired into <c>Program.cs</c> with a single
/// <c>builder.Services.AddPlatform(builder.Configuration)</c>. Every <see cref="IJobHandler"/> in the API
/// assembly is auto-discovered here, so an extension adds a handler simply by implementing the interface —
/// no edit to this file or <c>Program.cs</c>.
/// </summary>
public static class PlatformServiceRegistration
{
    public static IServiceCollection AddPlatform(this IServiceCollection services, IConfiguration config)
    {
        // --- Jobs substrate (§0.4.2) ---
        services.AddOptions<JobRunnerOptions>().Bind(config.GetSection(JobRunnerOptions.SectionName));
        services.AddScoped<IJobQueue, JobQueue>();
        RegisterJobHandlers(services);
        services.AddHostedService<JobRunner>();

        // --- Blob store (§0.4.3): filesystem dev default; S3-compatible in prod behind IBlobStore ---
        services.AddSingleton<IBlobStore>(sp => new FilesystemBlobStore(
            config["Blob:LocalRoot"],
            sp.GetRequiredService<ILogger<FilesystemBlobStore>>()));

        // --- Email transport (§0.4.4) ---
        // SMTP once a host is configured, otherwise the log transport, so a checkout with no
        // configuration still boots and dev keeps reading codes out of the log.
        services.AddOptions<SmtpOptions>()
            .Bind(config.GetSection(SmtpOptions.SectionName))
            .ValidateDataAnnotations();

        var smtp = config.GetSection(SmtpOptions.SectionName).Get<SmtpOptions>() ?? new SmtpOptions();

        if (smtp.IsConfigured)
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        else
            services.AddScoped<IEmailSender, LogEmailSender>();

        return services;
    }

    /// <summary>
    /// Discover and register every concrete <see cref="IJobHandler"/> in the API assembly as scoped, so
    /// <see cref="JobRunner"/> can resolve them per tick and index by <see cref="IJobHandler.Kind"/>.
    /// Mirrors the <c>IEndpointModule</c> self-registration convention.
    /// </summary>
    private static void RegisterJobHandlers(IServiceCollection services)
    {
        var handlerType = typeof(IJobHandler);
        var implementations = typeof(PlatformServiceRegistration).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && handlerType.IsAssignableFrom(t));

        foreach (var implementation in implementations)
            services.AddScoped(handlerType, implementation);
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace Paybitch.Api.Features.Recurring;

/// <summary>
/// DI registration for the E3 materialization worker. The endpoint module and validators self-register by
/// assembly scan, but the <c>RecurringScheduler</c> hosted service and its collaborator have no such
/// convention here — so this one line must run at composition time (Program.cs), mirroring the Activity
/// slice's <c>AddActivityWriter()</c>. Without it, rule CRUD still works but nothing ever fires.
/// </summary>
public static class RecurringFeatureExtensions
{
    public static IServiceCollection AddRecurringScheduler(this IServiceCollection services)
    {
        services.AddScoped<RecurringFireService>();
        services.AddHostedService<RecurringScheduler>();
        return services;
    }
}

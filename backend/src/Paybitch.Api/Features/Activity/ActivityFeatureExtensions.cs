using Microsoft.Extensions.DependencyInjection;

namespace Paybitch.Api.Features.Activity;

/// <summary>
/// DI registration for the activity write seam. The endpoint modules and validators self-register by
/// assembly scan, but a plain service has no such convention here — so this one line must run at
/// composition time (Program.cs) for <see cref="IActivityWriter"/> to be injectable into the other
/// slices' mutating handlers. Scoped: it shares the request's <c>AppDbContext</c>.
/// </summary>
public static class ActivityFeatureExtensions
{
    public static IServiceCollection AddActivityWriter(this IServiceCollection services)
    {
        services.AddScoped<IActivityWriter, ActivityWriter>();
        return services;
    }
}

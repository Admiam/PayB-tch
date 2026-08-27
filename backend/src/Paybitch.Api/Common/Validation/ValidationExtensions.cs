using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Paybitch.Api.Common.Validation;

/// <summary>
/// Validator discovery + the endpoint-filter helper. Equivalent to FluentValidation's
/// <c>AddValidatorsFromAssembly</c> without taking the (version-fragile) DI-extensions package:
/// every concrete <c>IValidator&lt;T&gt;</c> in the API assembly is registered as scoped, and
/// <see cref="WithValidation"/> attaches the <see cref="ValidationEndpointFilter"/> to an endpoint.
/// </summary>
public static class ValidationExtensions
{
    /// <summary>Scan the API assembly and register every concrete <c>IValidator&lt;T&gt;</c> as scoped.</summary>
    public static IServiceCollection AddApiValidators(this IServiceCollection services)
    {
        var assembly = typeof(ValidationExtensions).Assembly;

        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }))
        {
            foreach (var serviceType in type.GetInterfaces()
                         .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>)))
            {
                services.AddScoped(serviceType, type);
            }
        }

        return services;
    }

    /// <summary>Attach FluentValidation to an endpoint (or route group) as an endpoint filter.</summary>
    public static TBuilder WithValidation<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new ValidationEndpointFilter());
}

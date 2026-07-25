using System.Reflection;

namespace Paybitch.Api.Common.Endpoints;

/// <summary>
/// A feature slice's self-registration seam. Each slice implements this once and maps its own routes
/// — no slice ever edits Program.cs, the DbContext, or any shared file to add endpoints. The builder
/// passed in is the shared <c>/v1</c> route group, so a slice maps RELATIVE paths
/// (<c>app.MapGet("/currencies", …)</c> ⇒ <c>GET /v1/currencies</c>).
/// </summary>
public interface IEndpointModule
{
    /// <summary>Map this slice's endpoints. <paramref name="app"/> is the <c>/v1</c> route group.</summary>
    void MapEndpoints(IEndpointRouteBuilder app);
}

/// <summary>Reflection-based discovery + registration of every <see cref="IEndpointModule"/> in the API assembly.</summary>
public static class EndpointModuleExtensions
{
    /// <summary>
    /// Create the shared <c>/v1</c> route group, then instantiate and invoke every non-abstract
    /// <see cref="IEndpointModule"/> in the API assembly against it. Secure-by-default: the group
    /// requires an authenticated user, so a slice endpoint is authenticated unless it opts out with
    /// <c>.AllowAnonymous()</c> (auth exchange, invite preview, <c>GET /v1/currencies</c>).
    /// </summary>
    public static IEndpointRouteBuilder MapEndpointModules(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1").RequireAuthorization();

        foreach (var module in DiscoverModules(typeof(EndpointModuleExtensions).Assembly))
            module.MapEndpoints(v1);

        return v1;
    }

    private static IEnumerable<IEndpointModule> DiscoverModules(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => typeof(IEndpointModule).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (IEndpointModule)Activator.CreateInstance(t)!);
}

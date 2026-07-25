using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Infrastructure;

namespace Paybitch.Api.Features.Currencies;

/// <summary>The v1 currency set (static seed, D2). Unauthenticated — proves the <see cref="IEndpointModule"/>
/// convention end-to-end: this file self-registers <c>GET /v1/currencies</c> with no edit to Program.cs.</summary>
public sealed class CurrenciesModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/currencies", async (AppDbContext db, CancellationToken ct) =>
            {
                var currencies = await db.Currencies
                    .AsNoTracking()
                    .OrderBy(c => c.Code)
                    .Select(c => new CurrencyResponse(c.Code, c.MinorUnits, c.Symbol))
                    .ToListAsync(ct);
                return Results.Ok(currencies);
            })
            .AllowAnonymous()
            .WithName("ListCurrencies")
            .WithSummary("List the closed v1 currency set.")
            .WithTags("Currencies");
    }
}

/// <summary>Wire shape for a currency row (§3, D2 scale authority).</summary>
public sealed record CurrencyResponse(string Code, int MinorUnits, string Symbol);

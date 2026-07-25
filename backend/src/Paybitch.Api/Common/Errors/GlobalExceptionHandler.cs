using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Paybitch.Domain;

namespace Paybitch.Api.Common.Errors;

/// <summary>
/// The backstop <see cref="IExceptionHandler"/> (§5): maps domain and framework exceptions that
/// escape a handler to RFC 9457 problem+json. It NEVER echoes raw exception text or ids (D6, §3.4)
/// — a domain rule surfaces as its stable catalog code with a fixed detail. Unmapped exceptions are
/// left to the default <c>AddProblemDetails</c> pipeline (a code-less 500, no leak).
///
/// These mappings are a safety net, not the primary path: the boundary FluentValidation twins (§3.4)
/// are expected to reject bad split/currency/amount input with the specific 422 codes before any
/// domain type is constructed. A domain exception reaching here means a twin is missing (a bug).
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var result = Map(exception);
        if (result is null)
            return false; // let the default 500 problem pipeline handle it (no raw text leak)

        logger.LogWarning(
            exception,
            "Handled {ExceptionType} at {Path} → problem response",
            exception.GetType().Name,
            httpContext.Request.Path);

        await result.ExecuteAsync(httpContext);
        return true;
    }

    private static IResult? Map(Exception exception) => exception switch
    {
        // EXACT/SHARES/PERCENTAGE resolution failure — boundary twins own the specific split_* codes;
        // this backstops to the generic 422 rather than 500.
        InvalidSplitException => Problems.Validation(
            ProblemCodes.ValidationFailed,
            "split",
            "The expense split is invalid."),

        UnsupportedCurrencyException => Problems.Validation(
            ProblemCodes.UnsupportedCurrency,
            "currency",
            "Currency is not supported in v1."),

        // Combining two currencies is a server-side money violation; backstop to a generic 422.
        CurrencyMismatchException => Problems.Validation(
            ProblemCodes.ValidationFailed,
            "currency",
            "Currency mismatch between money values."),

        // Any other domain-rule violation.
        PaybitchDomainException => Problems.ValidationFailed(
            [new ProblemError("request", "The request violates a domain rule.")]),

        // Optimistic concurrency at SaveChanges — handlers should pre-empt this and return the
        // current representation via Problems.VersionConflict; here we can only emit the code.
        DbUpdateConcurrencyException => Problems.Create(
            StatusCodes.Status412PreconditionFailed,
            ProblemCodes.VersionConflict),

        // Kestrel MaxRequestBodySize trip surfaces as a 413 BadHttpRequestException.
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => Problems.PayloadTooLarge(),

        _ => null,
    };
}

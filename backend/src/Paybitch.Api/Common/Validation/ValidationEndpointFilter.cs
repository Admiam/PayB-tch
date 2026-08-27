using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Paybitch.Api.Common.Errors;

namespace Paybitch.Api.Common.Validation;

/// <summary>
/// Runs FluentValidation for every endpoint argument that has a registered <c>IValidator&lt;T&gt;</c>
/// (§5 — validation as an endpoint filter). On failure it short-circuits with a 422 problem+json
/// carrying <c>errors[]</c> (§3.4).
///
/// Top-level <c>code</c> resolution (the twin convention): if the failures share exactly one
/// FluentValidation <c>ErrorCode</c> that is a known catalog code — set by the validator via
/// <c>.WithErrorCode(ProblemCodes.TitleLength)</c> — that code becomes the problem code; otherwise it
/// is the generic <see cref="ProblemCodes.ValidationFailed"/>. Default validator-type error codes
/// (e.g. "NotEmptyValidator") are ignored because they are not in the catalog.
/// </summary>
public sealed class ValidationEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var ct = context.HttpContext.RequestAborted;
        var failures = new List<ValidationFailure>();

        foreach (var argument in context.Arguments)
        {
            if (argument is null)
                continue;

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (services.GetService(validatorType) is not IValidator validator)
                continue;

            var validationContext = new ValidationContext<object>(argument);
            var result = await validator.ValidateAsync(validationContext, ct);
            if (!result.IsValid)
                failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
            return await next(context);

        var errors = failures
            .Select(f => new ProblemError(ToCamelCase(f.PropertyName), f.ErrorMessage))
            .ToArray();

        var catalogCodes = failures
            .Select(f => f.ErrorCode)
            .Where(c => !string.IsNullOrEmpty(c) && ProblemCodes.All.Contains(c))
            .Distinct()
            .ToArray();

        var code = catalogCodes.Length == 1 ? catalogCodes[0] : ProblemCodes.ValidationFailed;
        return Problems.Validation(code, errors);
    }

    // FluentValidation reports PascalCase property paths; the wire uses camelCase field names.
    private static string ToCamelCase(string property)
    {
        if (string.IsNullOrEmpty(property) || !char.IsUpper(property[0]))
            return property;
        return char.ToLowerInvariant(property[0]) + property[1..];
    }
}

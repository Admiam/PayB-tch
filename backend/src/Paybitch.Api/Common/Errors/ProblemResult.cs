using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Paybitch.Api.Common.Errors;

/// <summary>
/// An RFC 9457 <c>application/problem+json</c> response. A hand-rolled <see cref="IResult"/> (rather
/// than <c>Results.Problem</c>) so helpers can set response headers — <c>Retry-After</c> on 429,
/// <c>ETag</c> alongside a 412 — and flatten arbitrary members (<c>code</c>, <c>errors</c>,
/// <c>existingId</c>, <c>current</c>) into the top-level object. The body is assembled as an ordered
/// dictionary so the literal member names are emitted verbatim.
/// </summary>
public sealed class ProblemResult : IResult
{
    private const string ContentType = "application/problem+json; charset=utf-8";

    private readonly int _status;
    private readonly string _code;
    private readonly string _title;
    private readonly string? _detail;
    private readonly IReadOnlyDictionary<string, object?>? _extensions;
    private readonly IReadOnlyDictionary<string, string>? _headers;

    // Serialize members with web defaults so nested records (ProblemError, representations) are camelCase.
    private static readonly JsonSerializerOptions BodyJsonOptions = new(JsonSerializerDefaults.Web);

    public ProblemResult(
        int status,
        string code,
        string title,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        _status = status;
        _code = code;
        _title = title;
        _detail = detail;
        _extensions = extensions;
        _headers = headers;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = _status;
        response.ContentType = ContentType;

        if (_headers is not null)
            foreach (var (name, value) in _headers)
                response.Headers[name] = value;

        var body = new Dictionary<string, object?>
        {
            ["type"] = ProblemCodes.TypeFor(_code),
            ["title"] = _title,
            ["status"] = _status,
            ["code"] = _code,
        };
        if (_detail is not null)
            body["detail"] = _detail;
        if (_extensions is not null)
            foreach (var (key, value) in _extensions)
                body[key] = value;

        await response.WriteAsJsonAsync(body, BodyJsonOptions, contentType: ContentType, httpContext.RequestAborted);
    }
}

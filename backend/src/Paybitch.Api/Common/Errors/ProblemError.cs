using System.Text.Json.Serialization;

namespace Paybitch.Api.Common.Errors;

/// <summary>
/// One per-field entry in a problem+json <c>errors[]</c> array (§3.4, Appendix B worked shape).
/// Serialized as <c>{ "field": …, "message": … }</c>.
/// </summary>
public sealed record ProblemError(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("message")] string Message);

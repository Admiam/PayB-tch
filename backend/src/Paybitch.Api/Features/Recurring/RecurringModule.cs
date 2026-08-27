using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Validation;

namespace Paybitch.Api.Features.Recurring;

/// <summary>
/// Self-registers the recurring-rule routes under <c>/v1/groups/{groupId}/recurring-rules</c> (§3.4). The
/// whole surface is member-scoped (D6, EXT-D3l): a group-level <c>.RequireGroupMembership()</c> runs first
/// (404 for non-members), before body validation and the handler's archived / idempotency / If-Match checks.
/// </summary>
public sealed class RecurringModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var rules = app
            .MapGroup("/groups/{groupId}/recurring-rules")
            .RequireGroupMembership()
            .WithTags("Recurring");

        rules.MapPost("", RecurringEndpoints.CreateAsync)
            .WithValidation()
            .WithName("CreateRecurringRule")
            .WithSummary("Create a recurring expense rule (clientId idempotency, D9).");

        rules.MapGet("", RecurringEndpoints.ListAsync)
            .WithName("ListRecurringRules")
            .WithSummary("List a group's active + paused rules (cursor envelope, §3.4).");

        rules.MapGet("/{ruleId}", RecurringEndpoints.GetAsync)
            .WithName("GetRecurringRule")
            .WithSummary("Fetch a single recurring rule with its split template (§3.4).");

        rules.MapPut("/{ruleId}", RecurringEndpoints.ReplaceAsync)
            .WithValidation()
            .WithName("ReplaceRecurringRule")
            .WithSummary("Full-aggregate replace; future-only; If-Match required (EXT-D3h).");

        rules.MapDelete("/{ruleId}", RecurringEndpoints.DeleteAsync)
            .WithName("DeleteRecurringRule")
            .WithSummary("Soft-delete a rule; If-Match required; past expenses kept (EXT-D3h).");

        rules.MapPost("/{ruleId}/pause", RecurringEndpoints.PauseAsync)
            .WithName("PauseRecurringRule")
            .WithSummary("active → paused; If-Match required; idempotent (EXT-D3i).");

        rules.MapPost("/{ruleId}/resume", RecurringEndpoints.ResumeAsync)
            .WithName("ResumeRecurringRule")
            .WithSummary("paused → active, roll next_run_at forward; If-Match required (EXT-D3i).");

        rules.MapGet("/{ruleId}/occurrences", RecurringEndpoints.OccurrencesAsync)
            .WithName("PreviewRecurringOccurrences")
            .WithSummary("Computed occurrence preview (X2 — no stored calendar, §3.4).");
    }
}

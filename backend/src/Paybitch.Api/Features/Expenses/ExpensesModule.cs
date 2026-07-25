using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Validation;

namespace Paybitch.Api.Features.Expenses;

/// <summary>
/// Self-registers the expense aggregate routes under <c>/v1/groups/{groupId}/expenses</c> (§3.2, §3.2b,
/// §3.4). The whole surface is member-scoped (D6): a group-level <c>.RequireGroupMembership()</c> runs
/// first (404 for non-members), before body validation and the handler's archived / idempotency /
/// If-Match checks.
/// </summary>
public sealed class ExpensesModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var expenses = app
            .MapGroup("/groups/{groupId}/expenses")
            .RequireGroupMembership()
            .WithTags("Expenses");

        expenses.MapPost("", ExpenseEndpoints.CreateAsync)
            .WithValidation()
            .WithName("CreateExpense")
            .WithSummary("Create an expense with a server-authoritative split (§3.2).");

        expenses.MapGet("", ExpenseEndpoints.ListAsync)
            .WithName("ListExpenses")
            .WithSummary("List a group's expenses, newest first (cursor envelope, §3.4).");

        expenses.MapGet("/{expenseId}", ExpenseEndpoints.GetAsync)
            .WithName("GetExpense")
            .WithSummary("Fetch a single expense with its resolved shares (§3.2).");

        expenses.MapPut("/{expenseId}", ExpenseEndpoints.ReplaceAsync)
            .WithValidation()
            .WithName("ReplaceExpense")
            .WithSummary("Full-aggregate replace; If-Match required (§3.2b).");

        expenses.MapDelete("/{expenseId}", ExpenseEndpoints.DeleteAsync)
            .WithName("DeleteExpense")
            .WithSummary("Soft-delete an expense; If-Match required (§3.2b).");
    }
}

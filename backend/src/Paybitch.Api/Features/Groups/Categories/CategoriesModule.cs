using Microsoft.EntityFrameworkCore;
using Paybitch.Api.Common.Auth;
using Paybitch.Api.Common.Endpoints;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Common.Validation;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Groups.Categories;

/// <summary>
/// Categories (§3.9). Global presets are static seed data (localized client-side by id); group customs
/// are member-authored. Uniqueness is case-insensitive against BOTH the group's customs and the preset
/// names (the partial indexes can't span both scopes, so the preset half is a boundary check).
/// </summary>
public sealed class CategoriesModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/categories", ListPresets)
            .WithName("ListPresetCategories")
            .WithSummary("Global preset categories (static seed).")
            .WithTags("Categories");

        app.MapGet("/groups/{groupId}/categories", ListGroupCategories)
            .RequireGroupMembership()
            .WithName("ListGroupCategories")
            .WithSummary("List a group's custom categories.")
            .WithTags("Categories");

        app.MapPost("/groups/{groupId}/categories", CreateCategory)
            .RequireGroupMembership()
            .RequireGroupNotArchived()
            .WithValidation()
            .WithName("CreateGroupCategory")
            .WithSummary("Create a group custom category (any member).")
            .WithTags("Categories");

        app.MapDelete("/groups/{groupId}/categories/{categoryId}", DeleteCategory)
            .RequireGroupAdmin()
            .RequireGroupNotArchived()
            .WithName("DeleteGroupCategory")
            .WithSummary("Soft-delete a group custom category (admin).")
            .WithTags("Categories");
    }

    // --- GET /categories (presets) ---
    private static async Task<IResult> ListPresets(AppDbContext db, CancellationToken ct)
    {
        var presets = await db.Categories.AsNoTracking()
            .Where(c => c.GroupId == null && c.DeletedAt == null)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryResponse(c.Id, c.GroupId, c.Name, c.IconSymbol))
            .ToListAsync(ct);
        return Results.Ok(presets);
    }

    // --- GET /groups/{g}/categories (customs) ---
    private static async Task<IResult> ListGroupCategories(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var customs = await db.Categories.AsNoTracking()
            .Where(c => c.GroupId == membership.GroupId && c.DeletedAt == null)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryResponse(c.Id, c.GroupId, c.Name, c.IconSymbol))
            .ToListAsync(ct);
        return Results.Ok(customs);
    }

    // --- POST /groups/{g}/categories ---
    private static async Task<IResult> CreateCategory(
        CreateCategoryRequest req, HttpContext http, AppDbContext db,
        IChangeLogWriter changeLog, IClock clock, CancellationToken ct)
    {
        var membership = http.GetMembership();
        var name = req.Name.Trim();
        var lowered = name.ToLowerInvariant();

        // Case-insensitive collision against the group's customs OR a preset name (§3.9).
        var exists = await db.Categories.AnyAsync(c =>
            c.DeletedAt == null
            && (c.GroupId == membership.GroupId || c.GroupId == null)
            && c.Name.ToLower() == lowered, ct);
        if (exists)
            return Problems.Conflict(ProblemCodes.CategoryExists);

        var category = new Category
        {
            GroupId = membership.GroupId,
            Name = name,
            IconSymbol = string.IsNullOrEmpty(req.IconSymbol) ? null : req.IconSymbol,
            CreatedAt = clock.UtcNow,
        };
        db.Categories.Add(category);
        changeLog.Append(membership.GroupId, ChangeLogEntityTypes.Category, category.Id, isDelete: false);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race to the uq_categories_group unique index — same outcome as the boundary check.
            return Problems.Conflict(ProblemCodes.CategoryExists);
        }

        return Results.Created(
            $"/v1/groups/{membership.GroupId}/categories/{category.Id}",
            CategoryResponse.From(category));
    }

    // --- DELETE /groups/{g}/categories/{categoryId} ---
    private static async Task<IResult> DeleteCategory(
        Guid categoryId, HttpContext http, AppDbContext db,
        IChangeLogWriter changeLog, IClock clock, CancellationToken ct)
    {
        var membership = http.GetMembership();

        // Preset ids (group_id NULL) are outside group scope ⇒ never matched ⇒ 404 (D6 posture, §3.9).
        var category = await db.Categories
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.GroupId == membership.GroupId && c.DeletedAt == null, ct);
        if (category is null)
            return Problems.NotFound();

        category.DeletedAt = clock.UtcNow;
        changeLog.Append(membership.GroupId, ChangeLogEntityTypes.Category, category.Id, isDelete: true);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}

using FluentValidation;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Groups.Categories;

/// <summary><c>POST /groups/{g}/categories</c> body — a group custom category (§3.9).</summary>
public sealed record CreateCategoryRequest(string Name, string? IconSymbol);

/// <summary>A category row (preset when <c>groupId</c> is null; group custom otherwise).</summary>
public sealed record CategoryResponse(Guid Id, Guid? GroupId, string Name, string? IconSymbol)
{
    public static CategoryResponse From(Category c) => new(c.Id, c.GroupId, c.Name, c.IconSymbol);
}

public sealed class CreateCategoryRequestValidator : AbstractValidator<CreateCategoryRequest>
{
    public CreateCategoryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.IconSymbol).MaximumLength(100).When(x => x.IconSymbol is not null);
    }
}

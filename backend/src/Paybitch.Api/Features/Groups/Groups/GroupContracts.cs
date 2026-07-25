using System.Globalization;
using FluentValidation;
using Paybitch.Api.Common.Errors;
using Paybitch.Api.Features.Groups.Support;
using Paybitch.Infrastructure.Entities;

namespace Paybitch.Api.Features.Groups.Groups;

// --- Requests ---

/// <summary><c>POST /groups</c> body. Caller becomes the group's first owner (§3.1).</summary>
public sealed record CreateGroupRequest(string Name, string DefaultCurrency, string? IconSymbol);

/// <summary><c>PATCH /groups/{g}</c> body — rename and/or unarchive (§3.7). <c>archived</c> may only be <c>false</c>.</summary>
public sealed record PatchGroupRequest(string? Name, bool? Archived);

// --- Responses ---

/// <summary>Caller's per-currency net in one group; <c>net</c> is D1 string minor units (§3.12/G26).</summary>
public sealed record CurrencyNetResponse(string Currency, string Net);

/// <summary><c>GET /groups</c> list item — header + the caller's own nets only (§3.12/G26).</summary>
public sealed record GroupListItemResponse(
    Guid Id,
    string Name,
    string? Icon,
    string DefaultCurrency,
    int MemberCount,
    bool Archived,
    int Version,
    IReadOnlyList<CurrencyNetResponse> CallerBalance);

/// <summary>A single group's header (detail / create / patch response).</summary>
public sealed record GroupResponse(
    Guid Id,
    string Name,
    string? Icon,
    string DefaultCurrency,
    int MemberCount,
    bool Archived,
    int Version,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static GroupResponse From(Group g, int memberCount) => new(
        g.Id, g.Name, g.IconSymbol, g.DefaultCurrency, memberCount,
        g.ArchivedAt is not null, g.Version, g.CreatedBy, g.CreatedAt);
}

// --- Validators ---

public sealed class CreateGroupRequestValidator : AbstractValidator<CreateGroupRequest>
{
    public CreateGroupRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().MaximumLength(100)
            .WithErrorCode(ProblemCodes.NameLength);

        RuleFor(x => x.DefaultCurrency)
            .Must(SupportedCurrencies.IsSupported)
            .WithErrorCode(ProblemCodes.UnsupportedCurrency)
            .WithMessage("Currency is not supported in v1.");

        RuleFor(x => x.IconSymbol)
            .MaximumLength(100)
            .When(x => x.IconSymbol is not null);
    }
}

public sealed class PatchGroupRequestValidator : AbstractValidator<PatchGroupRequest>
{
    public PatchGroupRequestValidator()
    {
        When(x => x.Name is not null, () =>
            RuleFor(x => x.Name!)
                .NotEmpty().MaximumLength(100)
                .WithErrorCode(ProblemCodes.NameLength));

        // Archive is the DELETE verb (§3.7); PATCH only ever clears the flag.
        RuleFor(x => x.Archived)
            .Must(a => a != true)
            .WithMessage("archived can only be set to false (unarchive); archive via DELETE /groups/{g}.");
    }
}

/// <summary>Shared helpers for reading list query params.</summary>
internal static class ListQuery
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    public static int ClampLimit(string? raw)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return Math.Clamp(n, 1, MaxLimit);
        return DefaultLimit;
    }
}

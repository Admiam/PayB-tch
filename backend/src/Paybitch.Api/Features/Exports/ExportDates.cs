using Paybitch.Api.Features.Expenses;

namespace Paybitch.Api.Features.Exports;

/// <summary>
/// Export date-window resolution (§4.4). Explicit <c>from</c>/<c>to</c> are plain local dates (§1.3) that
/// bucket / filter verbatim; an omitted bound falls back to the ledger floor (lower) or "today" (upper).
/// The strict ISO parse lives in <see cref="ExpenseWire.TryParseDate"/> so the wire format is one source.
/// </summary>
public static class ExportDates
{
    /// <summary>Lower-bound default — matches the §3.4 expense minimum date.</summary>
    public static readonly DateOnly Floor = new(2000, 1, 1);

    /// <summary>
    /// Resolve a (possibly open-ended) window: a parseable bound is used verbatim; a null / unparseable
    /// lower bound becomes <see cref="Floor"/>, an upper bound becomes <paramref name="today"/>.
    /// </summary>
    public static (DateOnly From, DateOnly To) Resolve(string? from, string? to, DateOnly today)
    {
        var f = ExpenseWire.TryParseDate(from, out var parsedFrom) ? parsedFrom : Floor;
        var t = ExpenseWire.TryParseDate(to, out var parsedTo) ? parsedTo : today;
        return (f, t);
    }
}

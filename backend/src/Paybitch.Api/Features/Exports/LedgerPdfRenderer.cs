using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Paybitch.Api.Features.Exports;

/// <summary>
/// The PDF-engine seam (EXT-D4b). Isolating QuestPDF behind this interface means the community→professional
/// license swap at the $1M revenue threshold is a registration change, not a re-architecture.
/// </summary>
public interface IPdfRenderer
{
    /// <summary>Render <paramref name="statement"/> to <paramref name="output"/> as a PDF.</summary>
    Task RenderStatementAsync(LedgerStatement statement, Stream output, CancellationToken ct);
}

/// <summary>
/// QuestPDF-backed <see cref="IPdfRenderer"/> (EXT-D4b) — pure .NET, no headless Chromium. Uses QuestPDF's
/// bundled Lato family, which covers Latin Extended-A (the Czech diacritics <c>ř ž ť č ň ů á é í ó ď</c>)
/// and is embedded <b>subsetted</b> in the output — the §4.8 acceptance target (zero <c>.notdef</c> tofu).
/// Money is formatted by <see cref="MoneyText"/> integer surgery (D1/D2), never through <c>double</c>.
/// </summary>
/// <remarks>
/// Licensed under the QuestPDF <b>Community License</b> (free while gross revenue &lt; $1M USD, EXT-D4b) —
/// set once here. Crossing the threshold is a license purchase, flagged as a revenue-trigger, not a TODO.
/// </remarks>
public sealed class LedgerPdfRenderer : IPdfRenderer
{
    static LedgerPdfRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public Task RenderStatementAsync(LedgerStatement statement, Stream output, CancellationToken ct)
    {
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9));

                ComposeHeader(page.Header(), statement);
                ComposeContent(page.Content(), statement);
                page.Footer().AlignCenter().Text(txt =>
                {
                    txt.Span("Page ");
                    txt.CurrentPageNumber();
                    txt.Span(" / ");
                    txt.TotalPages();
                });
            });
        }).GeneratePdf(output);

        return Task.CompletedTask;
    }

    private static void ComposeHeader(IContainer header, LedgerStatement s) =>
        header.Column(col =>
        {
            col.Item().Text("Paybitch").FontSize(20).Bold();
            if (!string.IsNullOrEmpty(s.GroupName))
                col.Item().Text(s.GroupName).FontSize(12).SemiBold();
            col.Item().Text($"{s.From:yyyy-MM-dd} – {s.To:yyyy-MM-dd}").FontSize(9).FontColor(Colors.Grey.Darken1);
        });

    private static void ComposeContent(IContainer content, LedgerStatement s) =>
        content.PaddingVertical(10).Column(col =>
        {
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(70);  // date
                    columns.RelativeColumn(3);   // title
                    columns.RelativeColumn(2);   // paid by
                    columns.RelativeColumn(2);   // category
                    columns.ConstantColumn(95);  // amount
                });

                table.Header(header =>
                {
                    HeaderCell(header.Cell(), "Date");
                    HeaderCell(header.Cell(), "Title");
                    HeaderCell(header.Cell(), "Paid by");
                    HeaderCell(header.Cell(), "Category");
                    header.Cell().AlignRight().Text("Amount").SemiBold();
                });

                foreach (var row in s.Rows)
                {
                    BodyCell(table.Cell(), row.Date.ToString("yyyy-MM-dd"));
                    BodyCell(table.Cell(), row.Title);
                    BodyCell(table.Cell(), row.PaidByName);
                    BodyCell(table.Cell(), row.Category ?? string.Empty);
                    table.Cell().PaddingVertical(2).AlignRight()
                        .Text($"{MoneyText.Human(row.AmountMinor, row.Scale, '.')} {row.Currency}");
                }
            });

            // Per-currency totals — never summed across (D5).
            col.Item().PaddingTop(12).AlignRight().Column(totals =>
            {
                foreach (var total in s.Totals)
                    totals.Item().Text($"Total {total.Currency}: {MoneyText.Human(total.TotalMinor, total.Scale, '.')}")
                        .SemiBold();
            });
        });

    private static void HeaderCell(IContainer cell, string text) =>
        cell.PaddingVertical(2).Text(text).SemiBold();

    private static void BodyCell(IContainer cell, string text) =>
        cell.PaddingVertical(2).Text(text);
}

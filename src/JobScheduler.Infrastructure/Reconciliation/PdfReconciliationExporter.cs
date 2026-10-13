using System.Globalization;
using JobScheduler.Application.Reconciliation;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Reconciliation;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace JobScheduler.Infrastructure.Reconciliation;

/// <summary>
/// Printable record-level report: job header, a per-rule summary and the records, failures first.
/// A PDF is for reading, so very large runs are cut off with a clear note (the CSV export has every record).
/// </summary>
public class PdfReconciliationExporter : IReconciliationExporter
{
    public const int MaxRecords = 2000;

    static PdfReconciliationExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // Keys and reasons come from user-uploaded files and can hold any script. A glyph the font lacks should show
        // as a placeholder, not fail the whole export (the CSV export keeps the exact text).
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }

    public string Format => "pdf";

    public ExportedFile Export(ReconciliationReport report)
    {
        var job = report.Job;
        var summary = report.Summary;

        // Failures first, in their original order within each group.
        var ordered = report.Results.OrderBy(r => r.Status == RecordStatus.Fail ? 0 : 1).ToList();
        var shown = ordered.Take(MaxRecords).ToList();
        var filter = string.Join(", ", new[]
        {
            report.StatusFilter is { } s ? $"status {s}" : null,
            report.RuleFilterName is { } n ? $"rule '{n}'" : null
        }.Where(x => x is not null));

        var pdf = Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(30);
            page.DefaultTextStyle(t => t.FontSize(8.5f));

            page.Header().Column(col =>
            {
                col.Item().Text($"Reconciliation results: {job.Name}").FontSize(16).SemiBold();
                col.Item().Text(
                    $"{job.TemplateName} · team {job.Team} · owner {report.OwnerEmail ?? job.OwnerId.ToString()} · run {report.RunId} ({summary.RunStatus})")
                    .FontColor(Colors.Grey.Darken2);
                col.Item().PaddingBottom(8).Text(
                    $"Generated {Ist(DateTime.UtcNow)} IST · {summary.Total} record checks, {summary.Passed} passed, {summary.Failed} failed"
                    + (filter.Length > 0 ? $" · filtered: {filter}" : ""))
                    .FontColor(Colors.Grey.Darken1);
            });

            page.Content().Column(col =>
            {
                col.Spacing(10);
                col.Item().Element(c => RuleSummary(c, summary));

                if (report.Results.Count == 0)
                {
                    col.Item().Text("No records match the filter.");
                    return;
                }

                if (ordered.Count > shown.Count)
                    col.Item().Text(
                        $"Showing the first {shown.Count} of {ordered.Count} records (failures first). Use the CSV export for every record.")
                        .FontColor(Colors.Red.Darken2).SemiBold();

                col.Item().Element(c => Records(c, shown));
            });

            page.Footer().AlignCenter().Text(t =>
            {
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
            });
        })).GeneratePdf();

        return new ExportedFile(pdf, "application/pdf", "pdf");
    }

    private static void RuleSummary(IContainer container, RunReconciliationSummaryDto summary) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(2f);   // rule
                cols.RelativeColumn(5f);   // definition
                cols.ConstantColumn(50);   // total
                cols.ConstantColumn(50);   // passed
                cols.ConstantColumn(50);   // failed
            });

            table.Header(h =>
            {
                foreach (var title in new[] { "Rule", "Definition", "Total", "Passed", "Failed" })
                    h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text(title).SemiBold();
            });

            foreach (var r in summary.Rules)
            {
                Cell(table, r.RuleName);
                Cell(table, r.Description);
                Cell(table, r.Total.ToString(CultureInfo.InvariantCulture));
                Cell(table, r.Passed.ToString(CultureInfo.InvariantCulture));
                Cell(table, r.Failed.ToString(CultureInfo.InvariantCulture), r.Failed > 0 ? Colors.Red.Darken2 : (string?)null);
            }
        });

    private static void Records(IContainer container, IReadOnlyList<ReconciliationResultDto> rows) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn(1.6f);   // rule
                cols.RelativeColumn(1.4f);   // key
                cols.RelativeColumn(1.2f);   // left
                cols.RelativeColumn(1.2f);   // right
                cols.RelativeColumn(1.1f);   // difference
                cols.ConstantColumn(34);     // status
                cols.RelativeColumn(4f);     // reason
            });

            table.Header(h =>
            {
                foreach (var title in new[] { "Rule", "Record", "Left", "Right", "Difference", "Status", "Reason" })
                    h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text(title).SemiBold();
            });

            foreach (var r in rows)
            {
                var fail = r.Status == RecordStatus.Fail;
                Cell(table, r.RuleName);
                Cell(table, r.RecordKey);
                Cell(table, Num(r.LeftValue));
                Cell(table, Num(r.RightValue));
                Cell(table, Num(r.Difference));
                Cell(table, fail ? "FAIL" : "PASS", fail ? Colors.Red.Darken2 : Colors.Green.Darken2);
                Cell(table, r.Reason);
            }
        });

    private static void Cell(TableDescriptor table, string text, string? color = null)
    {
        var cell = table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);
        if (color is null) cell.Text(text);
        else cell.Text(text).FontColor(color);
    }

    private static string Num(decimal? value) =>
        value is { } v ? v.ToString("0.00##########", CultureInfo.InvariantCulture) : "-";

    private static string Ist(DateTime utc) =>
        Domain.Jobs.Ist.FromUtc(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

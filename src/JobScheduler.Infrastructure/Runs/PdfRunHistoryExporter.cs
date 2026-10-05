using System.Globalization;
using JobScheduler.Application.Runs;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace JobScheduler.Infrastructure.Runs;

/// <summary>PDF export of a job's run history (PRD 4.8). Times are shown in IST. QuestPDF Community license.</summary>
public class PdfRunHistoryExporter : IRunHistoryExporter
{
    static PdfRunHistoryExporter() => QuestPDF.Settings.License = LicenseType.Community;

    public string Format => "pdf";

    public ExportedFile Export(RunHistoryReport report)
    {
        var job = report.Job;
        var runs = report.Runs.OrderBy(r => r.CreatedAtUtc).ToList();

        var pdf = Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(30);
            page.DefaultTextStyle(t => t.FontSize(9));

            page.Header().Column(col =>
            {
                col.Item().Text($"Run history: {job.Name}").FontSize(16).SemiBold();
                col.Item().Text(
                    $"{job.TemplateName} · {job.ScheduleType} · team {job.Team} · owner {report.OwnerEmail ?? job.OwnerId.ToString()} · status {job.Status}")
                    .FontColor(Colors.Grey.Darken2);
                col.Item().PaddingBottom(8).Text(
                    $"Generated {Ist(DateTime.UtcNow)} IST · {runs.Count} run(s)").FontColor(Colors.Grey.Darken1);
            });

            page.Content().Element(c =>
            {
                if (runs.Count == 0)
                {
                    c.Text("This job has no runs yet.");
                    return;
                }

                c.Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(1.1f);  // status
                        cols.RelativeColumn(1.2f);  // attempts
                        cols.RelativeColumn(1.5f);  // started
                        cols.RelativeColumn(1.5f);  // finished
                        cols.RelativeColumn(1.8f);  // failed step / error
                        cols.RelativeColumn(2.6f);  // idempotency key
                        cols.RelativeColumn(2.2f);  // steps
                    });

                    table.Header(h =>
                    {
                        foreach (var title in new[] { "Status", "Attempts", "Started (IST)", "Finished (IST)", "Failure", "Idempotency key", "Steps" })
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(4).Text(title).SemiBold();
                    });

                    foreach (var r in runs)
                    {
                        Cell(table, r.Status.ToString());
                        Cell(table, $"{r.Attempt} (auto-retries {r.AutoRetriesUsed})");
                        Cell(table, Ist(r.StartedAtUtc));
                        Cell(table, Ist(r.FinishedAtUtc));
                        Cell(table, r.Error is null ? "" : $"{r.FailedStep}: {r.Error}");
                        Cell(table, r.IdempotencyKey);
                        Cell(table, string.Join("\n", r.Steps.Select(s => $"{s.Step} #{s.Attempt}: {s.Status}")));
                    }
                });
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

    private static void Cell(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(text);

    private static string Ist(DateTime? utc) =>
        utc is { } t ? Domain.Jobs.Ist.FromUtc(t).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
}

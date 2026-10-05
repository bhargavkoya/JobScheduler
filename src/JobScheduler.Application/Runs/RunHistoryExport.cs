using System.Text;
using JobScheduler.Application.Jobs;

namespace JobScheduler.Application.Runs;

/// <summary>Everything an exporter needs: the job header and its runs.</summary>
public sealed record RunHistoryReport(JobDto Job, string? OwnerEmail, IReadOnlyList<JobRunDto> Runs);

public sealed record ExportedFile(byte[] Content, string ContentType, string FileExtension);

/// <summary>One output format of the run-history export (PRD 4.8). Add a format by adding an implementation.</summary>
public interface IRunHistoryExporter
{
    /// <summary>The value of the "format" query parameter that selects this exporter, e.g. "csv".</summary>
    string Format { get; }

    ExportedFile Export(RunHistoryReport report);
}

public class CsvRunHistoryExporter : IRunHistoryExporter
{
    public string Format => "csv";

    public ExportedFile Export(RunHistoryReport report) =>
        new(Encoding.UTF8.GetBytes(RunHistoryCsv.Build(report.Runs)), "text/csv", "csv");
}

using System.Globalization;
using System.Text;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Domain.Runs;

namespace JobScheduler.Application.Reconciliation;

/// <summary>One output format of the record-level export. Add a format by adding an implementation.</summary>
public interface IReconciliationExporter
{
    /// <summary>The value of the "format" query parameter that selects this exporter, e.g. "csv".</summary>
    string Format { get; }

    ExportedFile Export(ReconciliationReport report);
}

public interface IReconciliationResultsService
{
    Task<RunReconciliationSummaryDto> GetSummaryAsync(Guid runId, CancellationToken ct);

    Task<ResultsPageDto> ListAsync(
        Guid runId, RecordStatus? status, Guid? ruleId, int page, int pageSize, CancellationToken ct);

    /// <summary>Every matching record of the run in the requested format ("csv" or "pdf"). Unknown formats are a validation error.</summary>
    Task<ExportedFile> ExportAsync(Guid runId, string? format, RecordStatus? status, Guid? ruleId, CancellationToken ct);
}

/// <summary>Read side of a file reconciliation run: summary, paged records and export. Same team visibility as the job.</summary>
public class ReconciliationResultsService(
    IReconciliationStore store, IJobRunStore runs, IJobStore jobs, IUserStore users, ICurrentUser me,
    IEnumerable<IReconciliationExporter> exporters, TimeProvider clock, AtRiskPolicy atRisk) : IReconciliationResultsService
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    public async Task<RunReconciliationSummaryDto> GetSummaryAsync(Guid runId, CancellationToken ct)
    {
        var (run, job) = await LoadAsync(runId, ct);
        return await BuildSummaryAsync(run, job, ct);
    }

    public async Task<ResultsPageDto> ListAsync(
        Guid runId, RecordStatus? status, Guid? ruleId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) throw new ValidationException("The page number starts at 1.");
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw new ValidationException($"The page size must be between 1 and {MaxPageSize}.");
        await LoadAsync(runId, ct);

        var total = await store.CountAsync(runId, status, ruleId, ct);
        var rows = await store.QueryAsync(runId, status, ruleId, (page - 1) * pageSize, pageSize, ct);
        return new ResultsPageDto(rows.Select(r => r.ToDto()).ToList(), total, page, pageSize);
    }

    public async Task<ExportedFile> ExportAsync(
        Guid runId, string? format, RecordStatus? status, Guid? ruleId, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
        var exporter = exporters.FirstOrDefault(e => e.Format == wanted)
            ?? throw new ValidationException(
                $"Unsupported export format '{format}'. Use one of: {string.Join(", ", exporters.Select(e => e.Format))}.");

        var (run, job) = await LoadAsync(runId, ct);
        var summary = await BuildSummaryAsync(run, job, ct);
        var ruleName = ruleId is { } id
            ? summary.Rules.FirstOrDefault(r => r.RuleId == id)?.RuleName
              ?? throw new ValidationException("That rule does not belong to this run's job.")
            : null;

        var owner = await users.FindByIdAsync(job.OwnerId, ct);
        var rows = await store.QueryAsync(runId, status, ruleId, 0, int.MaxValue, ct);
        return exporter.Export(new ReconciliationReport(
            job.ToDto(clock.GetUtcNow().UtcDateTime, atRisk), owner?.Email, runId, summary,
            rows.Select(r => r.ToDto()).ToList(), status, ruleName));
    }

    private async Task<(JobRun Run, Domain.Jobs.Job Job)> LoadAsync(Guid runId, CancellationToken ct)
    {
        var run = await runs.FindAsync(runId, ct) ?? throw new NotFoundException($"Run '{runId}' was not found.");
        var job = await JobAccess.LoadVisibleAsync(jobs, me, run.JobId, ct);
        if (job.Template?.IsFileReconciliation != true)
            throw new ValidationException("This run has no reconciliation results: its job does not use uploaded files.");
        return (run, job);
    }

    private async Task<RunReconciliationSummaryDto> BuildSummaryAsync(JobRun run, Domain.Jobs.Job job, CancellationToken ct)
    {
        var counts = (await store.CountByRuleAsync(run.Id, ct)).ToDictionary(c => c.RuleId);
        var rules = new List<RuleSummaryDto>();
        foreach (var rule in (await store.ListRulesAsync(job.Id, ct)).OrderBy(r => r.Position))
        {
            counts.TryGetValue(rule.Id, out var c);
            rules.Add(new RuleSummaryDto(rule.Id, rule.Name, RuleText.Describe(rule), c?.Total ?? 0, c?.Passed ?? 0,
                (c?.Total ?? 0) - (c?.Passed ?? 0)));
        }

        var total = rules.Sum(r => r.Total);
        var passed = rules.Sum(r => r.Passed);
        return new RunReconciliationSummaryDto(
            run.Id, job.Id, job.Name, run.Status, run.FinishedAtUtc, total, passed, total - passed, rules);
    }
}

internal static class ResultMapping
{
    public static ReconciliationResultDto ToDto(this ReconciliationResult r) => new(
        r.Id, r.RuleId, r.RuleName, r.RecordKey, r.LeftRow, r.RightRow, r.LeftValue, r.RightValue, r.Difference,
        r.Status, r.Reason);
}

/// <summary>CSV of the record-level results: one row per record per rule. Opens directly in a spreadsheet.</summary>
public class CsvReconciliationExporter : IReconciliationExporter
{
    private static readonly string[] Header =
        ["Rule", "RecordKey", "LeftRow", "RightRow", "LeftValue", "RightValue", "Difference", "Status", "Reason"];

    public string Format => "csv";

    public ExportedFile Export(ReconciliationReport report)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Header)).Append("\r\n");
        foreach (var r in report.Results)
        {
            // Text cells are formula-safe; numbers are written as plain numbers so a spreadsheet keeps them numeric.
            sb.Append(string.Join(',',
                RunHistoryCsv.Escape(r.RuleName), RunHistoryCsv.Escape(r.RecordKey),
                Number(r.LeftRow), Number(r.RightRow), Number(r.LeftValue), Number(r.RightValue), Number(r.Difference),
                r.Status, RunHistoryCsv.Escape(r.Reason))).Append("\r\n");
        }

        return new ExportedFile(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "csv");
    }

    internal static string Number(decimal? value) =>
        value is { } v ? v.ToString("0.00##########", CultureInfo.InvariantCulture) : "";

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}

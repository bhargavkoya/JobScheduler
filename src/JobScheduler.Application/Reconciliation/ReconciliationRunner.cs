using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Domain.Runs;

namespace JobScheduler.Application.Reconciliation;

/// <summary>Which exact files a run used. Stored as the download step's output, so a retry reads the same bytes.</summary>
public sealed record InputSnapshot(IReadOnlyList<SnapshotFile> Files);

public sealed record SnapshotFile(string Alias, Guid FileId, string FileName, string Sha256, int Rows);

/// <summary>The file-based counterpart of the finance download + rules engine, used by file reconciliation jobs.</summary>
public interface IReconciliationRunner
{
    /// <summary>Pins the job's current files for this run and returns the snapshot as JSON (the "download" step output).</summary>
    Task<string> SnapshotInputsAsync(Job job, CancellationToken ct);

    /// <summary>
    /// Parses the pinned files, evaluates every rule for every record and stores the per-record results for the run,
    /// replacing any earlier results of the same run (so a retry cannot double-count).
    /// </summary>
    Task<CalculationResult> RunAsync(Job job, JobRun run, string snapshotJson, CancellationToken ct);
}

public class ReconciliationRunner(IReconciliationStore store) : IReconciliationRunner
{
    private const int FailureExamplesInSummary = 10;

    public async Task<string> SnapshotInputsAsync(Job job, CancellationToken ct)
    {
        var files = await store.ListActiveFilesAsync(job.Id, ct);
        if (files.Count == 0)
            throw new InvalidOperationException("This job has no input files to reconcile.");
        if ((await store.ListRulesAsync(job.Id, ct)).Count == 0)
            throw new InvalidOperationException("This job has no reconciliation rules.");

        var snapshot = new InputSnapshot(files
            .OrderBy(f => f.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(f => new SnapshotFile(f.Alias, f.Id, f.FileName, f.Sha256, f.RowCount))
            .ToList());
        return JsonSerializer.Serialize(snapshot);
    }

    public async Task<CalculationResult> RunAsync(Job job, JobRun run, string snapshotJson, CancellationToken ct)
    {
        var snapshot = JsonSerializer.Deserialize<InputSnapshot>(snapshotJson)
            ?? throw new InvalidOperationException("The input snapshot could not be read.");
        var tables = await LoadTablesAsync(snapshot, ct);

        var rules = (await store.ListRulesAsync(job.Id, ct)).OrderBy(r => r.Position).ToList();
        if (rules.Count == 0) throw new InvalidOperationException("This job has no reconciliation rules.");

        var results = new List<ReconciliationResult>();
        var lines = new List<string>();
        var examples = new List<string>();
        var values = new Dictionary<string, decimal>();

        foreach (var rule in rules)
        {
            var left = TableFor(tables, rule.LeftAlias, rule.Name);
            var right = TableFor(tables, rule.RightAlias, rule.Name);
            var outcomes = RuleEvaluator.Evaluate(rule, left, right);

            foreach (var o in outcomes)
                results.Add(new ReconciliationResult
                {
                    RunId = run.Id, RuleId = rule.Id, RuleName = rule.Name, RecordKey = Trim(o.Key, 500),
                    LeftRow = o.LeftRow, RightRow = o.RightRow, LeftValue = o.LeftValue, RightValue = o.RightValue,
                    Difference = o.Difference, Status = o.Status, Reason = Trim(o.Reason, 1000)
                });

            var failed = outcomes.Count(o => o.Status == RecordStatus.Fail);
            lines.Add($"- {rule.Name}: {outcomes.Count - failed} of {outcomes.Count} records passed, {failed} failed ({RuleText.Describe(rule)})");
            values[rule.Name] = failed;
            examples.AddRange(outcomes.Where(o => o.Status == RecordStatus.Fail)
                .Take(FailureExamplesInSummary).Select(o => $"- {rule.Name} / {o.Key}: {o.Reason}"));
        }

        await store.ReplaceResultsAsync(run.Id, results, ct);

        var totalFailed = results.Count(r => r.Status == RecordStatus.Fail);
        var summary = new StringBuilder()
            .AppendLine($"Reconciliation: {results.Count - totalFailed} of {results.Count} record checks passed, {totalFailed} failed, across {rules.Count} rule(s).")
            .AppendLine(string.Join(Environment.NewLine, lines));
        if (examples.Count > 0)
        {
            summary.AppendLine().AppendLine($"First failures (up to {FailureExamplesInSummary} per rule; full list in the app and its export):");
            summary.AppendLine(string.Join(Environment.NewLine, examples.Take(FailureExamplesInSummary * 2)));
        }

        return new CalculationResult(summary.ToString().TrimEnd(), values);
    }

    private async Task<Dictionary<string, CsvTable>> LoadTablesAsync(InputSnapshot snapshot, CancellationToken ct)
    {
        var tables = new Dictionary<string, CsvTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
            var bytes = await store.GetContentAsync(file.FileId, ct)
                ?? throw new InvalidOperationException($"The stored content of '{file.FileName}' is missing.");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{file.FileName}' no longer matches the file this run was started with.");
            tables[file.Alias] = CsvTable.Parse(bytes);
        }

        return tables;
    }

    private static CsvTable TableFor(Dictionary<string, CsvTable> tables, string alias, string ruleName) =>
        tables.TryGetValue(alias, out var table)
            ? table
            : throw new InvalidOperationException($"Rule '{ruleName}' uses file '{alias}', which is not part of this run.");

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 3)] + "...";
}

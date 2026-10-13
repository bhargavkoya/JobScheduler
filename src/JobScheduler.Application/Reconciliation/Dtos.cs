using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Domain.Runs;

namespace JobScheduler.Application.Reconciliation;

public sealed record FileUpload(string FileName, byte[] Content);

/// <summary>An uploaded file as the UI needs it: its columns (from the header row) and, right after upload, a short preview.</summary>
public sealed record InputFileDto(
    Guid Id,
    string Alias,
    string FileName,
    IReadOnlyList<string> Headers,
    int RowCount,
    long SizeBytes,
    DateTime UploadedAtUtc,
    IReadOnlyList<IReadOnlyList<string>> Preview);

public sealed record AttachFileDto(Guid FileId, string Alias);

public sealed record ReconciliationRuleDto(
    Guid? Id,
    string Name,
    string LeftAlias,
    string RightAlias,
    string? LeftKeyColumn,
    string? RightKeyColumn,
    string LeftExpression,
    ComparisonOperator Operator,
    string RightExpression,
    decimal Tolerance = 0,
    ToleranceMode ToleranceMode = ToleranceMode.Absolute);

/// <summary>What a job needs to run a file reconciliation: the uploaded files (by alias) and the rules over them.</summary>
public sealed record ReconciliationSetupDto(
    IReadOnlyList<AttachFileDto> Files, IReadOnlyList<ReconciliationRuleDto> Rules);

public sealed record ReconciliationSetupViewDto(
    IReadOnlyList<InputFileDto> Files, IReadOnlyList<ReconciliationRuleDto> Rules);

public sealed record ReplaceFileRequest(string Alias, Guid FileId);

public sealed record RuleSummaryDto(Guid RuleId, string RuleName, string Description, int Total, int Passed, int Failed);

public sealed record RunReconciliationSummaryDto(
    Guid RunId,
    Guid JobId,
    string JobName,
    RunStatus RunStatus,
    DateTime? FinishedAtUtc,
    int Total,
    int Passed,
    int Failed,
    IReadOnlyList<RuleSummaryDto> Rules);

public sealed record ReconciliationResultDto(
    long Id,
    Guid RuleId,
    string RuleName,
    string RecordKey,
    int? LeftRow,
    int? RightRow,
    decimal? LeftValue,
    decimal? RightValue,
    decimal? Difference,
    RecordStatus Status,
    string Reason);

public sealed record ResultsPageDto(
    IReadOnlyList<ReconciliationResultDto> Items, int Total, int Page, int PageSize);

/// <summary>Everything an exporter needs for one run's record-level results.</summary>
public sealed record ReconciliationReport(
    JobDto Job,
    string? OwnerEmail,
    Guid RunId,
    RunReconciliationSummaryDto Summary,
    IReadOnlyList<ReconciliationResultDto> Results,
    RecordStatus? StatusFilter,
    string? RuleFilterName = null);

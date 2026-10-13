using JobScheduler.Domain.Reconciliation;

namespace JobScheduler.Application.Reconciliation;

public sealed record RuleCount(Guid RuleId, string RuleName, int Total, int Passed);

/// <summary>Persistence seam for uploaded files, reconciliation rules and per-record results.</summary>
public interface IReconciliationStore
{
    /// <summary>Adds the file row and its bytes (separate table). Saved with the next <see cref="SaveChangesAsync"/>.</summary>
    void AddFile(InputFile file, byte[] content);

    /// <summary>Tracked entities, so callers can attach or supersede them. Bytes are not loaded.</summary>
    Task<IReadOnlyList<InputFile>> FindFilesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>The files currently attached to a job (not superseded), ordered by alias.</summary>
    Task<IReadOnlyList<InputFile>> ListActiveFilesAsync(Guid jobId, CancellationToken ct);

    Task<byte[]?> GetContentAsync(Guid fileId, CancellationToken ct);

    void AddRule(ReconciliationRule rule);
    Task<IReadOnlyList<ReconciliationRule>> ListRulesAsync(Guid jobId, CancellationToken ct);

    /// <summary>Deletes the run's earlier results and stores the new ones in one transaction, so a retry never doubles them.</summary>
    Task ReplaceResultsAsync(Guid runId, IReadOnlyList<ReconciliationResult> results, CancellationToken ct);

    Task<IReadOnlyList<RuleCount>> CountByRuleAsync(Guid runId, CancellationToken ct);
    Task<int> CountAsync(Guid runId, RecordStatus? status, Guid? ruleId, CancellationToken ct);
    Task<IReadOnlyList<ReconciliationResult>> QueryAsync(
        Guid runId, RecordStatus? status, Guid? ruleId, int skip, int take, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

using System.Text.RegularExpressions;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Reconciliation;

public interface IReconciliationSetupService
{
    /// <summary>
    /// Validates the uploaded files and rules against each other (aliases, join keys, expression columns) and attaches
    /// them to the job. Nothing is saved here: the caller saves together with the job, so a bad setup never leaves a
    /// half-created job behind.
    /// </summary>
    Task AttachAsync(Job job, ReconciliationSetupDto? setup, CancellationToken ct);

    Task<ReconciliationSetupViewDto> GetAsync(Guid jobId, CancellationToken ct);

    /// <summary>Swaps the file behind an alias (e.g. today's extract). The old file is superseded, not deleted.</summary>
    Task<ReconciliationSetupViewDto> ReplaceFileAsync(Guid jobId, ReplaceFileRequest request, CancellationToken ct);
}

public partial class ReconciliationSetupService(
    IReconciliationStore store, IJobStore jobs, ICurrentUser me, TimeProvider clock) : IReconciliationSetupService
{
    public const int MaxFiles = 5;
    public const int MaxRules = 20;

    public async Task AttachAsync(Job job, ReconciliationSetupDto? setup, CancellationToken ct)
    {
        if (setup is null || setup.Files.Count == 0)
            throw new ValidationException("Upload at least one CSV file for this job.");
        if (setup.Rules.Count == 0)
            throw new ValidationException("Add at least one reconciliation rule.");
        if (setup.Files.Count > MaxFiles) throw new ValidationException($"A job can use at most {MaxFiles} files.");
        if (setup.Rules.Count > MaxRules) throw new ValidationException($"A job can have at most {MaxRules} rules.");

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in setup.Files)
        {
            ValidateAlias(f.Alias);
            if (!aliases.Add(f.Alias.Trim()))
                throw new ValidationException($"The file alias '{f.Alias}' is used twice.");
        }

        if (setup.Files.Select(f => f.FileId).Distinct().Count() != setup.Files.Count)
            throw new ValidationException("The same uploaded file was added twice.");

        var found = (await store.FindFilesAsync(setup.Files.Select(f => f.FileId).ToList(), ct)).ToDictionary(f => f.Id);
        var headersByAlias = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var toAttach = new List<(InputFile File, string Alias)>();
        foreach (var f in setup.Files)
        {
            var file = RequireUsableFile(found, f.FileId);
            headersByAlias[f.Alias.Trim()] = file.HeaderList();
            toAttach.Add((file, f.Alias.Trim()));
        }

        var rules = new List<ReconciliationRule>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < setup.Rules.Count; i++)
        {
            var rule = BuildRule(job.Id, i + 1, setup.Rules[i], headersByAlias);
            if (!names.Add(rule.Name)) throw new ValidationException($"There are two rules named '{rule.Name}'.");
            rules.Add(rule);
        }

        foreach (var (file, alias) in toAttach)
        {
            file.JobId = job.Id;
            file.Alias = alias;
        }

        foreach (var rule in rules) store.AddRule(rule);
    }

    public async Task<ReconciliationSetupViewDto> GetAsync(Guid jobId, CancellationToken ct)
    {
        await JobAccess.LoadVisibleAsync(jobs, me, jobId, ct);
        return await ViewAsync(jobId, ct);
    }

    public async Task<ReconciliationSetupViewDto> ReplaceFileAsync(Guid jobId, ReplaceFileRequest request, CancellationToken ct)
    {
        var job = await JobAccess.LoadVisibleAsync(jobs, me, jobId, ct);
        if (me.Role != Role.Admin && job.OwnerId != me.UserId)
            throw new ForbiddenException("Only the job owner or an admin can replace its files.");
        if (job.Template?.IsFileReconciliation != true)
            throw new ValidationException("This job does not use uploaded files.");
        if (job.Status == JobStatus.InProgress)
            throw new ConflictException("The job is running right now. Replace the file when the run has finished.");
        if (job.Status == JobStatus.Cancelled)
            throw new ConflictException("A cancelled job cannot be changed.");

        var active = await store.ListActiveFilesAsync(jobId, ct);
        var current = active.FirstOrDefault(f => string.Equals(f.Alias, request.Alias?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ValidationException(
                $"This job has no file with the alias '{request.Alias}'. Aliases: {string.Join(", ", active.Select(f => f.Alias))}.");

        var found = (await store.FindFilesAsync([request.FileId], ct)).ToDictionary(f => f.Id);
        var replacement = RequireUsableFile(found, request.FileId);

        // The new file must still contain every column the rules read from this alias.
        var headers = replacement.HeaderList();
        var missing = RequiredColumns(await store.ListRulesAsync(jobId, ct), current.Alias)
            .Where(c => !headers.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
            throw new ValidationException(
                $"The new file is missing column(s) used by this job's rules: {string.Join(", ", missing)}.");

        current.SupersededAtUtc = clock.GetUtcNow().UtcDateTime;
        replacement.JobId = jobId;
        replacement.Alias = current.Alias;
        await store.SaveChangesAsync(ct);
        return await ViewAsync(jobId, ct);
    }

    private async Task<ReconciliationSetupViewDto> ViewAsync(Guid jobId, CancellationToken ct) =>
        new((await store.ListActiveFilesAsync(jobId, ct)).Select(f => f.ToDto()).ToList(),
            (await store.ListRulesAsync(jobId, ct)).OrderBy(r => r.Position).Select(r => r.ToDto()).ToList());

    // ---- validation ----

    /// <summary>A file the caller uploaded, not yet attached anywhere. Other people's files look the same as missing ones.</summary>
    private InputFile RequireUsableFile(IReadOnlyDictionary<Guid, InputFile> found, Guid id)
    {
        if (!found.TryGetValue(id, out var file) || (file.OwnerId != me.UserId && me.Role != Role.Admin))
            throw new ValidationException($"Uploaded file '{id}' was not found. Upload it again.");
        if (file.JobId is not null || file.SupersededAtUtc is not null)
            throw new ValidationException($"'{file.FileName}' is already used by a job. Upload it again to use it here.");
        return file;
    }

    private static void ValidateAlias(string? alias)
    {
        if (alias is null || !AliasPattern().IsMatch(alias.Trim()))
            throw new ValidationException(
                $"'{alias}' is not a valid file alias. Use a letter followed by letters, digits or underscores (max 30).");
    }

    /// <summary>Checks one rule against the headers of the files it uses and returns it ready to store.</summary>
    internal static ReconciliationRule BuildRule(
        Guid jobId, int position, ReconciliationRuleDto dto, IReadOnlyDictionary<string, IReadOnlyList<string>> headersByAlias)
    {
        var name = dto.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 200) throw new ValidationException("Every rule needs a name (max 200 characters).");
        string Fail(string message) => $"Rule '{name}': {message}";

        string ResolveAlias(string? alias, string which)
        {
            var key = headersByAlias.Keys.FirstOrDefault(k => string.Equals(k, alias?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ValidationException(Fail(
                    $"the {which} file '{alias}' is not one of this job's files ({string.Join(", ", headersByAlias.Keys)})."));
            return key;
        }

        var leftAlias = ResolveAlias(dto.LeftAlias, "left");
        var rightAlias = ResolveAlias(dto.RightAlias, "right");
        var single = string.Equals(leftAlias, rightAlias, StringComparison.OrdinalIgnoreCase);

        if (!Enum.IsDefined(dto.Operator)) throw new ValidationException(Fail("unknown comparison operator."));
        if (!Enum.IsDefined(dto.ToleranceMode)) throw new ValidationException(Fail("unknown tolerance type."));
        if (dto.Tolerance < 0) throw new ValidationException(Fail("the tolerance cannot be negative."));
        if (dto.ToleranceMode == ToleranceMode.Percent && dto.Tolerance > 100)
            throw new ValidationException(Fail("a percentage tolerance cannot be more than 100."));
        if (dto.Tolerance > 1_000_000_000_000m) throw new ValidationException(Fail("the tolerance is too large."));

        string? leftKey = null, rightKey = null;
        if (single)
        {
            if (!string.IsNullOrWhiteSpace(dto.LeftKeyColumn))
                leftKey = ResolveColumn(headersByAlias[leftAlias], dto.LeftKeyColumn, leftAlias, "key", name);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(dto.LeftKeyColumn) || string.IsNullOrWhiteSpace(dto.RightKeyColumn))
                throw new ValidationException(Fail("choose the key column in each file to match records on."));
            leftKey = ResolveColumn(headersByAlias[leftAlias], dto.LeftKeyColumn, leftAlias, "key", name);
            rightKey = ResolveColumn(headersByAlias[rightAlias], dto.RightKeyColumn, rightAlias, "key", name);
        }

        var left = ParseExpression(dto.LeftExpression, name, "left");
        var right = ParseExpression(dto.RightExpression, name, "right");
        foreach (var r in left.References.Concat(right.References))
        {
            var alias = new[] { leftAlias, rightAlias }
                .FirstOrDefault(a => string.Equals(a, r.Alias, StringComparison.OrdinalIgnoreCase))
                ?? throw new ValidationException(Fail(
                    $"the expression uses '{r.Alias}.{r.Column}', but this rule compares only '{leftAlias}'"
                    + (single ? "" : $" and '{rightAlias}'") + "."));
            ResolveColumn(headersByAlias[alias], r.Column, alias, "column", name);
        }

        return new ReconciliationRule
        {
            JobId = jobId, Position = position, Name = name, LeftAlias = leftAlias, RightAlias = rightAlias,
            LeftKeyColumn = leftKey, RightKeyColumn = rightKey,
            LeftExpression = dto.LeftExpression.Trim(), Operator = dto.Operator, RightExpression = dto.RightExpression.Trim(),
            Tolerance = dto.Tolerance, ToleranceMode = dto.ToleranceMode
        };
    }

    private static Expression ParseExpression(string? text, string ruleName, string side)
    {
        try
        {
            return Expression.Parse(text ?? "");
        }
        catch (ValidationException ex)
        {
            throw new ValidationException($"Rule '{ruleName}', {side} side: {ex.Message}");
        }
    }

    private static string ResolveColumn(IReadOnlyList<string> headers, string column, string alias, string kind, string ruleName)
    {
        var match = headers.FirstOrDefault(h => string.Equals(h, column.Trim(), StringComparison.OrdinalIgnoreCase));
        var what = kind == "key" ? "key column" : "column";
        return match ?? throw new ValidationException(
            $"Rule '{ruleName}': the {what} '{column}' does not exist in '{alias}'. Columns: {string.Join(", ", headers)}.");
    }

    /// <summary>Every column of one alias that the rules read: join keys and expression references.</summary>
    internal static IEnumerable<string> RequiredColumns(IEnumerable<ReconciliationRule> rules, string alias)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (string.Equals(rule.LeftAlias, alias, StringComparison.OrdinalIgnoreCase) && rule.LeftKeyColumn is not null)
                columns.Add(rule.LeftKeyColumn);
            if (string.Equals(rule.RightAlias, alias, StringComparison.OrdinalIgnoreCase) && !rule.IsSingleFile
                && rule.RightKeyColumn is not null)
                columns.Add(rule.RightKeyColumn);

            foreach (var r in Expression.Parse(rule.LeftExpression).References.Concat(Expression.Parse(rule.RightExpression).References))
                if (string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase))
                    columns.Add(r.Column);
        }

        return columns;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,29}$")]
    private static partial Regex AliasPattern();
}

internal static class RuleMapping
{
    public static ReconciliationRuleDto ToDto(this ReconciliationRule r) => new(
        r.Id, r.Name, r.LeftAlias, r.RightAlias, r.LeftKeyColumn, r.RightKeyColumn,
        r.LeftExpression, r.Operator, r.RightExpression, r.Tolerance, r.ToleranceMode);
}

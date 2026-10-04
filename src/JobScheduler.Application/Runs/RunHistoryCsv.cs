using System.Globalization;
using System.Text;
using JobScheduler.Domain.Jobs;

namespace JobScheduler.Application.Runs;

/// <summary>CSV export of a job's run history (PRD section 4.8). Times are shown in IST.</summary>
public static class RunHistoryCsv
{
    private static readonly string[] Header =
    [
        "RunId", "IdempotencyKey", "Status", "Attempts", "AutoRetriesUsed", "FailedStep", "Error",
        "CreatedIst", "StartedIst", "FinishedIst", "Steps"
    ];

    public static string Build(IEnumerable<JobRunDto> runs)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Header)).Append("\r\n");
        foreach (var r in runs.OrderBy(r => r.CreatedAtUtc))
        {
            var steps = string.Join("; ", r.Steps.Select(s => $"{s.Step}#{s.Attempt}={s.Status}"));
            var cells = new[]
            {
                r.Id.ToString(), r.IdempotencyKey, r.Status.ToString(),
                r.Attempt.ToString(CultureInfo.InvariantCulture), r.AutoRetriesUsed.ToString(CultureInfo.InvariantCulture),
                r.FailedStep?.ToString() ?? "", r.Error ?? "",
                Ist(r.CreatedAtUtc), Ist(r.StartedAtUtc), Ist(r.FinishedAtUtc), steps
            };
            sb.Append(string.Join(',', cells.Select(Escape))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Ist(DateTime? utc) =>
        utc is { } t ? JobScheduler.Domain.Jobs.Ist.FromUtc(t).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";

    /// <summary>RFC 4180 quoting, plus a leading apostrophe on cells a spreadsheet would treat as a formula.</summary>
    public static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}

using System.Text.Json;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;

namespace JobScheduler.Application.Runs;

internal static class JobConfig
{
    public static Dictionary<string, string> Read(Job job) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(job.ConfigJson) ?? new();
}

public class DownloadReportStep(IFinanceAppClient finance) : IPipelineStep
{
    public PipelineStep Name => PipelineStep.DownloadReport;

    public async Task<string> ExecuteAsync(StepContext context, CancellationToken ct)
    {
        var config = JobConfig.Read(context.Job);
        var reportName = config.GetValueOrDefault("reportName") ?? context.Job.Name;

        // The run's idempotency key makes a retried trigger return the same report instead of a new one.
        var reportId = await finance.TriggerReportAsync($"{context.Run.IdempotencyKey}:download", reportName, ct);
        var report = await finance.DownloadReportAsync(reportId, ct);
        return JsonSerializer.Serialize(report);
    }
}

public class CalculateStep(ICalculationEngine engine) : IPipelineStep
{
    public PipelineStep Name => PipelineStep.Calculate;

    public async Task<string> ExecuteAsync(StepContext context, CancellationToken ct)
    {
        if (!context.PriorOutputs.TryGetValue(PipelineStep.DownloadReport, out var downloadOutput))
            throw new InvalidOperationException("Calculate ran before DownloadReport produced output.");

        var report = JsonSerializer.Deserialize<ReportData>(downloadOutput)
            ?? throw new InvalidOperationException("Download output could not be read.");
        var result = await engine.CalculateAsync(context.Job.Id, report, JobConfig.Read(context.Job), ct);
        return JsonSerializer.Serialize(result);
    }
}

public class SendEmailStep(IEmailSender email) : IPipelineStep
{
    public PipelineStep Name => PipelineStep.SendEmail;

    public async Task<string> ExecuteAsync(StepContext context, CancellationToken ct)
    {
        var config = JobConfig.Read(context.Job);
        var recipient = context.Job.Template?.Fields
            .Where(f => f.Type == FieldType.Email)
            .Select(f => config.GetValueOrDefault(f.Name))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        if (recipient is null)
            return "No recipient configured; email skipped.";

        var summary = context.PriorOutputs.TryGetValue(PipelineStep.Calculate, out var calc)
            ? JsonSerializer.Deserialize<CalculationResult>(calc)?.Summary
            : null;

        await email.SendAsync(
            new EmailMessage(
                recipient,
                $"[Job Scheduler] {context.Job.Name} completed",
                $"Job '{context.Job.Name}' finished its run.\n\n{summary}",
                $"{context.Run.IdempotencyKey}:email"),
            ct);
        return $"Email sent to {recipient}.";
    }
}

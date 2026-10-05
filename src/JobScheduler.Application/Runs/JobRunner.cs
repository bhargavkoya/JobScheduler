using JobScheduler.Application.Approvals;
using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Runs;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Application.Runs;

/// <summary>
/// Executes one run: download -> calculate -> email. Each finished step is saved, so a retry resumes after
/// the last good step (no double-sent email, no double-counted calculation). Failures auto-retry with
/// exponential backoff up to the job's retry policy, then the job is marked Failed.
/// </summary>
public class JobRunner(
    IJobRunStore runs,
    IJobStore jobs,
    IEnumerable<IPipelineStep> pipeline,
    IJobQueue queue,
    IFailureNotifier failureNotifier,
    ICompletionNotifier completionNotifier,
    IApprovalFollowUp followUp,
    IJobChainer chainer,
    TimeProvider clock,
    ILogger<JobRunner> log)
{
    private readonly IReadOnlyList<IPipelineStep> _steps = pipeline.OrderBy(s => s.Name).ToList();

    public async Task ExecuteAsync(Guid runId, CancellationToken ct)
    {
        var run = await runs.FindAsync(runId, ct);
        if (run is null)
        {
            log.LogWarning("Run {RunId} not found; dropping message.", runId);
            return;
        }

        // Redelivery of a finished run is a no-op (queue messages are at-least-once).
        if (run.Status is RunStatus.Succeeded or RunStatus.Failed)
        {
            log.LogInformation("Run {RunId} already {Status}; nothing to do.", runId, run.Status);
            return;
        }

        var job = await jobs.FindAsync(run.JobId, ct);
        if (job is null)
        {
            log.LogWarning("Job {JobId} for run {RunId} not found; dropping message.", run.JobId, runId);
            return;
        }

        run.Status = RunStatus.Running;
        run.Attempt++;
        run.StartedAtUtc ??= Now();
        run.FinishedAtUtc = null;
        await SaveAsync(ct);

        var outputs = run.Steps
            .Where(s => s.Status == StepStatus.Succeeded)
            .GroupBy(s => s.Step)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.FinishedAtUtc).Last().Output);

        foreach (var step in _steps)
        {
            if (run.HasSucceeded(step.Name))
            {
                log.LogInformation("Run {RunId}: skipping {Step}, already succeeded.", runId, step.Name);
                continue;
            }

            var started = Now();
            try
            {
                var output = await step.ExecuteAsync(new StepContext(job, run, outputs), ct);
                outputs[step.Name] = output;
                AddStep(run, step.Name, StepStatus.Succeeded, output, started);
                await SaveAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down mid-run: leave the run Running; startup recovery re-queues it.
                throw;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Run {RunId} failed at {Step} (attempt {Attempt}).", runId, step.Name, run.Attempt);
                AddStep(run, step.Name, StepStatus.Failed, ex.Message, started);
                await HandleFailureAsync(run, job, step.Name, ex.Message, ct);
                return;
            }
        }

        run.Status = RunStatus.Succeeded;
        run.Error = null;
        run.FailedStep = null;
        run.FinishedAtUtc = Now();

        // Approval templates stop here and wait for the single approver; everything else is done.
        var needsApproval = job.Template?.RequiresApproval == true;
        if (needsApproval) job.RequestApproval(); else job.MarkCompleted();
        await SaveAsync(ct);

        if (needsApproval) await ScheduleFollowUpAsync(job, ct);
        else
        {
            await NotifyCompletedAsync(job, run, ct);
            await ChainAsync(job, run, ct);
        }
    }

    private async Task NotifyCompletedAsync(Domain.Jobs.Job job, JobRun run, CancellationToken ct)
    {
        // The job is already Completed and saved; a mail problem must not change that or fail the worker.
        try
        {
            await completionNotifier.NotifyCompletedAsync(job, run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Run {RunId}: could not send the completion notification.", run.Id);
        }
    }

    private async Task ChainAsync(Domain.Jobs.Job job, JobRun run, CancellationToken ct)
    {
        try
        {
            await chainer.OnJobCompletedAsync(job.Id, run.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Job {JobId}: could not start chained jobs.", job.Id);
        }
    }

    private async Task ScheduleFollowUpAsync(Domain.Jobs.Job job, CancellationToken ct)
    {
        try
        {
            await followUp.ScheduleAsync(job, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Job {JobId}: could not schedule the approval follow-up.", job.Id);
        }
    }

    private async Task HandleFailureAsync(
        JobRun run, Domain.Jobs.Job job, PipelineStep failedStep, string error, CancellationToken ct)
    {
        run.Error = error;
        run.FailedStep = failedStep;

        if (run.AutoRetriesUsed < job.RetryPolicy.MaxAutoRetries)
        {
            run.AutoRetriesUsed++;
            run.Status = RunStatus.Pending;
            await SaveAsync(ct);

            var delay = Backoff(job.RetryPolicy.BackoffSeconds, run.AutoRetriesUsed);
            log.LogInformation("Run {RunId}: auto-retry {N}/{Max} in {Delay}.",
                run.Id, run.AutoRetriesUsed, job.RetryPolicy.MaxAutoRetries, delay);
            await queue.EnqueueAsync(run.Id, delay, ct);
            return;
        }

        run.Status = RunStatus.Failed;
        run.FinishedAtUtc = Now();
        job.MarkFailed();
        await SaveAsync(ct);

        // The job is already Failed and saved; a mail problem must not change that or fail the worker.
        try
        {
            await failureNotifier.NotifyFailedAsync(job, run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Run {RunId}: could not send the failure notification.", run.Id);
        }
    }

    /// <summary>backoff * 2^(retry-1), capped at the policy limit.</summary>
    public static TimeSpan Backoff(int backoffSeconds, int retryNumber)
    {
        var seconds = backoffSeconds * Math.Pow(2, Math.Max(0, retryNumber - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, Domain.Jobs.RetryPolicy.MaxBackoffSecondsLimit));
    }

    private void AddStep(JobRun run, PipelineStep step, StepStatus status, string output, DateTime started)
    {
        var row = new JobRunStep
        {
            RunId = run.Id,
            Step = step,
            Status = status,
            Attempt = run.Attempt,
            Output = output,
            StartedAtUtc = started,
            FinishedAtUtc = Now()
        };
        run.Steps.Add(row);
        runs.AddStep(row);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await runs.SaveChangesAsync(ct);
        await jobs.SaveChangesAsync(ct);
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}

using JobScheduler.Application.Approvals;
using JobScheduler.Application.Common;
using JobScheduler.Application.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace JobScheduler.Infrastructure.Runs;

/// <summary>Fires when a Fixed job's time arrives: creates the run (idempotently) and queues it.</summary>
[DisallowConcurrentExecution]
public class FireJobRunJob(IServiceScopeFactory scopes, ILogger<FireJobRunJob> log) : IJob
{
    public const string JobIdKey = "jobId";

    public async Task Execute(IJobExecutionContext context)
    {
        var jobId = Guid.Parse(context.MergedJobDataMap.GetString(JobIdKey)!);
        await using var scope = scopes.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IRunOrchestrator>();

        try
        {
            var run = await orchestrator.EnqueueScheduledAsync(jobId, context.CancellationToken);
            log.LogInformation("Scheduler fired job {JobId}: run {RunId} ({Status}).", jobId, run.Id, run.Status);
        }
        catch (Exception ex) when (ex is ConflictException or NotFoundException)
        {
            // Cancelled or already started between scheduling and firing: nothing to run.
            log.LogInformation("Scheduler skipped job {JobId}: {Message}", jobId, ex.Message);
        }
    }
}

/// <summary>Fires the approval chaser when due. The follow-up itself decides whether anything still needs sending.</summary>
[DisallowConcurrentExecution]
public class FollowUpJob(IServiceScopeFactory scopes, ILogger<FollowUpJob> log) : IJob
{
    public const string JobIdKey = "jobId";

    public async Task Execute(IJobExecutionContext context)
    {
        var jobId = Guid.Parse(context.MergedJobDataMap.GetString(JobIdKey)!);
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IApprovalFollowUp>().FireAsync(jobId, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Follow-up for job {JobId} failed.", jobId);
        }
    }
}

public class QuartzJobScheduler(ISchedulerFactory factory, TimeProvider clock) : IJobScheduler
{
    private const string Group = "fixed-jobs";

    private const string FollowUpGroup = "follow-ups";

    private static JobKey KeyFor(Guid jobId) => new($"job-{jobId}", Group);
    private static JobKey FollowUpKeyFor(Guid jobId) => new($"followup-{jobId}", FollowUpGroup);

    public async Task ScheduleFixedAsync(Guid jobId, DateTime runAtUtc, CancellationToken ct)
    {
        var scheduler = await factory.GetScheduler(ct);
        var key = KeyFor(jobId);
        if (await scheduler.CheckExists(key, ct)) await scheduler.DeleteJob(key, ct);

        var job = JobBuilder.Create<FireJobRunJob>()
            .WithIdentity(key)
            .UsingJobData(FireJobRunJob.JobIdKey, jobId.ToString())
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"trigger-{jobId}", Group)
            .ForJob(key);

        // A time that already passed (app was down, or very soon) fires immediately rather than being dropped.
        var runAt = new DateTimeOffset(DateTime.SpecifyKind(runAtUtc, DateTimeKind.Utc));
        if (runAt <= clock.GetUtcNow()) trigger.StartNow(); else trigger.StartAt(runAt);

        await scheduler.ScheduleJob(job, trigger.WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow()).Build(), ct);
    }

    public async Task ScheduleFollowUpAsync(Guid jobId, DateTime dueUtc, CancellationToken ct)
    {
        var scheduler = await factory.GetScheduler(ct);
        var key = FollowUpKeyFor(jobId);
        if (await scheduler.CheckExists(key, ct)) await scheduler.DeleteJob(key, ct);

        var job = JobBuilder.Create<FollowUpJob>()
            .WithIdentity(key)
            .UsingJobData(FollowUpJob.JobIdKey, jobId.ToString())
            .Build();

        var trigger = TriggerBuilder.Create().WithIdentity($"followup-trigger-{jobId}", FollowUpGroup).ForJob(key);
        var due = new DateTimeOffset(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc));
        if (due <= clock.GetUtcNow()) trigger.StartNow(); else trigger.StartAt(due);

        await scheduler.ScheduleJob(job, trigger.WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow()).Build(), ct);
    }

    public async Task UnscheduleAsync(Guid jobId, CancellationToken ct)
    {
        var scheduler = await factory.GetScheduler(ct);
        await scheduler.DeleteJob(KeyFor(jobId), ct);
        await scheduler.DeleteJob(FollowUpKeyFor(jobId), ct);
    }
}

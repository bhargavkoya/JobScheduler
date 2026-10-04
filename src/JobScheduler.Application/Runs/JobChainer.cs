using JobScheduler.Application.Jobs;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Application.Runs;

/// <summary>Reacts to "job X completed" by starting the EventBased jobs that depend on X.</summary>
public interface IJobChainer
{
    Task OnJobCompletedAsync(Guid jobId, Guid runId, CancellationToken ct);
}

public class JobChainer(IJobStore jobs, IRunOrchestrator orchestrator, ILogger<JobChainer> log) : IJobChainer
{
    public async Task OnJobCompletedAsync(Guid jobId, Guid runId, CancellationToken ct)
    {
        foreach (var dependent in await jobs.ListWaitingDependentsAsync(jobId, ct))
        {
            try
            {
                // Keyed by the upstream run: a replayed completion maps to the run that already exists.
                var run = await orchestrator.EnqueueChainedAsync(dependent.Id, runId, ct);
                log.LogInformation("Job {Upstream} completed: chained job {Job} queued as run {RunId}.", jobId, dependent.Id, run.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken dependent (e.g. cancelled meanwhile) must not stop the others or fail the upstream job.
                log.LogWarning(ex, "Could not start chained job {Job} after {Upstream} completed.", dependent.Id, jobId);
            }
        }
    }
}

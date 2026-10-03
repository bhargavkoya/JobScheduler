using JobScheduler.Application.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Infrastructure.Runs;

/// <summary>Drains the queue and runs each job run through the pipeline, one scope per message.</summary>
public class JobRunWorker(IJobQueue queue, IServiceScopeFactory scopes, ILogger<JobRunWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var runId in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<JobRunner>().ExecuteAsync(runId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One bad message must not stop the worker.
                    log.LogError(ex, "Unhandled error processing run {RunId}.", runId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }
}

/// <summary>On startup, rebuild triggers and re-queue interrupted runs from the database.</summary>
public class ExecutionStartupService(IServiceScopeFactory scopes, ILogger<ExecutionStartupService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<StartupRecovery>().RecoverAsync(cancellationToken);
        log.LogInformation("Startup recovery: {Jobs} job(s) rescheduled, {Runs} run(s) re-queued.",
            result.JobsRescheduled, result.RunsRequeued);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using JobScheduler.Domain.Jobs;

namespace JobScheduler.Application.Jobs;

/// <summary>How long a job may run, or wait on a human, before it is flagged at risk.</summary>
public sealed record AtRiskPolicy(TimeSpan Threshold)
{
    public static readonly AtRiskPolicy Default = new(TimeSpan.FromMinutes(15));
}

/// <summary>
/// At-risk is computed on read, never stored, so it cannot go stale. It flags trouble before a hard failure
/// (PRD section 5): a Fixed run time that passed without the job starting, a run that has been going too long,
/// or an approval that has been waiting too long.
/// </summary>
public static class AtRiskEvaluator
{
    /// <summary>The scheduler fires due jobs immediately, so a Scheduled job this far past its time was missed.</summary>
    public static readonly TimeSpan LateGrace = TimeSpan.FromMinutes(1);

    public static string? Evaluate(Job job, DateTime nowUtc, TimeSpan threshold)
    {
        switch (job.Status)
        {
            case JobStatus.Scheduled when job.ScheduleType == ScheduleType.Fixed && job.RunAtUtc is { } runAt
                                          && nowUtc - runAt > LateGrace:
                return $"Scheduled run time passed {Minutes(nowUtc - runAt)} min ago and the job has not started.";
            case JobStatus.InProgress when nowUtc - job.StatusChangedAtUtc > threshold:
                return $"Running for {Minutes(nowUtc - job.StatusChangedAtUtc)} min (limit {Minutes(threshold)}).";
            case JobStatus.NeedsManualAction when nowUtc - job.StatusChangedAtUtc > threshold:
                return $"Waiting on a manual action for {Minutes(nowUtc - job.StatusChangedAtUtc)} min (limit {Minutes(threshold)}).";
            default:
                return null;
        }
    }

    private static int Minutes(TimeSpan span) => (int)Math.Floor(span.TotalMinutes);
}

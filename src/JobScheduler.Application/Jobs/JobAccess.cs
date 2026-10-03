using JobScheduler.Application.Common;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Jobs;

/// <summary>Shared team-visibility rules for jobs.</summary>
internal static class JobAccess
{
    /// <summary>Null means every team. Otherwise: own team + observer teams.</summary>
    public static IReadOnlyCollection<Team>? VisibleTeams(ICurrentUser me)
    {
        if (me.Role == Role.Admin || me.Permissions.Contains(Permissions.ViewOtherTeamsJobs))
            return null;
        return me.ObserverTeams.Append(me.Team).Distinct().ToList();
    }

    public static async Task<Job> LoadVisibleAsync(IJobStore jobs, ICurrentUser me, Guid id, CancellationToken ct)
    {
        var job = await jobs.FindAsync(id, ct);
        var visible = VisibleTeams(me);
        // Hidden jobs look the same as missing ones so existence isn't leaked across teams.
        if (job is null || (visible is not null && !visible.Contains(job.Team)))
            throw new NotFoundException($"Job '{id}' was not found.");
        return job;
    }
}

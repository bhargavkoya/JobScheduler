namespace JobScheduler.Infrastructure.Persistence;

/// <summary>
/// Not a domain entity — a trivial marker row used only to prove the EF Core
/// migration pipeline works end-to-end in Phase 0, before any real domain
/// entities exist (added in Phase 1).
/// </summary>
public class SystemInfo
{
    public int Id { get; set; }
    public string Component { get; set; } = "JobScheduler.Api";
    public DateTime InitializedAtUtc { get; set; }
}

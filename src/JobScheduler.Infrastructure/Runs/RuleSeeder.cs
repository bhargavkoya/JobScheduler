using System.Text.Json;
using JobScheduler.Domain.Runs;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Runs;

/// <summary>Seeds demo calculation rules (idempotent, keyed by name). Figures match the mock finance report.</summary>
public class RuleSeeder(JobSchedulerDbContext db)
{
    public async Task SeedAsync(CancellationToken ct)
    {
        await AddIfMissingAsync("Total Position", "Sum of all account amounts.", "SumAmount", new(), ct);
        await AddIfMissingAsync("Exposure Cap", "Total must stay within -5000..5000.", "Threshold",
            new() { ["min"] = "-5000", ["max"] = "5000" }, ct);
        // The mock report totals 1048.75, so a 1000 expectation at 1% tolerance fails: handy for the demo.
        await AddIfMissingAsync("Variance vs Ledger", "Total within 1% of the ledger figure of 1000.", "Variance",
            new() { ["expected"] = "1000", ["tolerancePercent"] = "1" }, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task AddIfMissingAsync(string name, string description, string type, Dictionary<string, string> parameters, CancellationToken ct)
    {
        if (await db.CalculationRules.AnyAsync(r => r.Name == name, ct)) return;
        db.CalculationRules.Add(new CalculationRule
        {
            Name = name,
            Description = description,
            Type = type,
            ParametersJson = JsonSerializer.Serialize(parameters)
        });
    }
}

namespace JobScheduler.Domain.Runs;

/// <summary>
/// A calculation rule stored as data: a rule <see cref="Type"/> (resolved to an IRule implementation)
/// plus its parameters. Jobs reference rules by name, so rules can be authored without a code change.
/// </summary>
public class CalculationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    /// <summary>JSON object of string parameters, interpreted by the rule type.</summary>
    public string ParametersJson { get; set; } = "{}";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

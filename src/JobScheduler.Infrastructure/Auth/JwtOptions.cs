namespace JobScheduler.Infrastructure.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "JobScheduler";
    public string Audience { get; set; } = "JobScheduler.Web";

    /// <summary>Signing key, at least 32 chars. Set via user-secrets or env var Jwt__Key; never commit.</summary>
    public string Key { get; set; } = string.Empty;

    public int ExpiryMinutes { get; set; } = 60;
}

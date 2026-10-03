namespace JobScheduler.Application.Auth;

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<UserProfile> GetProfileAsync(Guid userId, CancellationToken ct);
}

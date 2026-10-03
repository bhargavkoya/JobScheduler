using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Auth;

public interface IUserStore
{
    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct);
    Task<User?> FindByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface ITokenService
{
    TokenResult CreateToken(User user);
}

public sealed record TokenResult(string Token, DateTime ExpiresAtUtc);

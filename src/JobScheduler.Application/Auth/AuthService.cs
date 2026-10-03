using System.Net.Mail;
using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Auth;

public class AuthService(IUserStore users, IPasswordHasher hasher, ITokenService tokens) : IAuthService
{
    public const int MinPasswordLength = 8;

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        if (!IsValidEmail(email))
            throw new ValidationException("A valid email address is required.");
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < MinPasswordLength)
            throw new ValidationException($"Password must be at least {MinPasswordLength} characters.");

        if (await users.FindByEmailAsync(email, ct) is not null)
            throw new DuplicateEmailException(email);

        // Self-registration always yields a plain Business employee with no claims;
        // only an Admin can change team, role or claims.
        var user = new User
        {
            Email = email,
            PasswordHash = hasher.Hash(request.Password),
            Role = Role.Employee,
            PrimaryTeam = Team.Business
        };

        await users.AddAsync(user, ct);
        await users.SaveChangesAsync(ct);
        return BuildResult(user);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(NormalizeEmail(request.Email), ct);
        // Same error for unknown email and wrong password to avoid user enumeration.
        if (user is null || !hasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
            throw new InvalidCredentialsException();

        return BuildResult(user);
    }

    public async Task<UserProfile> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct) ?? throw new UserNotFoundException(userId);
        return user.ToProfile();
    }

    private AuthResult BuildResult(User user)
    {
        var token = tokens.CreateToken(user);
        return new AuthResult(token.Token, token.ExpiresAtUtc, user.ToProfile());
    }

    private static bool IsValidEmail(string email)
    {
        if (email.Length is 0 or > 256) return false;
        return MailAddress.TryCreate(email, out var parsed) && parsed.Address == email;
    }
}

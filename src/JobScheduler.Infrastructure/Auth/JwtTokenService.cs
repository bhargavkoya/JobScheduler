using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using JobScheduler.Application.Auth;
using JobScheduler.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JobScheduler.Infrastructure.Auth;

public class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public TokenResult CreateToken(User user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(AppClaimTypes.Role, user.Role.ToString()),
            new(AppClaimTypes.Team, user.PrimaryTeam.ToString())
        };
        claims.AddRange(user.ObserverTeams.Select(t => new Claim(AppClaimTypes.ObserverTeam, t.Team.ToString())));
        claims.AddRange(user.Claims.Select(c => new Claim(AppClaimTypes.Permission, c.Permission)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new TokenResult(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

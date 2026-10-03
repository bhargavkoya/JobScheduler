using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace JobScheduler.Infrastructure.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddAuthInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddScoped<IUserStore, EfUserStore>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<DemoDataSeeder>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
        services.AddScoped<ITemplateStore, EfTemplateStore>();
        services.AddScoped<IJobStore, EfJobStore>();
        services.AddScoped<ITemplateService, TemplateService>();
        services.AddScoped<IJobService, JobService>();
        services.AddScoped<TemplateSeeder>();
        return services;
    }
}

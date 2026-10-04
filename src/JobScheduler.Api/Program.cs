using System.Security.Cryptography;
using System.Text;
using JobScheduler.Api;
using JobScheduler.Infrastructure.Auth;
using JobScheduler.Api.Controllers;
using JobScheduler.Infrastructure.Jobs;
using JobScheduler.Infrastructure.Runs;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("JobSchedulerDb")
    ?? throw new InvalidOperationException(
        "Connection string 'JobSchedulerDb' is not configured. Set it via user-secrets or an environment variable.");

builder.Services.AddDbContext<JobSchedulerDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddAuthInfrastructure();
builder.Services.AddExecutionInfrastructure(builder.Configuration);
builder.Services.Configure<FinanceStubOptions>(builder.Configuration.GetSection(FinanceStubOptions.SectionName));
builder.Services.AddSingleton<MockFinanceState>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (jwtOptions.Key.Length < 32)
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException(
            "Jwt:Key must be at least 32 characters. Set it via user-secrets or the Jwt__Key environment variable.");

    // Dev convenience: ephemeral key so a fresh clone runs without secrets (tokens die on restart).
    jwtOptions.Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}
builder.Services.AddSingleton(Options.Create(jwtOptions));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep "sub", "role", "perm" as issued
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            RoleClaimType = AppClaimTypes.Role,
            NameClaimType = "sub",
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization(options => options.AddAppPolicies());
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<JobSchedulerDbContext>();

const string WebClientCorsPolicy = "WebClient";
builder.Services.AddCors(options =>
{
    options.AddPolicy(WebClientCorsPolicy, policy =>
        policy.WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<JobSchedulerDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<TemplateSeeder>().SeedAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<RuleSeeder>().SeedAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<DemoJobSeeder>().SeedAsync(CancellationToken.None);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors(WebClientCorsPolicy);

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

using JobScheduler.Application.Approvals;
using JobScheduler.Application.Calculation;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Notifications;
using JobScheduler.Application.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace JobScheduler.Infrastructure.Runs;

public static class ExecutionServiceCollectionExtensions
{
    public static IServiceCollection AddExecutionInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var finance = config.GetSection(FinanceAppOptions.SectionName).Get<FinanceAppOptions>() ?? new FinanceAppOptions();

        services.AddSingleton<IJobQueue, ChannelJobQueue>();
        services.AddScoped<IJobRunStore, EfJobRunStore>();
        services.AddScoped<IRunOrchestrator, RunOrchestrator>();
        services.AddScoped<IJobRunService, JobRunService>();
        services.AddScoped<IJobChainer, JobChainer>();
        services.AddScoped<ITriggerService, TriggerService>();
        services.AddSingleton<IRunHistoryExporter, CsvRunHistoryExporter>();
        services.AddSingleton<IRunHistoryExporter, PdfRunHistoryExporter>();
        services.AddScoped<JobRunner>();
        services.AddScoped<StartupRecovery>();

        services.AddScoped<IPipelineStep, DownloadReportStep>();
        services.AddScoped<IPipelineStep, CalculateStep>();
        services.AddScoped<IPipelineStep, SendEmailStep>();

        services.AddHttpClient<IFinanceAppClient, HttpFinanceAppClient>(client =>
        {
            client.BaseAddress = new Uri(finance.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddSingleton<IRule, SumAmountRule>();
        services.AddSingleton<IRule, ThresholdRule>();
        services.AddSingleton<IRule, VarianceRule>();
        services.AddSingleton<IRuleRegistry, RuleRegistry>();
        services.AddScoped<IRuleStore, EfRuleStore>();
        services.AddScoped<IRuleService, RuleService>();
        services.AddScoped<ICalculationEngine, RulesCalculationEngine>();
        services.AddScoped<RuleSeeder>();
        services.AddScoped<JobScheduler.Infrastructure.Jobs.DemoJobSeeder>();

        var email = config.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
        if (email.IsConfigured)
        {
            services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
            services.AddScoped<ISentEmailStore, EfSentEmailStore>();
            services.AddScoped<IdempotentEmailSender>();
            services.AddScoped<IEmailSender>(sp => ActivatorUtilities.CreateInstance<PreferenceAwareEmailSender>(
                sp, (IEmailSender)sp.GetRequiredService<IdempotentEmailSender>()));
        }
        else
        {
            // No Gmail credentials configured: log instead of sending so the app still runs and tests stay green.
            // Singleton, because it dedupes by key in memory; the preference check wraps it per request.
            services.AddSingleton<LoggingEmailSender>();
            services.AddScoped<IEmailSender>(sp => ActivatorUtilities.CreateInstance<PreferenceAwareEmailSender>(
                sp, (IEmailSender)sp.GetRequiredService<LoggingEmailSender>()));
        }
        services.AddScoped<IFailureNotifier, OwnerFailureNotifier>();
        services.AddScoped<ICompletionNotifier, OwnerCompletionNotifier>();
        services.AddScoped<INotificationPreferenceStore, EfNotificationPreferenceStore>();
        services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();

        services.AddScoped<IApprovalStore, EfApprovalStore>();
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<IApprovalFollowUp, ApprovalFollowUp>();
        var atRiskMinutes = config.GetValue<int?>("AtRisk:ThresholdMinutes") ?? 15;
        services.AddSingleton(new AtRiskPolicy(TimeSpan.FromMinutes(Math.Max(1, atRiskMinutes))));

        // In-memory Quartz store: the database stays the source of truth and StartupRecovery rebuilds triggers.
        services.AddQuartz(q => q.UseInMemoryStore());
        services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
        services.AddSingleton<IJobScheduler, QuartzJobScheduler>();

        services.AddHostedService<JobRunWorker>();
        services.AddHostedService<ExecutionStartupService>();
        return services;
    }
}

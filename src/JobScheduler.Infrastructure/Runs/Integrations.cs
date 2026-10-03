using System.Collections.Concurrent;
using System.Net.Http.Json;
using JobScheduler.Application.Runs;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Infrastructure.Runs;

public class FinanceAppOptions
{
    public const string SectionName = "FinanceApp";

    /// <summary>Must end with '/'. Defaults to the mock finance endpoints hosted by this API.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5031/mock-finance/";
}

/// <summary>REST client for the internal finance app. In the POC it points at the mock endpoints in the Api.</summary>
public class HttpFinanceAppClient(HttpClient http) : IFinanceAppClient
{
    private sealed record TriggerRequest(string IdempotencyKey, string ReportName);
    private sealed record TriggerResponse(string ReportId);

    public async Task<string> TriggerReportAsync(string idempotencyKey, string reportName, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("reports", new TriggerRequest(idempotencyKey, reportName), ct);
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadFromJsonAsync<TriggerResponse>(ct);
        return body?.ReportId ?? throw new InvalidOperationException("Finance app returned no report id.");
    }

    public async Task<ReportData> DownloadReportAsync(string reportId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"reports/{Uri.EscapeDataString(reportId)}", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ReportData>(ct)
            ?? throw new InvalidOperationException("Finance app returned an empty report.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Finance app returned {(int)response.StatusCode}: {body}");
    }
}

/// <summary>Placeholder until Gmail is wired in Phase 4. Logs instead of sending, and dedupes by idempotency key.</summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> log) : IEmailSender
{
    private readonly ConcurrentDictionary<string, byte> _sent = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (!_sent.TryAdd(message.IdempotencyKey, 0))
        {
            log.LogInformation("STUB EMAIL suppressed (already sent for key {Key}).", message.IdempotencyKey);
            return Task.CompletedTask;
        }

        log.LogInformation("STUB EMAIL to {To}: {Subject}", message.To, message.Subject);
        return Task.CompletedTask;
    }
}

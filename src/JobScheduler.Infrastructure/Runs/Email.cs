using JobScheduler.Application.Runs;
using JobScheduler.Domain.Runs;
using JobScheduler.Infrastructure.Persistence;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace JobScheduler.Infrastructure.Runs;

public class EmailOptions
{
    public const string SectionName = "Email";

    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;

    /// <summary>The Gmail address. Secret-ish: set via user-secrets or Email__Username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>A Gmail app password. Never commit it: user-secrets or the Email__Password env var.</summary>
    public string Password { get; set; } = string.Empty;

    public string FromName { get; set; } = "Job Scheduler";

    /// <summary>Defaults to <see cref="Username"/> (Gmail rewrites other senders anyway).</summary>
    public string? FromAddress { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}

/// <summary>Gmail SMTP via MailKit: STARTTLS on 587 with an app password.</summary>
public class SmtpEmailTransport(IOptions<EmailOptions> options) : IEmailTransport
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress ?? o.Username));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(o.Username, o.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

public class EfSentEmailStore(JobSchedulerDbContext db) : ISentEmailStore
{
    public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct) =>
        db.SentEmails.AnyAsync(e => e.IdempotencyKey == idempotencyKey, ct);

    public async Task<bool> TryRecordAsync(SentEmail email, CancellationToken ct)
    {
        db.SentEmails.Add(email);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(email).State = EntityState.Detached;
            return false;
        }
    }
}

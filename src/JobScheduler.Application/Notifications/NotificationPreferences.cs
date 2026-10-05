using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Users;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Application.Notifications;

public sealed record NotificationPreferenceDto(NotificationEvent Event, bool Enabled);

public sealed record SaveNotificationPreferencesRequest(IReadOnlyList<NotificationPreferenceDto>? Preferences);

public interface INotificationPreferenceStore
{
    Task<IReadOnlyList<NotificationPreference>> ListForUserAsync(Guid userId, CancellationToken ct);
    Task<NotificationPreference?> FindAsync(Guid userId, NotificationEvent evt, CancellationToken ct);
    Task AddAsync(NotificationPreference preference, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface INotificationPreferenceService
{
    /// <summary>One entry per event, so the UI always shows every toggle. Missing rows count as enabled.</summary>
    Task<IReadOnlyList<NotificationPreferenceDto>> GetMineAsync(CancellationToken ct);
    Task<IReadOnlyList<NotificationPreferenceDto>> SaveMineAsync(SaveNotificationPreferencesRequest request, CancellationToken ct);
}

public class NotificationPreferenceService(INotificationPreferenceStore store, ICurrentUser me) : INotificationPreferenceService
{
    public async Task<IReadOnlyList<NotificationPreferenceDto>> GetMineAsync(CancellationToken ct)
    {
        var saved = (await store.ListForUserAsync(me.UserId, ct)).ToDictionary(p => p.Event, p => p.Enabled);
        return Enum.GetValues<NotificationEvent>()
            .Select(e => new NotificationPreferenceDto(e, saved.GetValueOrDefault(e, true)))
            .ToList();
    }

    public async Task<IReadOnlyList<NotificationPreferenceDto>> SaveMineAsync(
        SaveNotificationPreferencesRequest request, CancellationToken ct)
    {
        var incoming = request.Preferences ?? throw new ValidationException("Preferences are required.");
        if (incoming.Any(p => !Enum.IsDefined(p.Event)))
            throw new ValidationException("Unknown notification event.");

        foreach (var pref in incoming.GroupBy(p => p.Event).Select(g => g.Last()))
        {
            var existing = await store.FindAsync(me.UserId, pref.Event, ct);
            if (existing is null)
                await store.AddAsync(new NotificationPreference { UserId = me.UserId, Event = pref.Event, Enabled = pref.Enabled }, ct);
            else
                existing.Enabled = pref.Enabled;
        }

        await store.SaveChangesAsync(ct);
        return await GetMineAsync(ct);
    }
}

/// <summary>
/// Enforces notification preferences in one place: every email tagged with an event passes through here.
/// Recipients who are not registered users (a free-text address on a job) cannot have preferences, so they always get mail.
/// A skipped email records nothing, so it is neither "sent" nor deduplicated.
/// </summary>
public class PreferenceAwareEmailSender(
    IEmailSender inner, IUserStore users, INotificationPreferenceStore preferences, ILogger<PreferenceAwareEmailSender> log)
    : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (message.Event is { } evt && await IsMutedAsync(message.To, evt, ct))
        {
            log.LogInformation("Email to {To} skipped: {Event} notifications are switched off.", message.To, evt);
            return;
        }

        await inner.SendAsync(message, ct);
    }

    private async Task<bool> IsMutedAsync(string to, NotificationEvent evt, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(AuthService.NormalizeEmail(to), ct);
        if (user is null) return false;
        var pref = await preferences.FindAsync(user.Id, evt, ct);
        return pref is { Enabled: false };
    }
}

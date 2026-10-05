using JobScheduler.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

/// <summary>The signed-in user's own email preferences: which events they want mailed to them.</summary>
[ApiController]
[Authorize]
[Route("me/notification-preferences")]
public class NotificationPreferencesController(INotificationPreferenceService preferences) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NotificationPreferenceDto>>> Get(CancellationToken ct) =>
        Ok(await preferences.GetMineAsync(ct));

    [HttpPut]
    public async Task<ActionResult<IReadOnlyList<NotificationPreferenceDto>>> Save(
        SaveNotificationPreferencesRequest request, CancellationToken ct) =>
        Ok(await preferences.SaveMineAsync(request, ct));
}

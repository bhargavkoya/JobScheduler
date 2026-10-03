namespace JobScheduler.Domain.Jobs;

/// <summary>
/// The app uses a single timezone, IST (UTC+05:30, no DST). A fixed offset avoids any
/// dependency on the host's tz database and matches the PRD's no-calendars scope.
/// </summary>
public static class Ist
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);

    public static DateTime ToUtc(DateTime istLocal) =>
        new DateTimeOffset(DateTime.SpecifyKind(istLocal, DateTimeKind.Unspecified), Offset).UtcDateTime;

    /// <summary>Returns an IST wall-clock time with Kind=Unspecified, so it serializes without a "Z".</summary>
    public static DateTime FromUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc.Add(Offset), DateTimeKind.Unspecified);
}

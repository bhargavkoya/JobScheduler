using System.Globalization;
using JobScheduler.Application.Auth;

namespace JobScheduler.Application.Jobs;

public enum RecurrenceFrequency
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2
}

/// <summary>Friendly recurrence, all in IST. Time is "HH:mm". DayOfWeek: 0=Sunday..6=Saturday. DayOfMonth: 1-28.</summary>
public sealed record RecurrenceDto(RecurrenceFrequency Frequency, string Time, int? DayOfWeek, int? DayOfMonth);

public sealed record RecurrenceSchedule(string Cron, string Text);

/// <summary>Maps the friendly recurrence to a Quartz cron expression (fired in IST by the scheduler).</summary>
public static class RecurrenceBuilder
{
    private static readonly string[] Days = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
    private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    public static RecurrenceSchedule Build(RecurrenceDto? r)
    {
        if (r is null) throw new ValidationException("Recurrent jobs require a recurrence.");
        if (!TimeOnly.TryParseExact(r.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new ValidationException("Recurrence time must be in HH:mm format (IST).");

        var at = $"{time.Minute} {time.Hour}";
        var label = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        switch (r.Frequency)
        {
            case RecurrenceFrequency.Daily:
                return new($"0 {at} * * ?", $"Daily at {label} IST");
            case RecurrenceFrequency.Weekly:
                if (r.DayOfWeek is not (>= 0 and <= 6))
                    throw new ValidationException("Weekly recurrence needs a day of week (0=Sunday..6=Saturday).");
                return new($"0 {at} ? * {Days[r.DayOfWeek.Value]}", $"Weekly on {DayNames[r.DayOfWeek.Value]} at {label} IST");
            case RecurrenceFrequency.Monthly:
                // Capped at 28 so every month has the day (no end-of-month surprises).
                if (r.DayOfMonth is not (>= 1 and <= 28))
                    throw new ValidationException("Monthly recurrence needs a day of month between 1 and 28.");
                return new($"0 {at} {r.DayOfMonth} * ?", $"Monthly on day {r.DayOfMonth} at {label} IST");
            default:
                throw new ValidationException("Unknown recurrence frequency.");
        }
    }
}

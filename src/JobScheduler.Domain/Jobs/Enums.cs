namespace JobScheduler.Domain.Jobs;

public enum ScheduleType
{
    Fixed = 0,
    Recurrent = 1,
    EventBased = 2,
    TriggerBased = 3,
    Manual = 4
}

public enum JobStatus
{
    Scheduled = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3,
    Failed = 4,
    NeedsManualAction = 5
}

public enum FieldType
{
    String = 0,
    Text = 1,
    Number = 2,
    Email = 3
}

namespace JobScheduler.Domain.Reconciliation;

/// <summary>
/// A CSV file uploaded as reconciliation input. Uploaded first (unattached), then attached to a job under an alias
/// that rules use to refer to it. Replacing a job's file supersedes the old row instead of deleting it, so past
/// runs stay explainable.
/// </summary>
public class InputFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }

    /// <summary>Null until the file is attached to a job.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Short name rules use for this file, e.g. "internal". Unique per job.</summary>
    public string Alias { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>JSON array of the column names, read from the header row at upload time.</summary>
    public string HeadersJson { get; set; } = "[]";

    public int RowCount { get; set; }
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Set when a newer file took over this alias on the job.</summary>
    public DateTime? SupersededAtUtc { get; set; }
}

/// <summary>The raw bytes of an <see cref="InputFile"/>, kept in their own table so listing files never loads them.</summary>
public class InputFileContent
{
    public Guid FileId { get; set; }
    public byte[] Bytes { get; set; } = [];
}

public enum ComparisonOperator
{
    Equal = 0,
    NotEqual = 1,
    LessThan = 2,
    LessThanOrEqual = 3,
    GreaterThan = 4,
    GreaterThanOrEqual = 5
}

public enum ToleranceMode
{
    /// <summary>The two sides may differ by up to this amount.</summary>
    Absolute = 0,

    /// <summary>The two sides may differ by up to this percentage of the right-hand value.</summary>
    Percent = 1
}

/// <summary>
/// One record-by-record check stored as data: join the two files on a key, compute a left and a right expression
/// over their columns, and compare. File and column names come from the uploaded files, never from code.
/// </summary>
public class ReconciliationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = string.Empty;

    public string LeftAlias { get; set; } = string.Empty;
    public string RightAlias { get; set; } = string.Empty;

    /// <summary>Join key columns. Optional only when both aliases are the same file (a within-file check).</summary>
    public string? LeftKeyColumn { get; set; }
    public string? RightKeyColumn { get; set; }

    public string LeftExpression { get; set; } = string.Empty;
    public ComparisonOperator Operator { get; set; }
    public string RightExpression { get; set; } = string.Empty;

    public decimal Tolerance { get; set; }
    public ToleranceMode ToleranceMode { get; set; }

    public bool IsSingleFile => string.Equals(LeftAlias, RightAlias, StringComparison.OrdinalIgnoreCase);
}

public enum RecordStatus
{
    Pass = 0,
    Fail = 1
}

/// <summary>The outcome of one rule for one record (or one unmatched record) in one run.</summary>
public class ReconciliationResult
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public Guid RuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;

    /// <summary>The join key value (or the row number for within-file rules without a key).</summary>
    public string RecordKey { get; set; } = string.Empty;

    /// <summary>1-based data row numbers in the left/right file (header excluded). Null when absent from that file.</summary>
    public int? LeftRow { get; set; }
    public int? RightRow { get; set; }

    public decimal? LeftValue { get; set; }
    public decimal? RightValue { get; set; }
    public decimal? Difference { get; set; }

    public RecordStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
}

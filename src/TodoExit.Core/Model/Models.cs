namespace TodoExit.Core.Model;

public enum Importance
{
    Low,
    Normal,
    High,
}

/// <summary>The typed view of an archive. Built by <see cref="ArchiveParser"/>.</summary>
public sealed record TodoArchive(DateTimeOffset? ExportedAt, IReadOnlyList<TaskList> Lists)
{
    /// <summary>What the read could not get from Graph. Empty for archives without "readNotes".</summary>
    public ReadNotes ReadNotes { get; init; } = ReadNotes.Empty;
}

public sealed record TaskList(
    string Id,
    string Name,
    bool IsOwner,
    bool IsShared,
    string? WellknownName,
    IReadOnlyList<TodoItem> Tasks);

public sealed record TodoItem
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>True when only some properties could be read from Graph ("todoExitPartial": true).</summary>
    public bool IsPartial { get; init; }

    /// <summary>Plain-text notes. Null when the body is empty.</summary>
    public string? Notes { get; init; }

    public bool IsCompleted { get; init; }

    public Importance Importance { get; init; } = Importance.Normal;

    public DateOnly? DueDate { get; init; }

    public DateOnly? StartDate { get; init; }

    /// <summary>Reminder in the target time zone. Only set when isReminderOn is true.</summary>
    public DateTime? Reminder { get; init; }

    /// <summary>Completion time in the target time zone.</summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>Creation time in the target time zone.</summary>
    public DateTime? CreatedAt { get; init; }

    /// <summary>Last modification time in the target time zone.</summary>
    public DateTime? ModifiedAt { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<Step> Steps { get; init; } = [];

    public Recurrence? Recurrence { get; init; }

    public IReadOnlyList<LinkedResource> LinkedResources { get; init; } = [];

    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];

    /// <summary>Graph's hasAttachments flag. Can be true while <see cref="Attachments"/> is empty (names not readable).</summary>
    public bool HasAttachments { get; init; }

    /// <summary>The task has attachments, whether or not their names are known.</summary>
    public bool HasAnyAttachment => HasAttachments || Attachments.Count > 0;

    /// <summary>The task has attachments but Graph did not give their names.</summary>
    public bool AttachmentNamesUnknown => HasAttachments && Attachments.Count == 0;
}

public sealed record Step(string Title, bool IsChecked);

public sealed record LinkedResource(string? DisplayName, string? ApplicationName, string? WebUrl);

public sealed record AttachmentInfo(string Name, long? Size, string? ContentType);

/// <summary>
/// Graph recurrence pattern and range. String values are kept as Graph spells them
/// (pattern: daily, weekly, absoluteMonthly, relativeMonthly, absoluteYearly, relativeYearly;
/// range: noEnd, endDate, numbered; index: first, second, third, fourth, last).
/// </summary>
public sealed record Recurrence
{
    public string PatternType { get; init; } = "daily";

    public int Interval { get; init; } = 1;

    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = [];

    public int DayOfMonth { get; init; }

    public int Month { get; init; }

    public string? Index { get; init; }

    public string RangeType { get; init; } = "noEnd";

    public DateOnly? RangeStartDate { get; init; }

    public DateOnly? RangeEndDate { get; init; }

    public int NumberOfOccurrences { get; init; }
}

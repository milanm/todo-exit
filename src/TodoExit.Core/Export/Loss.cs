using System.Globalization;
using TodoExit.Core.Model;

namespace TodoExit.Core.Export;

/// <summary>Output formats. The JSON archive is lossless; the others report what they could not carry.</summary>
[Flags]
public enum ExportFormats
{
    None = 0,
    Json = 1,
    Markdown = 2,
    Todoist = 4,
    Csv = 8,
    All = Json | Markdown | Todoist | Csv,
}

public static class ExportFormatNames
{
    public const string Json = "json";
    public const string Markdown = "markdown";
    public const string Todoist = "todoist";
    public const string Csv = "csv";

    /// <summary>Parses "json,markdown,todoist,csv". Returns false and the offending name on error.</summary>
    public static bool TryParse(string? value, out ExportFormats formats, out string? invalid)
    {
        formats = ExportFormats.None;
        invalid = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            invalid = string.Empty;
            return false;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case Json: formats |= ExportFormats.Json; break;
                case Markdown: case "md": formats |= ExportFormats.Markdown; break;
                case Todoist: formats |= ExportFormats.Todoist; break;
                case Csv: formats |= ExportFormats.Csv; break;
                case "all": formats |= ExportFormats.All; break;
                default:
                    invalid = part;
                    return false;
            }
        }

        if (formats == ExportFormats.None)
        {
            invalid = value;
            return false;
        }

        return true;
    }
}

public static class LossKinds
{
    public const string CompletedSkipped = "completed-skipped";
    public const string CheckedStepSkipped = "checked-step-skipped";
    public const string ReminderNotCarried = "reminder-not-carried";
    public const string AttachmentNotCarried = "attachment-not-carried";
    public const string RecurrenceApproximated = "recurrence-approximated";
    public const string SharedListMembersNotCarried = "shared-list-members-not-carried";
    public const string StartDateNotCarried = "start-date-not-carried";
    public const string StepOverflow = "step-overflow";
}

/// <summary>
/// Something an export format could not carry over. Bulk kinds are aggregated per format, list and kind
/// (<see cref="Aggregated"/> is true, <see cref="Task"/> is null and <see cref="Count"/> says how many).
/// </summary>
public sealed record Loss(string Format, string List, string? Task, string Kind, string Message, int Count = 1)
{
    /// <summary>For per-task entries: whether the task is open. Completed tasks are never listed individually.</summary>
    public bool TaskIsOpen { get; init; } = true;

    /// <summary>True for an entry that stands for <see cref="Count"/> tasks or steps of one list.</summary>
    public bool Aggregated { get; init; }
}

/// <summary>Plain-language meaning of each loss and read-error kind, and the aggregation rules.</summary>
public static class LossCatalog
{
    /// <summary>Kinds reported as one entry per format, list and kind, with a count.</summary>
    public static readonly IReadOnlySet<string> BulkKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        LossKinds.CompletedSkipped,
        LossKinds.AttachmentNotCarried,
        LossKinds.CheckedStepSkipped,
        LossKinds.ReminderNotCarried,
        LossKinds.StartDateNotCarried,
    };

    public static string Meaning(string kind) => kind switch
    {
        LossKinds.CompletedSkipped => "Completed tasks are left out on purpose: the Todoist CSV format cannot mark a task as done.",
        LossKinds.CheckedStepSkipped => "Checked steps are left out because they cannot be imported as done.",
        LossKinds.ReminderNotCarried => "Todoist cannot import reminders from CSV; set these again in Todoist.",
        LossKinds.AttachmentNotCarried => "Attachment files are not exported; save the files from To Do if you need them.",
        LossKinds.RecurrenceApproximated => "The repeat rule was written as the closest Todoist phrase; check these tasks after import.",
        LossKinds.SharedListMembersNotCarried => "Sharing and members are not carried; share the Todoist project again.",
        LossKinds.StartDateNotCarried => "Todoist has no start date, so start dates are not carried.",
        LossKinds.StepOverflow => "A task had more steps than one Todoist import allows; the extra steps were not written.",
        ReadErrorKinds.TaskUnreadable => "Graph could not return the task at all; it is missing from every format.",
        ReadErrorKinds.TaskPartial => "Graph failed partway through the task; it is kept with the properties Graph sent.",
        ReadErrorKinds.StepsUnreadable => "The steps of the task could not be read.",
        ReadErrorKinds.AttachmentsUnreadable => "The attachment names of the task could not be read.",
        ReadErrorKinds.AttachmentsDenied => "Graph refused to list attachments with read-only access; tasks with attachments are still marked.",
        _ => kind,
    };

    /// <summary>
    /// Folds bulk kinds into one entry per (format, list, kind). Exception: Todoist reminders and attachments of OPEN
    /// tasks stay per task, because the user re-adds those by hand. Order follows the first occurrence.
    /// </summary>
    public static IReadOnlyList<Loss> Aggregate(IEnumerable<Loss> losses)
    {
        var result = new List<Loss>();
        var index = new Dictionary<(string, string, string), int>();
        var namesUnknown = new HashSet<int>();
        foreach (var loss in losses)
        {
            var perTaskTodoist = loss.Format == ExportFormatNames.Todoist
                && loss.Kind is LossKinds.ReminderNotCarried or LossKinds.AttachmentNotCarried
                && loss.TaskIsOpen;
            if (loss.Aggregated || !BulkKinds.Contains(loss.Kind) || perTaskTodoist)
            {
                result.Add(loss);
                continue;
            }

            var key = (loss.Format, loss.List, loss.Kind);
            if (index.TryGetValue(key, out var i))
            {
                result[i] = result[i] with { Count = result[i].Count + loss.Count };
            }
            else
            {
                i = result.Count;
                index[key] = i;
                result.Add(new Loss(loss.Format, loss.List, null, loss.Kind, string.Empty, loss.Count) { Aggregated = true });
            }

            if (loss.Message.Contains("names could not be read", StringComparison.Ordinal))
            {
                namesUnknown.Add(i);
            }
        }

        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Aggregated && result[i].Message.Length == 0)
            {
                result[i] = result[i] with { Message = AggregateMessage(result[i].Kind, result[i].Count, namesUnknown.Contains(i)) };
            }
        }

        return result;
    }

    private static string AggregateMessage(string kind, int n, bool namesUnknown)
    {
        var count = n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        return kind switch
        {
            LossKinds.CompletedSkipped => $"{count} completed tasks were left out.",
            LossKinds.CheckedStepSkipped => $"{count} checked steps were left out.",
            LossKinds.ReminderNotCarried => $"{count} reminders of completed tasks were not carried.",
            LossKinds.AttachmentNotCarried => $"{count} tasks with attachments; the files are not exported."
                + (namesUnknown ? " Some attachment names could not be read from Microsoft Graph." : string.Empty),
            LossKinds.StartDateNotCarried => $"{count} start dates were not carried.",
            _ => $"{count} items: {Meaning(kind)}",
        };
    }
}

internal static class Fmt
{
    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Date(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string DateTime(DateTime d) => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Size(long? bytes)
    {
        if (bytes is not { } b || b < 0)
        {
            return "unknown size";
        }

        if (b < 1024)
        {
            return b.ToString(CultureInfo.InvariantCulture) + " B";
        }

        if (b < 1024 * 1024)
        {
            return (b / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return (b / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }
}

using System.Globalization;

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

/// <summary>One thing an export format could not carry over.</summary>
public sealed record Loss(string Format, string List, string? Task, string Kind, string Message);

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

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TodoExit.Core.Model;

namespace TodoExit.Core.Export;

/// <summary>What one export run could not carry over, plus the inventory it started from.</summary>
public sealed record LossReport(Inventory Inventory, IReadOnlyList<Loss> Losses, ExportFormats Formats)
{
    /// <summary>What could not be read from Microsoft Graph. Applies to every format, the archive included.</summary>
    public ReadNotes ReadNotes { get; init; } = ReadNotes.Empty;

    /// <summary>Most items any one section of report.md lists; the rest are in report.json.</summary>
    public const int MaxItemsPerSection = 200;

    public const string JsonFileName = "report.json";
    public const string MarkdownFileName = "report.md";

    private static readonly string[] FormatOrder =
        [ExportFormatNames.TodoistApi, ExportFormatNames.Todoist, ExportFormatNames.Markdown, ExportFormatNames.Csv, ExportFormatNames.Json];

    public byte[] ToJsonBytes()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            // LF on every OS, like the other exported files.
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            var c = Inventory.Totals;
            w.WriteStartObject();
            w.WriteBoolean("interrupted", ReadNotes.Interrupted);
            WriteNullable(w, "interruptedReason", ReadNotes.Interrupted ? ReadNotes.InterruptedReason ?? "unknown" : null);
            WriteNullable(w, "listInProgress", ReadNotes.Interrupted ? ListInProgressName() : null);
            w.WriteStartObject("inventory");
            w.WriteNumber("lists", c.Lists);
            w.WriteNumber("tasks", c.Tasks);
            w.WriteNumber("open", c.Open);
            w.WriteNumber("completed", c.Completed);
            w.WriteNumber("recurring", c.Recurring);
            w.WriteNumber("withSteps", c.WithSteps);
            w.WriteNumber("withNotes", c.WithNotes);
            w.WriteNumber("withReminders", c.WithReminders);
            w.WriteNumber("withAttachments", c.WithAttachments);
            w.WriteNumber("withLinkedResources", c.WithLinkedResources);
            w.WriteNumber("sharedLists", c.SharedLists);
            w.WriteNumber("unreadable", c.Unreadable);
            w.WriteNumber("partial", c.Partial);
            w.WriteEndObject();

            w.WriteStartArray("losses");
            foreach (var loss in Losses)
            {
                w.WriteStartObject();
                w.WriteString("format", loss.Format);
                w.WriteString("list", loss.List);
                if (loss.Task is null)
                {
                    w.WriteNull("task");
                }
                else
                {
                    w.WriteString("task", loss.Task);
                }

                w.WriteString("kind", loss.Kind);
                w.WriteNumber("count", loss.Count);
                w.WriteString("message", loss.Message);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("readErrors");
            foreach (var e in ReadNotes.Errors)
            {
                w.WriteStartObject();
                w.WriteString("kind", e.Kind);
                WriteNullable(w, "listId", e.ListId);
                WriteNullable(w, "listName", e.ListName);
                WriteNullable(w, "taskId", e.TaskId);
                WriteNullable(w, "taskTitle", e.TaskTitle);
                if (e.Position is { } position)
                {
                    w.WriteNumber("position", position);
                }
                else
                {
                    w.WriteNull("position");
                }

                w.WriteString("message", e.Message);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    public string ToMarkdown() => ToMarkdown("todo-exit report");

    /// <param name="title">The level-1 heading.</param>
    public string ToMarkdown(string title)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n");
        if (ReadNotes.Interrupted)
        {
            sb.Append("**This archive is incomplete.** The read from Microsoft Graph stopped before it finished: ")
                .Append(EscapeInline(ReadNotes.InterruptedReason ?? "unknown reason")).Append("\n\n");
            if (ListInProgressName() is { } inProgress)
            {
                var p = ReadNotes.ListInProgress!;
                sb.Append("List in progress: **").Append(EscapeInline(inProgress)).Append("** (")
                    .Append(p.TasksComplete ? "all tasks read" : $"tasks read up to position {p.NextSkip.ToString(CultureInfo.InvariantCulture)}")
                    .Append(p.StepsComplete ? ", steps read" : ", steps not finished")
                    .Append(p.AttachmentsComplete ? ", attachment details read" : ", attachment details not finished")
                    .Append(").\n\n");
            }

            sb.Append("Lists that had not been started are missing. Run the read again with --resume to finish it.\n\n");
        }

        sb.Append("## Inventory\n\n");
        sb.Append("| Item | Count |\n|---|---:|\n");
        foreach (var (label, value) in Inventory.Totals.Rows())
        {
            sb.Append("| ").Append(label).Append(" | ").Append(value.ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        }

        AppendReadErrors(sb);

        sb.Append("\n## What was not carried over\n\n");
        var requested = FormatOrder.Where(IsRequested).ToList();
        if (requested.Contains(ExportFormatNames.Json))
        {
            sb.Append(ReadNotes.Errors.Count == 0
                ? "The JSON archive (`archive.json`) keeps everything Microsoft Graph returned. It has no losses.\n\n"
                : "The JSON archive (`archive.json`) keeps everything Microsoft Graph returned. It has no losses of its own, but it lacks what could not be read (see above).\n\n");
        }

        var formats = requested.Where(f => f != ExportFormatNames.Json).ToList();
        if (formats.Count == 0)
        {
            return sb.ToString();
        }

        foreach (var format in formats)
        {
            sb.Append("### ").Append(Title(format)).Append("\n\n");
            var items = Losses.Where(l => l.Format == format).ToList();
            if (items.Count == 0)
            {
                sb.Append("Nothing was lost in this format.\n\n");
                continue;
            }

            AppendKindTable(sb, items.GroupBy(l => l.Kind).Select(g => (g.Key, g.Sum(l => l.Count))), format);
            var perTask = items.Where(l => !l.Aggregated).ToList();
            AppendItems(sb, perTask.Select(l => (l.List, l.Task is null ? null : $"**{EscapeInline(l.Task)}**: ", l.Message, l.Kind)).ToList());
        }

        return sb.ToString();
    }

    private void AppendReadErrors(StringBuilder sb)
    {
        if (ReadNotes.Errors.Count == 0)
        {
            return;
        }

        sb.Append("\n## Could not be read from Microsoft Graph\n\n");
        sb.Append("This applies to every format, the JSON archive included.\n\n");
        AppendKindTable(sb, ReadNotes.Errors.GroupBy(e => e.Kind).Select(g => (g.Key, g.Count())));
        AppendItems(sb, ReadNotes.Errors.Select(e => (
            e.ListName ?? e.ListId ?? "All lists",
            e.TaskTitle is not null
                ? $"**{EscapeInline(e.TaskTitle)}**: "
                : e.Position is { } position ? $"**Task at position {position.ToString(CultureInfo.InvariantCulture)}**: " : null,
            e.Message,
            e.Kind)).ToList());
    }

    private static void AppendKindTable(StringBuilder sb, IEnumerable<(string Kind, int Count)> rows, string? format = null)
    {
        sb.Append("| Kind | Count | What it means |\n|---|---:|---|\n");
        foreach (var (kind, count) in rows.OrderByDescending(r => r.Count).ThenBy(r => r.Kind, StringComparer.Ordinal))
        {
            sb.Append("| ").Append(kind).Append(" | ").Append(count.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" | ").Append(format is null ? LossCatalog.Meaning(kind) : LossCatalog.Meaning(kind, format)).Append(" |\n");
        }

        sb.Append('\n');
    }

    /// <summary>Items grouped by list, at most <see cref="MaxItemsPerSection"/>.</summary>
    private static void AppendItems(StringBuilder sb, List<(string List, string? Prefix, string Message, string Kind)> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var written = 0;
        foreach (var byList in items.GroupBy(i => i.List))
        {
            if (written >= MaxItemsPerSection)
            {
                break;
            }

            sb.Append("#### ").Append(byList.Key).Append("\n\n");
            foreach (var item in byList)
            {
                if (written >= MaxItemsPerSection)
                {
                    break;
                }

                sb.Append("- ").Append(item.Prefix).Append(EscapeInline(item.Message)).Append(" (`").Append(item.Kind).Append("`)\n");
                written++;
            }

            sb.Append('\n');
        }

        if (items.Count > written)
        {
            sb.Append("... and ").Append((items.Count - written).ToString("N0", CultureInfo.InvariantCulture))
                .Append(" more, see report.json.\n\n");
        }
    }

    /// <summary>
    /// At most three short lines for the end of a CLI run: what the Todoist CSV leaves out or needs by hand, and what
    /// could not be read. Empty when there is nothing to say.
    /// </summary>
    public IReadOnlyList<string> SummaryLines()
    {
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        static string Count(int n, string one, string many) => $"{N(n)} {(n == 1 ? one : many)}";
        var lines = new List<string>();
        string? todoistLine = null;
        string? splitLine = null;

        if (Formats.HasFlag(ExportFormats.Todoist))
        {
            var todoist = Losses.Where(l => l.Format == ExportFormatNames.Todoist).ToList();
            int Sum(string kind, bool openOnly = false) =>
                todoist.Where(l => l.Kind == kind && (!openOnly || (!l.Aggregated && l.TaskIsOpen))).Sum(l => l.Count);

            var parts = new List<string>();
            var completed = Sum(LossKinds.CompletedSkipped);
            if (completed > 0)
            {
                parts.Add($"{Count(completed, "completed task", "completed tasks")} left out by design");
            }

            var byHand = new List<string>();
            var reminders = Sum(LossKinds.ReminderNotCarried, openOnly: true);
            var attachments = Sum(LossKinds.AttachmentNotCarried, openOnly: true);
            if (reminders > 0)
            {
                byHand.Add(Count(reminders, "reminder", "reminders"));
            }

            if (attachments > 0)
            {
                byHand.Add(Count(attachments, "attachment", "attachments"));
            }

            if (byHand.Count > 0)
            {
                parts.Add(string.Join(" and ", byHand) + " to re-add by hand");
            }

            var repeats = Sum(LossKinds.RecurrenceApproximated);
            if (repeats > 0)
            {
                parts.Add($"{Count(repeats, "repeat rule", "repeat rules")} to check");
            }

            if (parts.Count > 0)
            {
                todoistLine = "Todoist CSV: " + string.Join(", ", parts) + ".";
            }

            var split = todoist.Count(l => l.Kind == LossKinds.ListSplit);
            if (split > 0)
            {
                splitLine = split == 1
                    ? "1 list was split into parts; import each part into its own Todoist project."
                    : $"{N(split)} lists were split into parts; import each part into its own Todoist project.";
            }
        }

        var read = new List<string>();
        var rest = new List<string>();
        int Errors(string kind) => ReadNotes.Errors.Count(e => e.Kind == kind);
        if (Errors(ReadErrorKinds.TaskPartial) is var partial and > 0)
        {
            read.Add(partial == 1 ? "1 task was only partly readable" : $"{N(partial)} tasks were only partly readable");
        }

        if (Errors(ReadErrorKinds.TaskUnreadable) is var unreadable and > 0)
        {
            read.Add(unreadable == 1 ? "1 task could not be read" : $"{N(unreadable)} tasks could not be read");
        }

        if (Errors(ReadErrorKinds.StepsUnreadable) is var steps and > 0)
        {
            read.Add($"steps of {Count(steps, "task", "tasks")} could not be read");
        }

        if (Errors(ReadErrorKinds.AttachmentsUnreadable) is var att and > 0)
        {
            read.Add($"attachment details of {Count(att, "task", "tasks")} could not be read");
        }

        if (ReadNotes.AttachmentsDenied || Errors(ReadErrorKinds.AttachmentsDenied) > 0)
        {
            read.Add("attachment names were not available");
        }

        if (Errors(ReadErrorKinds.ListChanged) is var changed and > 0)
        {
            read.Add(changed == 1 ? "1 list changed while it was read" : $"{N(changed)} lists changed while they were read");
        }

        if (read.Count > 0)
        {
            var sentence = read.Count <= 2
                ? string.Join(" and ", read)
                : string.Join(", ", read.Take(read.Count - 1)) + " and " + read[^1];
            rest.Add(char.ToUpperInvariant(sentence[0]) + sentence[1..] + ". Details in report.md.");
        }

        if (ReadNotes.Interrupted)
        {
            rest.Add("The archive is incomplete; run the read again with --resume to finish it.");
        }

        // At most three lines: the split hint gets its own line only when there is room.
        if (todoistLine is not null && splitLine is not null && rest.Count >= 2)
        {
            lines.Add(todoistLine + " " + splitLine[0].ToString().ToUpperInvariant() + splitLine[1..]);
        }
        else
        {
            if (todoistLine is not null)
            {
                lines.Add(todoistLine);
            }

            if (splitLine is not null)
            {
                lines.Add(todoistLine is null && rest.Count >= 2 ? "Todoist CSV: " + splitLine : splitLine);
            }
        }

        lines.AddRange(rest);
        return lines.Take(3).ToList();
    }

    /// <summary>Name (or id) of the list the interrupted read was working on.</summary>
    private string? ListInProgressName()
    {
        if (ReadNotes.ListInProgress is not { } p)
        {
            return null;
        }

        return ReadNotes.ListName(p.ListId)
            ?? Inventory.PerList.FirstOrDefault(l => l.ListId == p.ListId)?.ListName
            ?? p.ListId;
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    public IReadOnlyList<ExportFile> ToFiles() =>
    [
        ExportFile.FromText(MarkdownFileName, ToMarkdown()),
        new ExportFile(JsonFileName, ToJsonBytes()),
    ];

    private bool IsRequested(string format) => format switch
    {
        ExportFormatNames.Json => Formats.HasFlag(ExportFormats.Json),
        ExportFormatNames.Markdown => Formats.HasFlag(ExportFormats.Markdown),
        ExportFormatNames.Todoist => Formats.HasFlag(ExportFormats.Todoist),
        ExportFormatNames.Csv => Formats.HasFlag(ExportFormats.Csv),
        ExportFormatNames.TodoistApi => Formats.HasFlag(ExportFormats.TodoistApi),
        _ => false,
    };

    private static string Title(string format) => format switch
    {
        ExportFormatNames.Todoist => "Todoist CSV",
        ExportFormatNames.Markdown => "Markdown",
        ExportFormatNames.Csv => "Generic CSV (tasks.csv)",
        ExportFormatNames.TodoistApi => "Todoist (direct transfer)",
        _ => format,
    };

    private static string EscapeInline(string text) =>
        text.Replace("\r\n", " ").Replace('\n', ' ').Replace("*", "\\*");
}

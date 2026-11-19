using System.Text;
using System.Text.RegularExpressions;
using TodoExit.Core.Model;

namespace TodoExit.Core.Export;

/// <summary>
/// Todoist project import CSV, one file per list under todoist/.
/// Format facts used here: UTF-8, fixed header, TYPE lowercase, PRIORITY 1 = p1 (highest) to 4 = p4,
/// an empty PRIORITY imports as p1 so it is always written, INDENT 1 = task and 2 = sub-task,
/// at most 300 tasks per imported project.
/// </summary>
public static partial class TodoistCsvExporter
{
    public const string Header = "TYPE,CONTENT,DESCRIPTION,PRIORITY,INDENT,AUTHOR,RESPONSIBLE,DATE,DATE_LANG,TIMEZONE,DURATION,DURATION_UNIT";
    public const int MaxTasksPerFile = 300;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\r\n\t]+")]
    private static partial Regex LineBreaksAndTabs();

    /// <summary>CONTENT must stay on one line: runs of CR, LF and tab become a single space.</summary>
    internal static string OneLine(string text) => LineBreaksAndTabs().Replace(text, " ");

    private sealed record Row(string Content, string Description, int Priority, int Indent, string Date, string TaskTitle);

    public static IReadOnlyList<ExportFile> Export(TodoArchive archive, bool includeCompleted, ICollection<Loss>? losses = null)
    {
        losses ??= new List<Loss>();
        var names = FileNames.SanitizeUnique(archive.Lists.Select(l => l.Name));
        var files = new List<ExportFile>();

        for (var i = 0; i < archive.Lists.Count; i++)
        {
            var list = archive.Lists[i];
            if (list.IsShared)
            {
                losses.Add(new Loss(ExportFormatNames.Todoist, list.Name, null, LossKinds.SharedListMembersNotCarried,
                    "This list is shared in To Do. Members and sharing are not carried; share the Todoist project again."));
            }

            var blocks = BuildBlocks(list, includeCompleted, losses);
            var parts = Split(blocks, list, losses);
            if (parts.Count == 0)
            {
                continue;
            }

            for (var p = 0; p < parts.Count; p++)
            {
                var path = parts.Count == 1
                    ? $"todoist/{names[i]}.csv"
                    : $"todoist/{names[i]} - part {p + 1}.csv";
                files.Add(ExportFile.FromText(path, Render(parts[p])));
            }
        }

        return files;
    }

    private static List<List<Row>> BuildBlocks(TaskList list, bool includeCompleted, ICollection<Loss> losses)
    {
        var blocks = new List<List<Row>>();
        foreach (var task in list.Tasks)
        {
            if (task.IsCompleted && !includeCompleted)
            {
                losses.Add(Loss(list, task, LossKinds.CompletedSkipped,
                    "Completed task skipped. The Todoist CSV format cannot mark tasks as completed."));
                continue;
            }

            var block = new List<Row> { TaskRow(list, task, losses) };
            foreach (var step in task.Steps)
            {
                if (step.IsChecked && !includeCompleted)
                {
                    losses.Add(Loss(list, task, LossKinds.CheckedStepSkipped,
                        $"Checked step skipped: {step.Title}"));
                    continue;
                }

                block.Add(new Row(OneLine(step.Title), string.Empty, 4, 2, string.Empty, task.Title));
            }

            blocks.Add(block);
        }

        return blocks;
    }

    private static Row TaskRow(TaskList list, TodoItem task, ICollection<Loss> losses)
    {
        var content = new StringBuilder(OneLine(task.Title));
        foreach (var category in task.Categories)
        {
            content.Append(" @").Append(Whitespace().Replace(category.Trim(), "_"));
        }

        var description = new List<string>();
        if (task.Notes is { } notes)
        {
            description.Add(notes);
        }

        foreach (var link in task.LinkedResources)
        {
            var name = link.DisplayName ?? link.ApplicationName ?? "link";
            description.Add(string.IsNullOrWhiteSpace(link.WebUrl) ? $"Link: {name}" : $"Link: {name} {link.WebUrl}");
        }

        var date = string.Empty;
        if (task.Recurrence is { } recurrence)
        {
            var (text, caveat) = RecurrenceText.Describe(recurrence, task.DueDate, startingFrom: task.DueDate);
            if (caveat is not null)
            {
                losses.Add(Loss(list, task, LossKinds.RecurrenceApproximated,
                    $"Recurrence written as \"{(text.Length > 0 ? text : "(none)")}\": {caveat}"));
            }

            if (text.Length > 0)
            {
                date = text;
            }
        }

        if (date.Length == 0 && task.DueDate is { } due)
        {
            date = Fmt.Date(due);
        }

        if (task.Reminder is { } reminder)
        {
            losses.Add(Loss(list, task, LossKinds.ReminderNotCarried,
                $"Reminder at {Fmt.DateTime(reminder)} is not carried. Todoist reminders cannot be imported from CSV."));
        }

        if (task.StartDate is { } startDate)
        {
            losses.Add(Loss(list, task, LossKinds.StartDateNotCarried,
                $"Start date {Fmt.Date(startDate)} is not carried."));
        }

        if (task.HasAnyAttachment)
        {
            losses.Add(MarkdownExporter.AttachmentLoss(ExportFormatNames.Todoist, list, task) with { TaskIsOpen = !task.IsCompleted });
        }

        var priority = task.Importance == Importance.High ? 1 : 4;
        return new Row(content.ToString(), string.Join("\n", description), priority, 1, date, task.Title);
    }

    /// <summary>Packs blocks (a task and its sub-tasks) into files of at most 300 task rows without splitting a block.</summary>
    private static List<List<Row>> Split(List<List<Row>> blocks, TaskList list, ICollection<Loss> losses)
    {
        var parts = new List<List<Row>>();
        var current = new List<Row>();
        foreach (var original in blocks)
        {
            var block = original;
            if (block.Count > MaxTasksPerFile)
            {
                var dropped = block.Count - MaxTasksPerFile;
                losses.Add(new Loss(ExportFormatNames.Todoist, list.Name, block[0].TaskTitle, LossKinds.StepOverflow,
                    $"This task has more steps than one Todoist import allows. {dropped} steps were not written."));
                block = block.Take(MaxTasksPerFile).ToList();
            }

            if (current.Count > 0 && current.Count + block.Count > MaxTasksPerFile)
            {
                parts.Add(current);
                current = new List<Row>();
            }

            current.AddRange(block);
        }

        if (current.Count > 0)
        {
            parts.Add(current);
        }

        return parts;
    }

    private static string Render(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        foreach (var row in rows)
        {
            Csv.AppendRow(sb,
            [
                "task",
                row.Content,
                row.Description,
                row.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                row.Indent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Empty,
                string.Empty,
                row.Date,
                row.Date.Length > 0 ? "en" : string.Empty,
                string.Empty,
                string.Empty,
                "None",
            ]);
        }

        return sb.ToString();
    }

    private static Loss Loss(TaskList list, TodoItem task, string kind, string message) =>
        new(ExportFormatNames.Todoist, list.Name, task.Title, kind, message) { TaskIsOpen = !task.IsCompleted };
}

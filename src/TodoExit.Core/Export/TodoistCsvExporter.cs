using System.Text;
using TodoExit.Core.Model;

namespace TodoExit.Core.Export;

/// <summary>
/// Todoist project import CSV, one file per list under todoist/.
/// Format facts used here: UTF-8, fixed header, TYPE lowercase, PRIORITY 1 = p1 (highest) to 4 = p4,
/// an empty PRIORITY imports as p1 so it is always written, INDENT 1 = task and 2 = sub-task,
/// at most 300 tasks per imported project.
/// </summary>
public static class TodoistCsvExporter
{
    public const string Header = "TYPE,CONTENT,DESCRIPTION,PRIORITY,INDENT,AUTHOR,RESPONSIBLE,DATE,DATE_LANG,TIMEZONE,DURATION,DURATION_UNIT";
    public const int MaxTasksPerFile = 300;

    /// <summary>CONTENT must stay on one line: runs of CR, LF and tab become a single space.</summary>
    internal static string OneLine(string text) => TodoistMapping.OneLine(text);

    private sealed record Row(string Content, string Description, int Priority, int Indent, string Date, string TaskTitle);

    public static IReadOnlyList<ExportFile> Export(TodoArchive archive, bool includeCompleted, ICollection<Loss>? losses = null)
    {
        losses ??= new List<Loss>();
        var allParts = new List<List<List<Row>>>(archive.Lists.Count);

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
            allParts.Add(parts);
            if (parts.Count > 1)
            {
                // A Todoist project holds at most 300 tasks (403 MAX_ITEMS_LIMIT_REACHED otherwise), so each part
                // needs its own project. Informational: nothing is lost.
                losses.Add(new Loss(ExportFormatNames.Todoist, list.Name, null, LossKinds.ListSplit,
                    $"This list has more than 300 tasks to import and was split into {parts.Count} files. Import each part into its own Todoist project.",
                    parts.Count));
            }
        }

        // Names are made unique over the final file names, after splitting: a list named "Work - part 1" must not
        // overwrite the first part of a split list "Work".
        var stems = FileNames.UniqueStems(
            archive.Lists.Select(l => FileNames.Sanitize(l.Name)).ToList(),
            allParts.Select(p => p.Count).ToList(),
            PathOf);

        var files = new List<ExportFile>();
        for (var i = 0; i < archive.Lists.Count; i++)
        {
            var parts = allParts[i];
            for (var p = 0; p < parts.Count; p++)
            {
                files.Add(ExportFile.FromText(PathOf(stems[i], p + 1, parts.Count), Render(parts[p])));
            }
        }

        return files;
    }

    private static string PathOf(string stem, int part, int count) =>
        count == 1 ? $"todoist/{stem}.csv" : $"todoist/{stem} - part {part}.csv";

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
        var fields = TodoistMapping.Map(list, task, ExportFormatNames.Todoist, losses, withRecurrence: true,
            reminder => $"Reminder at {Fmt.DateTime(reminder)} is not carried. Todoist reminders cannot be imported from CSV.");
        var content = new StringBuilder(fields.Content);
        foreach (var label in fields.Labels)
        {
            content.Append(" @").Append(label);
        }

        var priority = fields.HighPriority ? 1 : 4;
        return new Row(content.ToString(), fields.Description, priority, 1, fields.Date, task.Title);
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
        TodoistMapping.Loss(ExportFormatNames.Todoist, list, task, kind, message);
}

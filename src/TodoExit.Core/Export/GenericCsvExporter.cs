using System.Text;
using TodoExit.Core.Model;

namespace TodoExit.Core.Export;

/// <summary>tasks.csv: one row per task across all lists, UTF-8 with BOM so Excel detects the encoding.</summary>
public static class GenericCsvExporter
{
    public const string FileName = "tasks.csv";

    public static readonly string[] Columns =
    [
        "List", "Title", "Status", "Importance", "Due", "Start", "Reminder", "Repeats", "Notes",
        "Steps", "Categories", "Links", "Attachments", "Created", "Completed",
    ];

    public static ExportFile Export(TodoArchive archive, ICollection<Loss>? losses = null)
    {
        var sb = new StringBuilder();
        Csv.AppendRow(sb, Columns);
        foreach (var list in archive.Lists)
        {
            foreach (var task in list.Tasks)
            {
                Csv.AppendRow(sb, Row(list, task));
                if (losses is not null && task.Attachments.Count > 0)
                {
                    losses.Add(MarkdownExporter.AttachmentLoss(ExportFormatNames.Csv, list, task));
                }
            }
        }

        return ExportFile.FromText(FileName, sb.ToString(), withBom: true);
    }

    private static string?[] Row(TaskList list, TodoItem task)
    {
        var repeats = task.Recurrence is { } r ? RecurrenceText.Describe(r, task.DueDate).Text : null;
        return
        [
            list.Name,
            task.Title,
            task.IsCompleted ? "completed" : "open",
            task.Importance.ToString().ToLowerInvariant(),
            task.DueDate is { } due ? Fmt.Date(due) : null,
            task.StartDate is { } start ? Fmt.Date(start) : null,
            task.Reminder is { } reminder ? Fmt.DateTime(reminder) : null,
            repeats,
            task.Notes,
            string.Join("; ", task.Steps.Select(s => (s.IsChecked ? "[x] " : "[ ] ") + s.Title)),
            string.Join("; ", task.Categories),
            string.Join("; ", task.LinkedResources.Select(FormatLink)),
            string.Join("; ", task.Attachments.Select(a => a.Name)),
            task.CreatedAt is { } created ? Fmt.DateTime(created) : null,
            task.CompletedAt is { } completed ? Fmt.DateTime(completed) : null,
        ];
    }

    private static string FormatLink(LinkedResource link)
    {
        var name = link.DisplayName ?? link.ApplicationName;
        if (string.IsNullOrWhiteSpace(link.WebUrl))
        {
            return name ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(name) ? link.WebUrl : $"{name} ({link.WebUrl})";
    }
}

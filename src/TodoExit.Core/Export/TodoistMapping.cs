using System.Text.RegularExpressions;
using TodoExit.Core.Model;

namespace TodoExit.Core.Export;

/// <summary>The Todoist fields of one To Do task, shared by the CSV exporter and the API transfer.</summary>
/// <param name="Content">One-line title, without labels.</param>
/// <param name="Description">Notes, then one "Link: ..." line per linked resource, joined with LF.</param>
/// <param name="Labels">Categories with whitespace runs replaced by '_' (CSV writes them as " @label").</param>
/// <param name="Date">A recurrence phrase, a yyyy-MM-dd date, or empty.</param>
/// <param name="DateIsRecurrence">True when <paramref name="Date"/> is a recurrence phrase.</param>
internal sealed record TodoistTaskFields(
    string Content,
    string Description,
    bool HighPriority,
    IReadOnlyList<string> Labels,
    string Date,
    bool DateIsRecurrence);

/// <summary>
/// Maps a To Do task to Todoist fields and records what cannot be carried. One place for both Todoist paths,
/// so the CSV and the API transfer cannot drift apart.
/// </summary>
internal static partial class TodoistMapping
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\r\n\t]+")]
    private static partial Regex LineBreaksAndTabs();

    /// <summary>Content must stay on one line: runs of CR, LF and tab become a single space.</summary>
    public static string OneLine(string text) => LineBreaksAndTabs().Replace(text, " ");

    public static string Label(string category) => Whitespace().Replace(category.Trim(), "_");

    /// <param name="withRecurrence">False writes only the plain due date (completed tasks in the API transfer).</param>
    /// <param name="reminderLoss">Message for a reminder that is not carried, or null when it is carried.</param>
    /// <remarks>Loss order (recurrence, reminder, start date, attachment) is part of the CSV report output.</remarks>
    public static TodoistTaskFields Map(
        TaskList list, TodoItem task, string format, ICollection<Loss> losses, bool withRecurrence, Func<DateTime, string?> reminderLoss)
    {
        var labels = task.Categories.Select(Label).ToList();

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
        var isRecurrence = false;
        if (withRecurrence && task.Recurrence is { } recurrence)
        {
            var (text, caveat) = RecurrenceText.Describe(recurrence, task.DueDate, startingFrom: task.DueDate);
            if (caveat is not null)
            {
                losses.Add(Loss(format, list, task, LossKinds.RecurrenceApproximated,
                    $"Recurrence written as \"{(text.Length > 0 ? text : "(none)")}\": {caveat}"));
            }

            if (text.Length > 0)
            {
                date = text;
                isRecurrence = true;
            }
        }

        if (date.Length == 0 && task.DueDate is { } due)
        {
            date = Fmt.Date(due);
        }

        if (task.Reminder is { } reminder && reminderLoss(reminder) is { } reminderMessage)
        {
            losses.Add(Loss(format, list, task, LossKinds.ReminderNotCarried, reminderMessage));
        }

        if (task.StartDate is { } startDate)
        {
            losses.Add(Loss(format, list, task, LossKinds.StartDateNotCarried,
                $"Start date {Fmt.Date(startDate)} is not carried."));
        }

        if (task.HasAnyAttachment)
        {
            losses.Add(MarkdownExporter.AttachmentLoss(format, list, task) with { TaskIsOpen = !task.IsCompleted });
        }

        return new TodoistTaskFields(
            OneLine(task.Title), string.Join("\n", description), task.Importance == Importance.High, labels, date, isRecurrence);
    }

    public static Loss Loss(string format, TaskList list, TodoItem task, string kind, string message) =>
        new(format, list.Name, task.Title, kind, message) { TaskIsOpen = !task.IsCompleted };
}

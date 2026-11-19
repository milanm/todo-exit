using System.Text;
using TodoExit.Core.Model;

namespace TodoExit.Core.Export;

/// <summary>One Markdown file per list under markdown/.</summary>
public static class MarkdownExporter
{
    public static IReadOnlyList<ExportFile> Export(TodoArchive archive, ICollection<Loss>? losses = null)
    {
        var names = FileNames.SanitizeUnique(archive.Lists.Select(l => l.Name));
        var files = new List<ExportFile>(archive.Lists.Count);
        for (var i = 0; i < archive.Lists.Count; i++)
        {
            var list = archive.Lists[i];
            files.Add(ExportFile.FromText($"markdown/{names[i]}.md", Render(list)));

            if (losses is not null)
            {
                foreach (var task in list.Tasks.Where(t => t.Attachments.Count > 0))
                {
                    losses.Add(AttachmentLoss(ExportFormatNames.Markdown, list, task));
                }
            }
        }

        return files;
    }

    internal static Loss AttachmentLoss(string format, TaskList list, TodoItem task) => new(
        format,
        list.Name,
        task.Title,
        LossKinds.AttachmentNotCarried,
        "Attachment content is not exported: " + string.Join(", ", task.Attachments.Select(a => a.Name)));

    public static string Render(TaskList list)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(OneLine(list.Name)).Append('\n');

        var open = list.Tasks.Where(t => !t.IsCompleted).ToList();
        var completed = list.Tasks.Where(t => t.IsCompleted).ToList();

        if (open.Count > 0)
        {
            sb.Append('\n');
            foreach (var task in open)
            {
                sb.Append("- [ ] ").Append(OneLine(task.Title));
                var meta = new List<string>();
                if (task.DueDate is { } due)
                {
                    meta.Add("due " + Fmt.Date(due));
                }

                if (task.Recurrence is { } r)
                {
                    var (text, _) = RecurrenceText.Describe(r, task.DueDate);
                    if (text.Length > 0)
                    {
                        meta.Add("repeats " + text);
                    }
                }

                if (task.Reminder is { } reminder)
                {
                    meta.Add("reminder " + Fmt.DateTime(reminder));
                }

                if (task.Importance == Importance.High)
                {
                    meta.Add("high importance");
                }

                if (meta.Count > 0)
                {
                    sb.Append(" (").Append(string.Join(", ", meta)).Append(')');
                }

                AppendTags(sb, task);
                sb.Append('\n');
                AppendDetails(sb, task);
            }
        }

        if (completed.Count > 0)
        {
            sb.Append("\n## Completed\n\n");
            foreach (var task in completed)
            {
                sb.Append("- [x] ").Append(OneLine(task.Title));
                if (task.CompletedAt is { } at)
                {
                    sb.Append(" (completed ").Append(Fmt.Date(at)).Append(')');
                }

                AppendTags(sb, task);
                sb.Append('\n');
                AppendDetails(sb, task);
            }
        }

        return sb.ToString();
    }

    private static void AppendTags(StringBuilder sb, TodoItem task)
    {
        foreach (var category in task.Categories)
        {
            sb.Append(" #").Append(string.Join('-', category.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }
    }

    private static void AppendDetails(StringBuilder sb, TodoItem task)
    {
        const string indent = "    ";
        foreach (var step in task.Steps)
        {
            sb.Append(indent).Append(step.IsChecked ? "- [x] " : "- [ ] ").Append(OneLine(step.Title)).Append('\n');
        }

        if (task.Notes is { } notes)
        {
            foreach (var line in notes.Split('\n'))
            {
                sb.Append(indent).Append('>');
                if (line.Length > 0)
                {
                    sb.Append(' ').Append(line);
                }

                sb.Append('\n');
            }
        }

        foreach (var link in task.LinkedResources)
        {
            var name = link.DisplayName ?? link.ApplicationName ?? link.WebUrl ?? "link";
            if (string.IsNullOrWhiteSpace(link.WebUrl))
            {
                sb.Append(indent).Append("- link: ").Append(OneLine(name)).Append('\n');
            }
            else
            {
                sb.Append(indent).Append("- link: [").Append(EscapeLinkText(OneLine(name))).Append("](")
                    .Append(link.WebUrl.Replace(" ", "%20").Replace(")", "%29")).Append(")\n");
            }
        }

        foreach (var attachment in task.Attachments)
        {
            sb.Append(indent).Append("- attachment: ").Append(OneLine(attachment.Name))
                .Append(" (").Append(Fmt.Size(attachment.Size)).Append(")\n");
        }
    }

    private static string OneLine(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    private static string EscapeLinkText(string text) => text.Replace("[", "\\[").Replace("]", "\\]");
}

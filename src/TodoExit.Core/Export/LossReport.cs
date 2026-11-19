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

    public const string JsonFileName = "report.json";
    public const string MarkdownFileName = "report.md";

    private static readonly string[] FormatOrder =
        [ExportFormatNames.Todoist, ExportFormatNames.Markdown, ExportFormatNames.Csv, ExportFormatNames.Json];

    public byte[] ToJsonBytes()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            var c = Inventory.Totals;
            w.WriteStartObject();
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
            w.WriteBoolean("stepsForCompletedTasksSkipped", ReadNotes.StepsForCompletedTasksSkipped);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# todo-exit report\n\n");
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

            sb.Append("| Kind | Count |\n|---|---:|\n");
            foreach (var group in items.GroupBy(l => l.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                sb.Append("| ").Append(group.Key).Append(" | ").Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append(" |\n");
            }

            sb.Append('\n');
            foreach (var byList in items.GroupBy(l => l.List))
            {
                sb.Append("#### ").Append(byList.Key).Append("\n\n");
                foreach (var loss in byList)
                {
                    sb.Append("- ");
                    if (loss.Task is not null)
                    {
                        sb.Append("**").Append(EscapeInline(loss.Task)).Append("**: ");
                    }

                    sb.Append(EscapeInline(loss.Message)).Append(" (`").Append(loss.Kind).Append("`)\n");
                }

                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private void AppendReadErrors(StringBuilder sb)
    {
        if (ReadNotes.IsEmpty)
        {
            return;
        }

        sb.Append("\n## Could not be read from Microsoft Graph\n\n");
        sb.Append("This applies to every format, the JSON archive included.\n\n");
        if (ReadNotes.StepsForCompletedTasksSkipped)
        {
            sb.Append("Steps of completed tasks were not read, by choice (--skip-completed-steps). Completed tasks have no steps in this export.\n\n");
        }

        if (ReadNotes.Errors.Count == 0)
        {
            return;
        }

        sb.Append("| Kind | Count |\n|---|---:|\n");
        foreach (var group in ReadNotes.Errors.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("| ").Append(group.Key).Append(" | ").Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append(" |\n");
        }

        sb.Append('\n');
        foreach (var byList in ReadNotes.Errors.GroupBy(e => e.ListName ?? e.ListId ?? "(unknown list)"))
        {
            sb.Append("#### ").Append(byList.Key).Append("\n\n");
            foreach (var e in byList)
            {
                sb.Append("- ");
                if (e.TaskTitle is not null)
                {
                    sb.Append("**").Append(EscapeInline(e.TaskTitle)).Append("**: ");
                }
                else if (e.Position is { } position)
                {
                    sb.Append("**Task at position ").Append(position.ToString(CultureInfo.InvariantCulture)).Append("**: ");
                }

                sb.Append(EscapeInline(e.Message)).Append(" (`").Append(e.Kind).Append("`)\n");
            }

            sb.Append('\n');
        }
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
        _ => false,
    };

    private static string Title(string format) => format switch
    {
        ExportFormatNames.Todoist => "Todoist CSV",
        ExportFormatNames.Markdown => "Markdown",
        ExportFormatNames.Csv => "Generic CSV (tasks.csv)",
        _ => format,
    };

    private static string EscapeInline(string text) =>
        text.Replace("\r\n", " ").Replace('\n', ' ').Replace("*", "\\*");
}

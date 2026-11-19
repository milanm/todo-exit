using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TodoExit.Core.Export;
using TodoExit.Core.Model;

namespace TodoExit.Core.Todoist;

/// <summary>
/// todoist-transfer-report.md and .json: a short transfer summary followed by the usual loss report under the
/// "todoist-api" format, so the user gets the same kind of report as for the CSV.
/// </summary>
public static class TodoistTransferReport
{
    public const string MarkdownFileName = "todoist-transfer-report.md";
    public const string JsonFileName = "todoist-transfer-report.json";

    /// <summary>Plan losses plus what happened during the transfer, aggregated like the export report.</summary>
    public static LossReport Losses(TodoArchive archive, TodoistPlan plan, TodoistTransferState? state)
    {
        var losses = new List<Loss>(plan.Losses);
        if (state is not null)
        {
            losses.AddRange(state.Errors.Select(e =>
                new Loss(ExportFormatNames.TodoistApi, e.List, e.Task, e.LossKind, e.Message)));
        }

        return new LossReport(Inventory.From(archive), LossCatalog.Aggregate(losses), ExportFormats.TodoistApi) { ReadNotes = archive.ReadNotes };
    }

    public static IReadOnlyList<ExportFile> ToFiles(
        TodoArchive archive, TodoistPlan plan, TodoistTransferState state, TodoistTransferResult result, TimeSpan elapsed)
    {
        var report = Losses(archive, plan, state);
        var counts = TransferCounts.From(plan, state);
        var markdown = report.ToMarkdown("todo-exit transfer to Todoist");
        var cut = markdown.IndexOf("\n\n", StringComparison.Ordinal) + 2;
        markdown = markdown[..cut] + Summary(plan, state, result, counts, elapsed) + markdown[cut..];

        var transfer = new JsonObject
        {
            ["status"] = result.Status.ToString().ToLowerInvariant(),
            ["message"] = result.Message,
            ["transferId"] = state.TransferId,
            ["account"] = plan.Account?.Email,
            ["elapsedSeconds"] = Math.Round(elapsed.TotalSeconds, 1),
            ["projectsCreated"] = counts.Projects,
            ["tasks"] = counts.Tasks,
            ["subTasks"] = counts.SubTasks,
            ["completed"] = counts.Completed,
            ["reminders"] = counts.Reminders,
            ["errors"] = counts.Errors,
            ["skipped"] = counts.Skipped,
            ["projects"] = new JsonArray(plan.Projects.Select(p => (JsonNode)new JsonObject
            {
                ["list"] = p.ListName,
                ["name"] = p.Name,
                ["id"] = state.Ids.TryGetValue(p.Key, out var id) && !state.Failed.ContainsKey(p.Key) ? id : null,
                ["tasks"] = p.OpenTasks + p.CompletedTasks,
                ["subTasks"] = p.OpenSubTasks + p.CompletedSubTasks,
            }).ToArray()),
        };

        var root = new JsonObject { ["transfer"] = transfer };
        var losses = (JsonObject)JsonNode.Parse(report.ToJsonBytes())!;
        foreach (var (name, value) in losses.ToList())
        {
            losses.Remove(name);
            root[name] = value;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            root.WriteTo(writer);
        }

        return [ExportFile.FromText(MarkdownFileName, markdown), new ExportFile(JsonFileName, stream.ToArray())];
    }

    private static string Summary(TodoistPlan plan, TodoistTransferState state, TodoistTransferResult result, TransferCounts c, TimeSpan elapsed)
    {
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append(result.Status switch
        {
            TransferStatus.Completed => "The transfer finished.",
            TransferStatus.Interrupted => "**The transfer was interrupted.** Run it again with --resume to finish it.",
            _ => "**The transfer stopped.**",
        });
        if (result.Message is { } message)
        {
            sb.Append(' ').Append(message);
        }

        sb.Append("\n\n");
        if (plan.Account?.Email is { } email)
        {
            sb.Append("Todoist account: ").Append(email).Append("\n\n");
        }

        sb.Append("| Item | Count |\n|---|---:|\n");
        sb.Append("| Projects created | ").Append(N(c.Projects)).Append(" |\n");
        sb.Append("| Tasks | ").Append(N(c.Tasks)).Append(" |\n");
        sb.Append("| Sub-tasks | ").Append(N(c.SubTasks)).Append(" |\n");
        sb.Append("| Completed in Todoist | ").Append(N(c.Completed)).Append(" |\n");
        sb.Append("| Reminders | ").Append(N(c.Reminders)).Append(" |\n");
        sb.Append("| Errors from Todoist | ").Append(N(c.Errors)).Append(" |\n");
        sb.Append("| Skipped (parent not created) | ").Append(N(c.Skipped)).Append(" |\n");
        sb.Append("| Time | ").Append(elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s |\n\n");

        sb.Append("## Projects\n\n| To Do list | Todoist project | Created |\n|---|---|---|\n");
        foreach (var p in plan.Projects)
        {
            var created = state.Ids.ContainsKey(p.Key) && !state.Failed.ContainsKey(p.Key);
            sb.Append("| ").Append(p.ListName.Replace("|", "\\|")).Append(" | ").Append(p.Name.Replace("|", "\\|"))
                .Append(" | ").Append(created ? "yes" : "no").Append(" |\n");
        }

        sb.Append('\n');
        return sb.ToString();
    }
}

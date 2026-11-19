using System.Text.Json.Nodes;
using TodoExit.Core.Archive;
using TodoExit.Core.Export;
using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

public class LossAggregationTests
{
    private const string Reminder = """ "isReminderOn": true, "reminderDateTime": { "dateTime": "2026-11-25T08:00:00", "timeZone": "UTC" } """;
    private const string Attachment = """ "hasAttachments": true, "attachments": [ { "name": "f.pdf", "size": 1 } ] """;

    /// <summary>
    /// "Work": 5 completed tasks (3 with reminders, 2 with attachments, one with 2 checked steps),
    /// 2 open tasks with reminder and attachment, 1 open task with a start date and a checked step, 1 open recurring task
    /// whose rule is approximated. "Shared": a shared list with one open task.
    /// </summary>
    private static (TodoArchiveDocument Doc, TodoArchive Archive) Account(int extraOpenWithReminders = 0)
    {
        var tasks = new List<JsonObject>();
        for (var i = 0; i < 5; i++)
        {
            var extra = new List<string> { "\"status\": \"completed\"" };
            if (i < 3)
            {
                extra.Add(Reminder);
            }

            if (i is 3 or 4)
            {
                extra.Add(Attachment);
            }

            if (i == 0)
            {
                extra.Add("""  "checklistItems": [ { "displayName": "a", "isChecked": true }, { "displayName": "b", "isChecked": true } ] """);
            }

            tasks.Add(TestData.Task($"Done {i}", "{" + string.Join(",", extra) + "}"));
        }

        tasks.Add(TestData.Task("Open A", "{" + Reminder + "," + Attachment + "}"));
        tasks.Add(TestData.Task("Open B", "{" + Reminder + "," + Attachment + "}"));
        tasks.Add(TestData.Task("Open C", """
            { "startDateTime": { "dateTime": "2026-11-20T00:00:00", "timeZone": "UTC" },
              "checklistItems": [ { "displayName": "x", "isChecked": true }, { "displayName": "y", "isChecked": false } ] }
            """));
        tasks.Add(TestData.Task("Repeats", """{ "recurrence": { "pattern": { "type": "relativeYearly", "interval": 1, "month": 3, "daysOfWeek": ["friday"], "index": "last" } } }"""));
        for (var i = 0; i < extraOpenWithReminders; i++)
        {
            tasks.Add(TestData.Task($"Remind {i}", "{" + Reminder + "}"));
        }

        var doc = TestData.Document(
            (TestData.List("Work"), tasks.ToArray()),
            (TestData.List("Shared", shared: true), [TestData.Task("Milk")]));
        return (doc, ArchiveParser.Parse(doc, TimeZoneInfo.Utc));
    }

    private static List<Loss> Losses(ExportResult result, string format, string kind) =>
        result.Report.Losses.Where(l => l.Format == format && l.Kind == kind).ToList();

    [Fact]
    public void Bulk_kinds_are_aggregated_with_counts_and_no_task_names()
    {
        var (doc, archive) = Account();
        var result = ExportService.Export(doc, archive, new ExportOptions());

        var completed = Assert.Single(Losses(result, "todoist", LossKinds.CompletedSkipped));
        Assert.Equal((5, null, true), (completed.Count, completed.Task, completed.Aggregated));
        Assert.Equal("5 completed tasks were left out.", completed.Message);

        var checkedSteps = Assert.Single(Losses(result, "todoist", LossKinds.CheckedStepSkipped));
        Assert.Equal((1, null), (checkedSteps.Count, checkedSteps.Task)); // only open tasks are written; Open C has one

        var start = Assert.Single(Losses(result, "todoist", LossKinds.StartDateNotCarried));
        Assert.Equal((1, null, true), (start.Count, start.Task, start.Aggregated));

        foreach (var format in new[] { "markdown", "csv" })
        {
            var attachments = Assert.Single(Losses(result, format, LossKinds.AttachmentNotCarried));
            Assert.Equal((4, null), (attachments.Count, attachments.Task)); // 2 completed + 2 open
        }

        // Kinds that need the task name stay per task.
        Assert.Equal("Repeats", Assert.Single(Losses(result, "todoist", LossKinds.RecurrenceApproximated)).Task);
        var shared = Assert.Single(Losses(result, "todoist", LossKinds.SharedListMembersNotCarried));
        Assert.Null(shared.Task);
        Assert.False(shared.Aggregated);
    }

    [Fact]
    public void Todoist_reminders_and_attachments_stay_per_task_for_open_tasks_only()
    {
        var (doc, archive) = Account();
        var result = ExportService.Export(doc, archive, new ExportOptions());

        Assert.Equal(["Open A", "Open B"], Losses(result, "todoist", LossKinds.ReminderNotCarried).Select(l => l.Task));
        Assert.Equal(["Open A", "Open B"], Losses(result, "todoist", LossKinds.AttachmentNotCarried).Select(l => l.Task));
        Assert.All(Losses(result, "todoist", LossKinds.ReminderNotCarried), l => Assert.Equal(1, l.Count));

        // With completed tasks written as open rows, their reminders are counted, not listed.
        var included = ExportService.Export(doc, archive, new ExportOptions(IncludeCompleted: true));
        var reminders = Losses(included, "todoist", LossKinds.ReminderNotCarried);
        Assert.Equal(["Open A", "Open B"], reminders.Where(l => !l.Aggregated).Select(l => l.Task));
        var aggregated = Assert.Single(reminders, l => l.Aggregated);
        Assert.Equal((3, null), (aggregated.Count, aggregated.Task));
        var attachments = Losses(included, "todoist", LossKinds.AttachmentNotCarried);
        Assert.Equal(2, Assert.Single(attachments, l => l.Aggregated).Count);
    }

    [Fact]
    public void Report_json_has_counts_and_null_task_for_aggregated_entries()
    {
        var (doc, archive) = Account();
        var result = ExportService.Export(doc, archive, new ExportOptions());
        var json = JsonNode.Parse(result.Files.Single(f => f.Path == "report.json").GetText())!.AsObject();

        var losses = json["losses"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.All(losses, l => Assert.True(l.ContainsKey("count")));
        var completed = losses.Single(l => (string)l["kind"]! == "completed-skipped");
        Assert.Equal(5, (int)completed["count"]!);
        Assert.Null(completed["task"]);
        Assert.True(completed.ContainsKey("task"));
        Assert.Equal(1, (int)losses.First(l => (string)l["kind"]! == "reminder-not-carried")["count"]!);
    }

    [Fact]
    public void Report_md_has_kind_table_with_meanings_and_lists_only_per_task_items()
    {
        var (doc, archive) = Account();
        var md = ExportService.Export(doc, archive, new ExportOptions()).Report.ToMarkdown();

        Assert.Contains("| Kind | Count | What it means |", md);
        Assert.Contains("| completed-skipped | 5 | Completed tasks are left out on purpose", md);
        Assert.Contains("**Open A**: Reminder at", md);
        Assert.DoesNotContain("Done 0", md);
        Assert.DoesNotContain("5 completed tasks were left out.", md);
        Assert.True(md.IndexOf("## Inventory", StringComparison.Ordinal) < md.IndexOf("## What was not carried over", StringComparison.Ordinal));
    }

    [Fact]
    public void No_section_lists_more_than_200_items()
    {
        var (doc, archive) = Account(extraOpenWithReminders: 250);
        var result = ExportService.Export(doc, archive, new ExportOptions(ExportFormats.Todoist));
        var md = result.Report.ToMarkdown();

        var todoistSection = md[md.IndexOf("### Todoist CSV", StringComparison.Ordinal)..];
        Assert.Equal(LossReport.MaxItemsPerSection, todoistSection.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
        var total = result.Report.Losses.Count(l => !l.Aggregated);
        Assert.Contains($"... and {total - 200} more, see report.json.", md);
        Assert.Equal(252, Losses(result, "todoist", LossKinds.ReminderNotCarried).Count);

        var json = JsonNode.Parse(result.Files.Single(f => f.Path == "report.json").GetText())!;
        Assert.Equal(result.Report.Losses.Count, json["losses"]!.AsArray().Count);
    }

    [Fact]
    public void Read_errors_section_is_capped_too()
    {
        var errors = Enumerable.Range(0, 230)
            .Select(i => new ReadError(ReadErrorKinds.TaskPartial, "L", "Work", $"t{i}", $"T{i}", i, "partial"))
            .ToList();
        var doc = TodoArchiveDocument.Create(DateTimeOffset.UnixEpoch, [new ArchivedList(TestData.List("Work"), [])], new ReadNotes(errors));
        var md = ExportService.Export(doc, ArchiveParser.Parse(doc, TimeZoneInfo.Utc), new ExportOptions(ExportFormats.Json)).Report.ToMarkdown();

        Assert.Contains("| task-partial | 230 |", md);
        Assert.Equal(200, md.Split('\n').Count(l => l.StartsWith("- **T", StringComparison.Ordinal)));
        Assert.Contains("... and 30 more, see report.json.", md);
    }

    [Fact]
    public void Summary_lines_are_short_and_plain()
    {
        var (doc, archive) = Account();
        var result = ExportService.Export(doc, archive, new ExportOptions());
        Assert.Equal(
            ["Todoist CSV: 5 completed tasks left out by design, 2 reminders and 2 attachments to re-add by hand, 1 repeat rule to check."],
            result.Report.SummaryLines());

        var notes = new ReadNotes(
        [
            new ReadError(ReadErrorKinds.TaskPartial, "L", "Work", "t1", "A", 1, "x"),
            new ReadError(ReadErrorKinds.TaskPartial, "L", "Work", "t2", "B", 2, "x"),
            new ReadError(ReadErrorKinds.AttachmentsDenied, null, null, null, null, null, "denied"),
        ])
        { AttachmentsDenied = true };
        var report = new LossReport(Inventory.From(archive), [], ExportFormats.Json) { ReadNotes = notes };
        Assert.Equal(["2 tasks were only partly readable and attachment names were not available. Details in report.md."], report.SummaryLines());

        var clean = new LossReport(Inventory.From(archive), [], ExportFormats.All);
        Assert.Empty(clean.SummaryLines());
    }

    [Fact]
    public void Old_archive_with_steps_skipped_note_loads_and_keeps_the_property()
    {
        const string json = """
            {
              "format": "todo-exit-archive",
              "version": 1,
              "exportedAt": "2026-11-19T13:30:00Z",
              "readNotes": { "stepsForCompletedTasksSkipped": true, "errors": [] },
              "lists": [ { "list": { "id": "L", "displayName": "Work" }, "tasks": [ { "id": "t", "title": "A", "status": "completed" } ] } ]
            }
            """;
        var doc = TodoArchiveDocument.Parse(json);

        Assert.True(doc.ReadNotes.IsEmpty);
        Assert.Contains("\"stepsForCompletedTasksSkipped\": true", doc.ToJsonString());
        var result = ExportService.Export(doc, ArchiveParser.Parse(doc, TimeZoneInfo.Utc), new ExportOptions());
        Assert.DoesNotContain("stepsForCompletedTasksSkipped", result.Files.Single(f => f.Path == "report.json").GetText());
    }
}

public class ListSplitTests
{
    private static (TodoArchiveDocument, TodoArchive) Lists(params (string Name, int Tasks)[] lists)
    {
        var doc = TestData.Document(lists
            .Select(l => (TestData.List(l.Name), Enumerable.Range(0, l.Tasks).Select(i => TestData.Task($"{l.Name} {i}")).ToArray()))
            .ToArray());
        return (doc, ArchiveParser.Parse(doc, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Split_list_gets_one_list_split_entry_with_the_number_of_parts()
    {
        var (doc, archive) = Lists(("Big", 650), ("Small", 300));
        var result = ExportService.Export(doc, archive, new ExportOptions(ExportFormats.Todoist));

        Assert.Equal(3, result.Files.Count(f => f.Path.StartsWith("todoist/Big - part ", StringComparison.Ordinal)));
        var split = Assert.Single(result.Report.Losses, l => l.Kind == LossKinds.ListSplit);
        Assert.Equal(("todoist", "Big", null, 3, false), (split.Format, split.List, split.Task, split.Count, split.Aggregated));
        Assert.Contains("Import each part into its own Todoist project.", split.Message);

        var md = result.Report.ToMarkdown();
        Assert.Contains("| list-split | 3 | This list has more than 300 tasks to import. Import each part into its own Todoist project. |", md);
        var json = JsonNode.Parse(result.Files.Single(f => f.Path == "report.json").GetText())!;
        var entry = json["losses"]!.AsArray().Single(l => (string)l!["kind"]! == "list-split")!;
        Assert.Equal(3, (int)entry["count"]!);
        Assert.Null(entry["task"]);
    }

    [Fact]
    public void List_with_300_rows_or_fewer_gets_no_list_split_entry()
    {
        var (doc, archive) = Lists(("Exactly", 300), ("Few", 3));
        var result = ExportService.Export(doc, archive, new ExportOptions());
        Assert.DoesNotContain(result.Report.Losses, l => l.Kind == LossKinds.ListSplit);
        Assert.DoesNotContain(result.Report.SummaryLines(), l => l.Contains("split", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_mentions_split_lists_and_stays_within_three_lines()
    {
        var (doc, archive) = Lists(("Big", 650));
        var report = ExportService.Export(doc, archive, new ExportOptions(ExportFormats.Todoist)).Report;
        Assert.Equal(["1 list was split into parts; import each part into its own Todoist project."], report.SummaryLines());

        var (doc2, archive2) = Lists(("A", 301), ("B", 400));
        var report2 = ExportService.Export(doc2, archive2, new ExportOptions(ExportFormats.Todoist)).Report;
        Assert.Equal(["2 lists were split into parts; import each part into its own Todoist project."], report2.SummaryLines());

        // With a Todoist line, read problems and an incomplete archive, the hint is merged into the Todoist line.
        var task = TestData.Task("Done", """{ "status": "completed" }""");
        var doc3 = TestData.Document((TestData.List("Big"), Enumerable.Range(0, 301).Select(i => TestData.Task($"T{i}")).Append(task).ToArray()));
        var notes = new ReadNotes([new ReadError(ReadErrorKinds.TaskPartial, "list-Big", "Big", "x", "X", 1, "partial")])
        {
            Interrupted = true,
            InterruptedReason = "token expired",
        };
        var archive3 = ArchiveParser.Parse(doc3, TimeZoneInfo.Utc);
        var result3 = ExportService.Export(doc3, archive3 with { ReadNotes = notes }, new ExportOptions(ExportFormats.Todoist));
        var lines = result3.Report.SummaryLines();
        Assert.Equal(3, lines.Count);
        Assert.Equal("Todoist CSV: 1 completed task left out by design. 1 list was split into parts; import each part into its own Todoist project.", lines[0]);
        Assert.StartsWith("1 task was only partly readable", lines[1]);
        Assert.StartsWith("The archive is incomplete", lines[2]);
    }
}

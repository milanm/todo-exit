using System.Text.Json.Nodes;
using TodoExit.Core.Export;

namespace TodoExit.Core.Tests;

public class TodoistCsvTests
{
    private const string Header = "TYPE,CONTENT,DESCRIPTION,PRIORITY,INDENT,AUTHOR,RESPONSIBLE,DATE,DATE_LANG,TIMEZONE,DURATION,DURATION_UNIT";

    private static string[] Lines(ExportFile file) => file.GetText().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Writes_header_and_always_writes_priority()
    {
        var archive = TestData.Archive((TestData.List("Inbox"), [
            TestData.Task("Normal task"),
            TestData.Task("Urgent", """{ "importance": "high" }"""),
            TestData.Task("Someday", """{ "importance": "low" }"""),
        ]));

        var files = TodoistCsvExporter.Export(archive, includeCompleted: false);
        var file = Assert.Single(files);
        Assert.Equal("todoist/Inbox.csv", file.Path);
        Assert.False(file.Content.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));

        var lines = Lines(file);
        Assert.Equal(Header, lines[0]);
        Assert.Equal("task,Normal task,,4,1,,,,,,,None", lines[1]);
        Assert.Equal("task,Urgent,,1,1,,,,,,,None", lines[2]);
        Assert.Equal("task,Someday,,4,1,,,,,,,None", lines[3]);
    }

    [Fact]
    public void Due_date_and_recurrence_go_into_date_column()
    {
        var archive = TestData.Archive((TestData.List("L"), [
            TestData.Task("Due", """{ "dueDateTime": { "dateTime": "2026-11-24T23:00:00.0000000", "timeZone": "UTC" } }"""),
            TestData.Task("Weekly", """
                { "dueDateTime": { "dateTime": "2026-11-22T23:00:00.0000000", "timeZone": "UTC" },
                  "recurrence": { "pattern": { "type": "weekly", "interval": 1, "daysOfWeek": ["monday", "thursday"] }, "range": { "type": "noEnd" } } }
                """),
            TestData.Task("Daily no due", """{ "recurrence": { "pattern": { "type": "daily", "interval": 1 } } }"""),
        ]));

        var lines = Lines(TodoistCsvExporter.Export(archive, false)[0]);
        Assert.Equal("task,Due,,4,1,,,2026-11-25,en,,,None", lines[1]);
        Assert.Equal("task,Weekly,,4,1,,,\"every mon, thu starting 2026-11-23\",en,,,None", lines[2]);
        Assert.Equal("task,Daily no due,,4,1,,,every day,en,,,None", lines[3]);
    }

    [Fact]
    public void Steps_become_indent_2_rows_and_checked_steps_are_skipped_by_default()
    {
        var archive = TestData.Archive((TestData.List("L"), [
            TestData.Task("Parent", """
                { "importance": "high", "checklistItems": [
                    { "displayName": "done step", "isChecked": true },
                    { "displayName": "open step", "isChecked": false } ] }
                """),
        ]));

        var losses = new List<Loss>();
        var lines = Lines(TodoistCsvExporter.Export(archive, false, losses)[0]);
        Assert.Equal(3, lines.Length);
        Assert.Equal("task,Parent,,1,1,,,,,,,None", lines[1]);
        Assert.Equal("task,open step,,4,2,,,,,,,None", lines[2]);
        Assert.Contains(losses, l => l.Kind == LossKinds.CheckedStepSkipped && l.Task == "Parent");
    }

    [Fact]
    public void Completed_tasks_are_skipped_by_default_and_included_with_flag()
    {
        var archive = TestData.Archive((TestData.List("L"), [
            TestData.Task("Open"),
            TestData.Task("Done", """{ "status": "completed", "checklistItems": [ { "displayName": "s", "isChecked": true } ] }"""),
        ]));

        var losses = new List<Loss>();
        var skipped = Lines(TodoistCsvExporter.Export(archive, false, losses)[0]);
        Assert.Equal(2, skipped.Length);
        Assert.Single(losses, l => l.Kind == LossKinds.CompletedSkipped && l.Task == "Done");

        var included = Lines(TodoistCsvExporter.Export(archive, true)[0]);
        Assert.Equal(4, included.Length);
        Assert.Equal("task,Done,,4,1,,,,,,,None", included[2]);
        Assert.Equal("task,s,,4,2,,,,,,,None", included[3]);
    }

    [Fact]
    public void Categories_become_labels_with_underscores()
    {
        var archive = TestData.Archive((TestData.List("L"), [
            TestData.Task("Tagged", """{ "categories": ["Q4 review", "Work"] }"""),
        ]));

        var lines = Lines(TodoistCsvExporter.Export(archive, false)[0]);
        Assert.StartsWith("task,Tagged @Q4_review @Work,", lines[1]);
    }

    [Fact]
    public void Fields_with_commas_quotes_and_newlines_are_quoted()
    {
        var archive = TestData.Archive((TestData.List("L"), [
            TestData.Task("Buy milk, eggs and \"good\" bread", """
                { "body": { "content": "line one\nline two", "contentType": "text" },
                  "linkedResources": [ { "displayName": "Mail", "webUrl": "https://example.com/m" } ] }
                """),
        ]));

        var text = TodoistCsvExporter.Export(archive, false)[0].GetText();
        Assert.Contains("task,\"Buy milk, eggs and \"\"good\"\" bread\",\"line one\nline two\nLink: Mail https://example.com/m\",4,1,", text);
    }

    [Fact]
    public void Line_breaks_and_tabs_in_content_collapse_to_one_space_but_description_keeps_them()
    {
        var task = TestData.Task("first\r\nsecond\n\n\tthird");
        task["body"] = new JsonObject { ["content"] = "note 1\nnote 2", ["contentType"] = "text" };
        task["checklistItems"] = new JsonArray((JsonNode)new JsonObject { ["displayName"] = "step\r\none", ["isChecked"] = false });

        var lines = Lines(TodoistCsvExporter.Export(TestData.Archive((TestData.List("L"), [task])), false)[0]);

        Assert.Equal(3, lines.Length);
        Assert.Equal("task,first second third,\"note 1\nnote 2\",4,1,,,,,,,None", lines[1]);
        Assert.Equal("task,step one,,4,2,,,,,,,None", lines[2]);
    }

    [Fact]
    public void End_dated_recurrence_puts_starting_before_ending()
    {
        var archive = TestData.Archive((TestData.List("L"), [
            TestData.Task("Daily", """
                { "dueDateTime": { "dateTime": "2026-11-24T23:00:00.0000000", "timeZone": "UTC" },
                  "recurrence": { "pattern": { "type": "daily", "interval": 1 }, "range": { "type": "endDate", "startDate": "2026-11-25", "endDate": "2027-06-30" } } }
                """),
        ]));

        var lines = Lines(TodoistCsvExporter.Export(archive, false)[0]);
        Assert.Equal("task,Daily,,4,1,,,every day starting 2026-11-25 ending 2027-06-30,en,,,None", lines[1]);

        var md = MarkdownExporter.Export(archive)[0].GetText();
        Assert.Contains("repeats every day ending 2027-06-30)", md);
        Assert.DoesNotContain("starting", md);
        Assert.Contains(",every day ending 2027-06-30,", GenericCsvExporter.Export(archive).GetText());
    }

    [Fact]
    public void List_with_no_rows_produces_no_file()
    {
        var archive = TestData.Archive(
            (TestData.List("Empty"), []),
            (TestData.List("Only done"), [TestData.Task("x", """{ "status": "completed" }""")]),
            (TestData.List("Has one"), [TestData.Task("y")]));

        var files = TodoistCsvExporter.Export(archive, false);
        Assert.Equal(["todoist/Has one.csv"], files.Select(f => f.Path));
    }

    [Fact]
    public void Splits_at_300_rows_without_separating_parent_from_subtasks()
    {
        var tasks = new List<JsonObject>();
        for (var i = 0; i < 298; i++)
        {
            tasks.Add(TestData.Task($"Task {i}"));
        }

        // Rows 299..302: this parent and its three steps must move to part 2 together.
        tasks.Add(TestData.Task("Parent", """{ "checklistItems": [ { "displayName": "a" }, { "displayName": "b" }, { "displayName": "c" } ] }"""));
        tasks.Add(TestData.Task("After"));

        var archive = TestData.Archive((TestData.List("Big"), tasks.ToArray()));
        var files = TodoistCsvExporter.Export(archive, false);

        Assert.Equal(["todoist/Big - part 1.csv", "todoist/Big - part 2.csv"], files.Select(f => f.Path));
        var part1 = Lines(files[0]);
        var part2 = Lines(files[1]);
        Assert.Equal(Header, part1[0]);
        Assert.Equal(Header, part2[0]);
        Assert.Equal(298, part1.Length - 1);
        Assert.Equal(5, part2.Length - 1);
        Assert.StartsWith("task,Parent,", part2[1]);
        Assert.StartsWith("task,a,,4,2", part2[2]);
        Assert.StartsWith("task,After,", part2[5]);
    }

    [Fact]
    public void Exactly_300_rows_stay_in_one_file()
    {
        var tasks = Enumerable.Range(0, 300).Select(i => TestData.Task($"T{i}")).ToArray();
        var files = TodoistCsvExporter.Export(TestData.Archive((TestData.List("L"), tasks)), false);
        Assert.Equal("todoist/L.csv", Assert.Single(files).Path);
    }

    [Fact]
    public void Parent_with_more_than_299_steps_is_cut_and_reported()
    {
        var steps = new JsonArray();
        for (var i = 0; i < 305; i++)
        {
            steps.Add((JsonNode)new JsonObject { ["displayName"] = $"s{i}", ["isChecked"] = false });
        }

        var parent = TestData.Task("Huge");
        parent["checklistItems"] = steps;
        var losses = new List<Loss>();
        var files = TodoistCsvExporter.Export(TestData.Archive((TestData.List("L"), [parent])), false, losses);

        Assert.Equal(301, Lines(Assert.Single(files)).Length);
        Assert.Contains(losses, l => l.Kind == LossKinds.StepOverflow && l.Message.Contains("6 steps"));
    }

    [Fact]
    public void Reports_todoist_losses()
    {
        var archive = TestData.Archive((TestData.List("Shared", shared: true), [
            TestData.Task("All", """
                { "isReminderOn": true, "reminderDateTime": { "dateTime": "2026-11-25T08:00:00", "timeZone": "UTC" },
                  "startDateTime": { "dateTime": "2026-11-20T23:00:00", "timeZone": "UTC" },
                  "attachments": [ { "name": "a.png" }, { "name": "b.pdf" } ],
                  "recurrence": { "pattern": { "type": "relativeYearly", "interval": 1, "month": 3, "daysOfWeek": ["friday"], "index": "last" } } }
                """),
        ]));

        var losses = new List<Loss>();
        TodoistCsvExporter.Export(archive, false, losses);

        Assert.Contains(losses, l => l.Kind == LossKinds.SharedListMembersNotCarried && l.Task is null);
        Assert.Contains(losses, l => l.Kind == LossKinds.ReminderNotCarried);
        Assert.Contains(losses, l => l.Kind == LossKinds.StartDateNotCarried);
        Assert.Contains(losses, l => l.Kind == LossKinds.AttachmentNotCarried && l.Message.Contains("a.png") && l.Message.Contains("b.pdf"));
        Assert.Contains(losses, l => l.Kind == LossKinds.RecurrenceApproximated && l.Message.Contains("last fri"));
        Assert.All(losses, l => Assert.Equal("todoist", l.Format));
    }
}

public class MarkdownTests
{
    [Fact]
    public void Renders_open_and_completed_tasks()
    {
        var archive = TestData.Archive((TestData.List("Home"), [
            TestData.Task("Fix tap", """
                { "importance": "high", "categories": ["Home repairs"],
                  "dueDateTime": { "dateTime": "2026-11-24T23:00:00", "timeZone": "UTC" },
                  "isReminderOn": true, "reminderDateTime": { "dateTime": "2026-11-25T08:00:00", "timeZone": "UTC" },
                  "recurrence": { "pattern": { "type": "weekly", "interval": 1, "daysOfWeek": ["wednesday"] } },
                  "checklistItems": [ { "displayName": "buy washer", "isChecked": true }, { "displayName": "fit it", "isChecked": false } ],
                  "body": { "content": "Line 1\n\nLine 3", "contentType": "text" },
                  "linkedResources": [ { "displayName": "Guide", "webUrl": "https://example.com/g" } ],
                  "attachments": [ { "name": "photo.jpg", "size": 2048 } ] }
                """),
            TestData.Task("Plain"),
            TestData.Task("Done", """{ "status": "completed", "completedDateTime": { "dateTime": "2026-11-01T00:00:00", "timeZone": "UTC" } }"""),
        ]));

        var losses = new List<Loss>();
        var file = Assert.Single(MarkdownExporter.Export(archive, losses));
        Assert.Equal("markdown/Home.md", file.Path);
        var expected = """
            # Home

            - [ ] Fix tap (due 2026-11-25, repeats every wed, reminder 2026-11-25 09:00, high importance) #Home-repairs
                - [x] buy washer
                - [ ] fit it
                > Line 1
                >
                > Line 3
                - link: [Guide](https://example.com/g)
                - attachment: photo.jpg (2 KB)
            - [ ] Plain

            ## Completed

            - [x] Done (completed 2026-11-01)

            """.Replace("\r\n", "\n");
        Assert.Equal(expected, file.GetText());
        Assert.Single(losses);
        Assert.Equal(("markdown", LossKinds.AttachmentNotCarried), (losses[0].Format, losses[0].Kind));
    }

    [Fact]
    public void Empty_list_still_gets_a_file_with_heading()
    {
        var file = Assert.Single(MarkdownExporter.Export(TestData.Archive((TestData.List("Empty"), []))));
        Assert.Equal("# Empty\n", file.GetText());
    }
}

public class GenericCsvTests
{
    [Fact]
    public void Has_bom_header_and_one_row_per_task()
    {
        var archive = TestData.Archive(
            (TestData.List("A"), [
                TestData.Task("One", """
                    { "importance": "high", "dueDateTime": { "dateTime": "2026-11-24T23:00:00", "timeZone": "UTC" },
                      "checklistItems": [ { "displayName": "x", "isChecked": true }, { "displayName": "y" } ],
                      "categories": ["c1", "c2"], "createdDateTime": "2026-10-01T08:00:00Z" }
                    """),
                TestData.Task("Two", """{ "status": "completed", "completedDateTime": { "dateTime": "2026-11-01T00:00:00", "timeZone": "UTC" } }"""),
            ]),
            (TestData.List("B"), [TestData.Task("Three")]));

        var file = GenericCsvExporter.Export(archive);
        Assert.Equal("tasks.csv", file.Path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.Content.Take(3));

        var lines = file.GetText().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("List,Title,Status,Importance,Due,Start,Reminder,Repeats,Notes,Steps,Categories,Links,Attachments,Created,Completed", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.Equal("A,One,open,high,2026-11-25,,,,,[x] x; [ ] y,c1; c2,,,2026-10-01 10:00,", lines[1]);
        Assert.Equal("A,Two,completed,normal,,,,,,,,,,,2026-11-01 01:00", lines[2]);
        Assert.StartsWith("B,Three,open,", lines[3]);
    }
}

public class FileNameTests
{
    [Theory]
    [InlineData("Work: Q4/Q1 <plan>?", "Work_ Q4_Q1 _plan__")]
    [InlineData("a\\b|c*d\"e", "a_b_c_d_e")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("ends with dots...", "ends with dots")]
    [InlineData("trailing . . ", "trailing")]
    [InlineData("...", "list")]
    [InlineData("", "list")]
    [InlineData("Kupovina čćšđž", "Kupovina čćšđž")]
    public void Sanitizes(string input, string expected)
    {
        Assert.Equal(expected, FileNames.Sanitize(input));
    }

    [Fact]
    public void Deduplicates_case_insensitively()
    {
        var names = FileNames.SanitizeUnique(["Work", "work", "WORK", "Home", "Work (2)", "a/b", "a:b"]);
        Assert.Equal(["Work", "work (2)", "WORK (3)", "Home", "Work (2) (2)", "a_b", "a_b (2)"], names);
    }

    [Fact]
    public void Exporters_use_deduplicated_names()
    {
        var archive = TestData.Archive(
            (TestData.List("Tasks", id: "1"), [TestData.Task("a")]),
            (TestData.List("tasks", id: "2"), [TestData.Task("b")]));

        Assert.Equal(["markdown/Tasks.md", "markdown/tasks (2).md"], MarkdownExporter.Export(archive).Select(f => f.Path));
        Assert.Equal(["todoist/Tasks.csv", "todoist/tasks (2).csv"], TodoistCsvExporter.Export(archive, false).Select(f => f.Path));
    }
}

public class LossReportTests
{
    private static TodoExit.Core.Archive.TodoArchiveDocument Doc() => TestData.Document(
        (TestData.List("Shared", shared: true), [
            TestData.Task("Open", """{ "attachments": [ { "name": "f.txt" } ], "isReminderOn": true, "reminderDateTime": { "dateTime": "2026-11-25T08:00:00", "timeZone": "UTC" } }"""),
            TestData.Task("Done", """{ "status": "completed" }"""),
        ]));

    [Fact]
    public void Report_contains_inventory_and_losses_per_requested_format()
    {
        var doc = Doc();
        var archive = Model.ArchiveParser.Parse(doc, TestData.Belgrade);
        var result = ExportService.Export(doc, archive, new ExportOptions());

        Assert.Contains(result.Files, f => f.Path == "report.md");
        var json = JsonNode.Parse(result.Files.Single(f => f.Path == "report.json").GetText())!.AsObject();

        var inventory = json["inventory"]!.AsObject();
        Assert.Equal(2, (int)inventory["tasks"]!);
        Assert.Equal(1, (int)inventory["sharedLists"]!);
        Assert.Equal(11, inventory.Count);

        var losses = json["losses"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var kinds = losses.Select(l => ((string)l["format"]!, (string)l["kind"]!)).ToHashSet();
        Assert.Contains(("todoist", "completed-skipped"), kinds);
        Assert.Contains(("todoist", "reminder-not-carried"), kinds);
        Assert.Contains(("todoist", "attachment-not-carried"), kinds);
        Assert.Contains(("todoist", "shared-list-members-not-carried"), kinds);
        Assert.Contains(("markdown", "attachment-not-carried"), kinds);
        Assert.Contains(("csv", "attachment-not-carried"), kinds);
        Assert.DoesNotContain(losses, l => (string)l["format"]! == "json");
        Assert.All(losses.Where(l => (string)l["format"]! != "todoist"), l => Assert.Equal("attachment-not-carried", (string)l["kind"]!));

        var shared = losses.Single(l => (string)l["kind"]! == "shared-list-members-not-carried");
        Assert.True(shared.ContainsKey("task"));
        Assert.Null(shared["task"]);
    }

    [Fact]
    public void Only_requested_formats_are_reported_and_written()
    {
        var doc = Doc();
        var archive = Model.ArchiveParser.Parse(doc, TestData.Belgrade);
        var result = ExportService.Export(doc, archive, new ExportOptions(ExportFormats.Json | ExportFormats.Csv));

        Assert.Equal(["archive.json", "tasks.csv", "report.md", "report.json"], result.Files.Select(f => f.Path));
        Assert.All(result.Report.Losses, l => Assert.Equal("csv", l.Format));
        var md = result.Files.Single(f => f.Path == "report.md").GetText();
        Assert.Contains("| Tasks | 2 |", md);
        Assert.Contains("attachment-not-carried", md);
        Assert.DoesNotContain("Todoist", md);
    }
}

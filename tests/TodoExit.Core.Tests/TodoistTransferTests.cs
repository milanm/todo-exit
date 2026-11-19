using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TodoExit.Core.Export;
using TodoExit.Core.Model;
using TodoExit.Core.Todoist;

namespace TodoExit.Core.Tests;

public class TodoistTransferTests
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    internal const string ReportJson = """
        { "id": "t-report", "importance": "high",
          "body": { "content": "Line 1\nLine 2", "contentType": "text" },
          "categories": ["Q4 review", "Work"],
          "linkedResources": [ { "displayName": "Mail", "webUrl": "https://example.com/m" } ],
          "dueDateTime": { "dateTime": "2026-12-01T00:00:00.0000000", "timeZone": "UTC" },
          "startDateTime": { "dateTime": "2026-11-25T00:00:00.0000000", "timeZone": "UTC" },
          "isReminderOn": true, "reminderDateTime": { "dateTime": "2026-12-01T08:00:00.0000000", "timeZone": "UTC" },
          "hasAttachments": true,
          "checklistItems": [
            { "displayName": "a", "isChecked": false },
            { "displayName": "b", "isChecked": false },
            { "displayName": "c", "isChecked": true, "checkedDateTime": "2026-11-15T09:30:00Z" } ] }
        """;

    internal const string WeeklyJson = """
        { "id": "t-weekly", "dueDateTime": { "dateTime": "2026-11-22T23:00:00.0000000", "timeZone": "UTC" },
          "recurrence": { "pattern": { "type": "weekly", "interval": 1, "daysOfWeek": ["monday"] }, "range": { "type": "noEnd" } } }
        """;

    internal const string DoneJson = """
        { "id": "t-done", "status": "completed",
          "completedDateTime": { "dateTime": "2026-11-10T00:00:00.0000000", "timeZone": "UTC" },
          "dueDateTime": { "dateTime": "2026-11-09T23:00:00.0000000", "timeZone": "UTC" },
          "recurrence": { "pattern": { "type": "daily", "interval": 1 }, "range": { "type": "noEnd" } },
          "checklistItems": [ { "displayName": "x", "isChecked": false } ] }
        """;

    internal static (JsonObject, JsonObject[])[] RichLists() =>
    [
        (TestData.List("Work"), [TestData.Task("Report", ReportJson), TestData.Task("Weekly", WeeklyJson), TestData.Task("Done", DoneJson)]),
        (TestData.List("Empty"), []),
        (TestData.List("Home"), [TestData.Task("Milk", """{ "id": "t-milk" }""")]),
    ];

    internal static TodoArchive Rich() => TestData.Archive(RichLists());

    private sealed class Run
    {
        public required TodoistPlan Plan { get; init; }

        public required TodoistTransferState State { get; init; }

        public required TodoistTransferResult Result { get; init; }

        public List<TimeSpan> Delays { get; } = new();

        public List<byte[]> Saves { get; } = new();
    }

    private static async Task<Run> Transfer(
        TodoArchive archive, FakeTodoist fake, bool includeCompleted = false, IReadOnlyList<string>? lists = null,
        bool allowDuplicates = false, TodoistTransferState? state = null, int maxAttempts = 6)
    {
        var delays = new List<TimeSpan>();
        var saves = new List<byte[]>();
        using var http = new HttpClient(fake, disposeHandler: false);
        var client = new TodoistClient(http, new TodoistToken(FakeTodoist.Token), (d, _) =>
        {
            delays.Add(d);
            return Task.CompletedTask;
        }) { MaxAttempts = maxAttempts };
        var account = await client.ReadAccountAsync(CancellationToken.None);
        var settings = state?.Settings ?? new TodoistPlanSettings
        {
            IncludeCompleted = includeCompleted,
            Lists = lists,
            AllowDuplicateNames = allowDuplicates,
            TimeZoneId = "Europe/Belgrade",
            MaxTasksPerProject = account.MaxTasks ?? 300,
            Reminders = account.Reminders,
            PlannedAt = Now,
        };
        var plan = TodoistPlanner.Build(archive, settings, account, state);
        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));
        state ??= TodoistTransfer.NewState(plan, "fingerprint", Now);
        var result = await TodoistTransfer.RunAsync(plan, state, client, (s, _) =>
        {
            saves.Add(s.ToUtf8Bytes());
            return Task.CompletedTask;
        }, null, CancellationToken.None);
        var run = new Run { Plan = plan, State = state, Result = result };
        run.Delays.AddRange(delays);
        run.Saves.AddRange(saves);
        return run;
    }

    private static FakeTodoist.Item Single(FakeTodoist fake, string content) => Assert.Single(fake.Items, i => i.Content == content);

    [Fact]
    public async Task Every_field_is_mapped_and_priority_is_the_inverse_of_the_csv()
    {
        var fake = new FakeTodoist();
        var run = await Transfer(Rich(), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Equal(["Inbox", "Work", "Home"], fake.Projects.Select(p => p.Name));
        var report = Single(fake, "Report");
        Assert.Equal(fake.ProjectNamed("Work").Id, report.ProjectId);
        Assert.Null(report.ParentId);
        Assert.Equal("Line 1\nLine 2\nLink: Mail https://example.com/m", report.Description);
        Assert.Equal(4, report.Priority);
        Assert.Equal(["Q4_review", "Work"], report.Labels);
        Assert.Equal("""{"date":"2026-12-01"}""", report.Due!.ToJsonString());

        // The CSV writes the same task as priority 1 (its scale is inverted) with the same labels and date.
        var csv = TodoistCsvExporter.Export(Rich(), false)[0].GetText().Split("\r\n");
        Assert.Equal("task,Report @Q4_review @Work,\"Line 1\nLine 2\nLink: Mail https://example.com/m\",1,1,,,2026-12-01,en,,,None", csv[1]);

        var steps = fake.Items.Where(i => i.ParentId == report.Id).ToList();
        Assert.Equal(["a", "b"], steps.Select(s => s.Content));
        Assert.All(steps, s => Assert.Equal(1, s.Priority));
        Assert.All(steps, s => Assert.Equal(report.ProjectId, s.ProjectId));

        Assert.Equal("""{"string":"every mon starting 2026-11-23","lang":"en"}""", Single(fake, "Weekly").Due!.ToJsonString());
        Assert.Equal(1, Single(fake, "Weekly").Priority);
        Assert.DoesNotContain(fake.Items, i => i.Content is "Done" or "c" or "x");
        Assert.Equal(fake.ProjectNamed("Home").Id, Single(fake, "Milk").ProjectId);
        Assert.Empty(fake.Reminders);
        Assert.DoesNotContain(fake.Writes.SelectMany(w => w.Commands!), c => (string)c!["type"]! == "reminder_add");

        var losses = TodoistTransferReport.Losses(Rich(), run.Plan, run.State).Losses;
        Assert.All(losses, l => Assert.Equal(ExportFormatNames.TodoistApi, l.Format));
        Assert.Contains(losses, l => l.Kind == LossKinds.ReminderNotCarried && l.Task == "Report" && l.Message.Contains("does not include reminders"));
        Assert.Contains(losses, l => l.Kind == LossKinds.StartDateNotCarried);
        Assert.Contains(losses, l => l.Kind == LossKinds.AttachmentNotCarried && l.Task == "Report");
        Assert.Contains(losses, l => l.Kind == LossKinds.CompletedSkipped && l.Count == 1);
        Assert.Contains(losses, l => l.Kind == LossKinds.CheckedStepSkipped && l.Count == 1);
        Assert.DoesNotContain(losses, l => l.List == "Empty");
    }

    [Fact]
    public async Task Completed_tasks_and_checked_steps_are_completed_with_their_dates_and_reminders_added_when_allowed()
    {
        var fake = new FakeTodoist { RemindersAllowed = true };
        var run = await Transfer(Rich(), fake, includeCompleted: true);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        var c = Single(fake, "c");
        Assert.True(c.Checked);
        Assert.Equal("2026-11-15T09:30:00.000000Z", c.CompletedAt);
        Assert.False(Single(fake, "a").Checked);

        var done = Single(fake, "Done");
        Assert.True(done.Checked);
        Assert.Equal("2026-11-10T00:00:00.000000Z", done.CompletedAt);
        Assert.Equal("""{"date":"2026-11-10"}""", done.Due!.ToJsonString()); // no recurring due on a completed task
        var x = Single(fake, "x");
        Assert.Equal(done.Id, x.ParentId);
        Assert.True(x.Checked);
        Assert.Equal("2026-11-10T00:00:00.000000Z", x.CompletedAt);

        var reminder = Assert.Single(fake.Reminders);
        Assert.Equal(Single(fake, "Report").Id, reminder.ItemId);
        Assert.Equal("absolute", (string?)reminder.Args["type"]);
        Assert.Equal("""{"date":"2026-12-01T08:00:00.000000Z"}""", reminder.Args["due"]!.ToJsonString());
        Assert.Equal(["item_id", "type", "due"], reminder.Args.Select(p => p.Key));

        // Order: add parent, add child, complete child, complete parent; a reminder right after its task.
        var commands = fake.Writes.SelectMany(w => w.Commands!).ToList();
        var types = commands.Select(cmd => $"{cmd!["type"]}:{cmd["args"]!["content"] ?? string.Empty}").ToList();
        var k = types.IndexOf("item_add:Done");
        Assert.Equal(["item_add:Done", "item_add:x", "item_complete:", "item_complete:"], types.Skip(k).Take(4));
        Assert.Equal((string?)commands[k + 1]!["temp_id"], (string?)commands[k + 2]!["args"]!["id"]);
        Assert.Equal((string?)commands[k]!["temp_id"], (string?)commands[k + 3]!["args"]!["id"]);
        var r = types.IndexOf("item_add:Report");
        Assert.Equal(["item_add:Report", "reminder_add:", "item_add:a", "item_add:b", "item_add:c", "item_complete:", "item_add:Weekly"], types.Skip(r).Take(7));
        Assert.Equal(3, types.Count(t => t == "item_complete:"));

        var counts = TransferCounts.From(run.Plan, run.State);
        Assert.Equal(new TransferCounts(2, 4, 4, 3, 1, 0, 0), counts);
    }

    [Fact]
    public async Task Premium_only_reminder_is_reported_and_does_not_stop_the_transfer()
    {
        var fake = new FakeTodoist();
        var archive = Rich();
        using var http = new HttpClient(fake, disposeHandler: false);
        var client = new TodoistClient(http, new TodoistToken(FakeTodoist.Token));
        var account = await client.ReadAccountAsync(CancellationToken.None) with { Reminders = true }; // the plan says yes, the server says no
        var plan = TodoistPlanner.Build(archive, new TodoistPlanSettings { TimeZoneId = "Europe/Belgrade", Reminders = true, PlannedAt = Now }, account);
        var state = TodoistTransfer.NewState(plan, "f", Now);
        var result = await TodoistTransfer.RunAsync(plan, state, client, (_, _) => Task.CompletedTask, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Completed, result.Status);
        Assert.Empty(fake.Reminders);
        Assert.True(state.RemindersRefused);
        Assert.Contains(state.Errors, e => e.LossKind == LossKinds.ReminderNotCarried && e.Tag == "PREMIUM_ONLY");
        Assert.Equal(0, TransferCounts.From(plan, state).Errors);
        Assert.Equal(5, fake.Items.Count);
    }

    [Fact]
    public void Past_reminders_are_not_planned()
    {
        var archive = TestData.Archive((TestData.List("L"), [TestData.Task("Old", """
            { "isReminderOn": true, "reminderDateTime": { "dateTime": "2026-11-01T08:00:00.0000000", "timeZone": "UTC" } }
            """)]));
        var plan = TodoistPlanner.Build(archive, new TodoistPlanSettings { TimeZoneId = "Europe/Belgrade", Reminders = true, PlannedAt = Now }, null);
        Assert.Null(plan.Projects[0].Items[0].ReminderUtc);
        Assert.Contains(plan.Losses, l => l.Kind == LossKinds.ReminderNotCarried && l.Message.Contains("in the past"));
    }

    [Fact]
    public async Task Plan_with_a_token_only_reads()
    {
        var fake = new FakeTodoist();
        using var http = new HttpClient(fake, disposeHandler: false);
        var client = new TodoistClient(http, new TodoistToken(FakeTodoist.Token));
        var account = await client.ReadAccountAsync(CancellationToken.None);
        var plan = TodoistPlanner.Build(Rich(), new TodoistPlanSettings { TimeZoneId = "UTC", PlannedAt = Now }, account);

        Assert.True(plan.CanStart);
        Assert.Equal(2, plan.ProjectsNeeded);
        Assert.Equal(5, plan.ProjectsAvailable); // the Inbox does not count
        Assert.Equal("tester@example.com", account.Email);
        Assert.Equal("Beginner", account.PlanName);
        var request = Assert.Single(fake.Requests);
        Assert.False(request.Form.ContainsKey("commands"));
        Assert.Equal("*", request.Form["sync_token"]);
        Assert.Equal("""["user","user_plan_limits","projects"]""", request.Form["resource_types"]);
    }

    private static TodoArchive Big(int tasks, int steps, string name = "Big", string? prefix = null) =>
        TestData.Archive((TestData.List(name), Enumerable.Range(0, tasks).Select(i => TestData.Task($"{prefix ?? name} {i}", $$"""
            { "id": "{{name}}-{{i}}", "checklistItems": [ {{string.Join(",", Enumerable.Range(0, steps).Select(s => $$"""{ "displayName": "step {{i}}.{{s}}" }"""))}} ] }
            """)).ToArray()));

    private static void AssertEachOnce(FakeTodoist fake, int tasks, int steps, string name = "Big")
    {
        var project = fake.ProjectNamed(name);
        Assert.Single(fake.Projects, p => p.Name == name);
        for (var i = 0; i < tasks; i++)
        {
            var task = Assert.Single(fake.Items, it => it.Content == $"{name} {i}");
            Assert.Equal(project.Id, task.ProjectId);
            for (var s = 0; s < steps; s++)
            {
                var step = Assert.Single(fake.Items, it => it.Content == $"step {i}.{s}");
                Assert.Equal(task.Id, step.ParentId);
            }
        }

        Assert.Equal(tasks * (1 + steps), fake.Items.Count);
    }

    [Fact]
    public async Task Requests_carry_at_most_100_commands_and_parents_resolve_across_boundaries()
    {
        var fake = new FakeTodoist { MaxTasks = 1000 };
        var run = await Transfer(Big(120, 3), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Empty(run.State.Errors);
        Assert.All(fake.Writes, w => Assert.InRange(w.Commands!.Count, 1, 100));
        Assert.Equal(5, fake.Writes.Count()); // 481 commands
        AssertEachOnce(fake, 120, 3);

        // A sub-task whose parent was created in an earlier request refers to the real id, not a temp_id.
        var second = fake.Writes.ElementAt(1).Commands!;
        var first = second[0]!;
        Assert.Equal("item_add", (string)first["type"]!);
        Assert.StartsWith("p", (string)first["args"]!["project_id"]!);
        Assert.All(run.State.Ids.Values, id => Assert.False(string.IsNullOrEmpty(id)));
    }

    [Fact]
    public async Task Lost_response_is_retried_with_the_same_uuids_and_ids_are_recovered()
    {
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.LoseResponse.Add(1);
        fake.LoseResponse.Add(3);
        var run = await Transfer(Big(120, 3), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Empty(run.State.Errors);
        AssertEachOnce(fake, 120, 3);
        Assert.Equal(2, run.Delays.Count);

        var writes = fake.Writes.ToList();
        Assert.Equal(writes[0].Form["commands"], writes[1].Form["commands"]); // the retry is byte for byte the same
        Assert.Contains(fake.Requests, r => r.Form.TryGetValue("resource_types", out var t) && t.Contains("items"));
        Assert.Equal(run.Plan.Projects.Sum(p => p.Items.Count) + 1, run.State.Ids.Count);
        Assert.All(run.State.Ids.Values, id => Assert.False(string.IsNullOrEmpty(id)));
    }

    [Fact]
    public async Task Resume_after_a_lost_response_ends_with_every_task_exactly_once()
    {
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.LoseResponse.Add(2);
        var archive = Big(120, 3);
        var first = await Transfer(archive, fake, maxAttempts: 1);

        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);
        var saved = TodoistTransferState.Parse(first.Saves[^1]);
        Assert.Equal(100, saved.Pending.Count);

        var second = await Transfer(archive, fake, state: saved);
        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        Assert.Empty(second.State.Errors);
        AssertEachOnce(fake, 120, 3);
        Assert.True(second.State.Finished);
        Assert.Empty(second.State.Pending);
    }

    private static JsonObject[] Fillers(int n) =>
        Enumerable.Range(0, n).Select(i => TestData.Task($"Filler {i}", $$"""{ "id": "f{{i}}" }""")).ToArray();

    [Fact]
    public async Task Resume_finds_tasks_whose_bare_url_todoist_rewrote_into_a_titled_link()
    {
        // Request 1: project_add, 97 fillers, P and Q (both the same bare URL) = 100 commands. Q's sub-task is the
        // first command of request 2, so it needs Q's id from the recovery.
        const string Url = "https://www.learndash.com/";
        var tasks = Fillers(97)
            .Append(TestData.Task(Url, """{ "id": "p" }"""))
            .Append(TestData.Task(Url, """{ "id": "q", "checklistItems": [ { "displayName": "q step" } ] }"""))
            .ToArray();
        var archive = TestData.Archive((TestData.List("Links"), tasks));
        var fake = new FakeTodoist();
        fake.LoseResponse.Add(1);
        var first = await Transfer(archive, fake, maxAttempts: 1);
        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);

        // Minutes later: Todoist has rewritten the content, and the user added a task with the same URL to the new project.
        fake.RewriteBareUrls = true;
        var project = fake.ProjectNamed("Links");
        fake.Items.Add(new FakeTodoist.Item { Id = "user-1", ProjectId = project.Id, Content = Url, ChildOrder = 100 });

        var second = await Transfer(archive, fake, state: TodoistTransferState.Parse(first.Saves[^1]));

        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        Assert.Empty(second.State.Errors);
        var links = fake.Items.Where(i => i.ProjectId == project.Id && i.ParentId is null && i.Content.Contains(Url, StringComparison.Ordinal))
            .OrderBy(i => i.ChildOrder).ToList();
        Assert.Equal(3, links.Count); // P, Q and the user's task: nothing created twice
        Assert.All(links, i => Assert.StartsWith("[Page title of www.learndash.com | Sell [things]](", i.Content));
        Assert.Equal(links[0].Id, second.State.Ids["t:p"]);
        Assert.Equal(links[1].Id, second.State.Ids["t:q"]);
        var step = Single(fake, "q step");
        Assert.Equal(links[1].Id, step.ParentId);
        Assert.DoesNotContain(fake.Items, i => i.ParentId == "user-1");
        Assert.DoesNotContain("user-1", second.State.Ids.Values);
        Assert.Equal(97 + 2 + 1 + 1, fake.Items.Count);
    }

    [Fact]
    public async Task Resume_tells_sibling_sub_tasks_with_the_same_rewritten_url_apart_by_position()
    {
        // Request 1: project_add, 96 fillers, T, s0 (checked) and s1, both with the same URL = 100 commands.
        // Request 2 starts with item_complete for s0, which must reach the first sibling and not the second.
        const string Url = "https://same.example/page";
        var tasks = Fillers(96)
            .Append(TestData.Task("https://parent.example/", $$"""
                { "id": "t", "checklistItems": [
                    { "displayName": "{{Url}}", "isChecked": true, "checkedDateTime": "2026-11-15T09:30:00Z" },
                    { "displayName": "{{Url}}", "isChecked": false } ] }
                """))
            .ToArray();
        var archive = TestData.Archive((TestData.List("Steps"), tasks));
        var fake = new FakeTodoist();
        fake.LoseResponse.Add(1);
        var first = await Transfer(archive, fake, includeCompleted: true, maxAttempts: 1);
        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);
        Assert.DoesNotContain(fake.Items, i => i.Checked);

        fake.RewriteBareUrls = true;
        var second = await Transfer(archive, fake, state: TodoistTransferState.Parse(first.Saves[^1]));

        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        Assert.Empty(second.State.Errors);
        var parent = Assert.Single(fake.Items, i => i.Content.Contains("https://parent.example/", StringComparison.Ordinal));
        Assert.StartsWith("[Page title of parent.example", parent.Content);
        Assert.Equal(parent.Id, second.State.Ids["t:t"]);
        var steps = fake.Items.Where(i => i.ParentId == parent.Id).OrderBy(i => i.ChildOrder).ToList();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, i => Assert.EndsWith($"]({Url})", i.Content));
        Assert.Equal(steps[0].Id, second.State.Ids["s:t/0"]);
        Assert.Equal(steps[1].Id, second.State.Ids["s:t/1"]);
        Assert.True(steps[0].Checked);
        Assert.Equal("2026-11-15T09:30:00.000000Z", steps[0].CompletedAt);
        Assert.False(steps[1].Checked);
        Assert.False(parent.Checked);
        Assert.Equal(96 + 3, fake.Items.Count);
    }

    [Theory]
    [InlineData("https://www.learndash.com/", "[LearnDash Learning Management System & Software. Sell Courses with WordPress](https://www.learndash.com/)")]
    [InlineData("Read https://a.example/x later", "Read [A [beta] title](https://a.example/x) later")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)", "[Foo (bar) - Wikipedia](https://en.wikipedia.org/wiki/Foo_(bar))")]
    [InlineData("https://a.example/", "[Title \\] escaped](https://a.example/ \"tooltip\")")]
    [InlineData("Plain text", "Plain text")]
    public void Match_key_counts_a_markdown_link_as_its_address(string planned, string returned)
    {
        Assert.Equal(TodoistTransfer.MatchKey(planned), TodoistTransfer.MatchKey(returned));
    }

    [Theory]
    [InlineData("https://a.example/", "[Title](https://b.example/)")]
    [InlineData("Buy milk", "Buy bread")]
    [InlineData("Read https://a.example/x later", "[Title](https://a.example/x)")]
    public void Match_key_keeps_different_contents_apart(string planned, string returned)
    {
        Assert.NotEqual(TodoistTransfer.MatchKey(planned), TodoistTransfer.MatchKey(returned));
    }

    [Fact]
    public async Task Resume_after_a_request_that_never_arrived_sends_it_normally()
    {
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.DropRequest.Add(2);
        var archive = Big(120, 3);
        var first = await Transfer(archive, fake, maxAttempts: 1);
        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);

        var second = await Transfer(archive, fake, state: TodoistTransferState.Parse(first.Saves[^1]));
        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        AssertEachOnce(fake, 120, 3);
        Assert.DoesNotContain(fake.Requests, r => r.Form.TryGetValue("resource_types", out var t) && t.Contains("items"));
    }

    [Fact]
    public async Task Lost_response_with_completed_items_recovers_open_ones_and_completes_each_once()
    {
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.LoseResponse.Add(1);
        var tasks = Enumerable.Range(0, 60).Select(i => TestData.Task($"T{i}", i % 2 == 0
            ? $$"""{ "id": "t{{i}}", "checklistItems": [ { "displayName": "s{{i}}", "isChecked": true } ] }"""
            : $$"""{ "id": "t{{i}}", "status": "completed", "completedDateTime": { "dateTime": "2026-11-0{{1 + i % 9}}T00:00:00.0000000", "timeZone": "UTC" } }""")).ToArray();
        var run = await Transfer(TestData.Archive((TestData.List("Mixed"), tasks)), fake, includeCompleted: true);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Empty(run.State.Errors);
        Assert.Equal(90, fake.Items.Count);
        for (var i = 0; i < 60; i++)
        {
            var t = Single(fake, $"T{i}");
            Assert.Equal(i % 2 == 1, t.Checked);
            if (i % 2 == 0)
            {
                Assert.True(Single(fake, $"s{i}").Checked);
                Assert.Equal(t.Id, Single(fake, $"s{i}").ParentId);
            }
        }
    }

    [Fact]
    public async Task Throttled_request_waits_for_retry_after_and_continues()
    {
        var fake = new FakeTodoist { Throttle = 1, RetryAfterSeconds = 7 };
        var run = await Transfer(Rich(), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Equal([TimeSpan.FromSeconds(7)], run.Delays);
        Assert.Equal(5, fake.Items.Count);
    }

    [Fact]
    public async Task Server_errors_back_off_then_stop_cleanly_with_state_saved()
    {
        var fake = new FakeTodoist { ServerErrors = 100 };
        var run = await Transfer(Rich(), fake, maxAttempts: 4);

        Assert.Equal(TransferStatus.Interrupted, run.Result.Status);
        Assert.Contains("after 4 attempts", run.Result.Message);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], run.Delays);
        Assert.NotEmpty(TodoistTransferState.Parse(run.Saves[^1]).Pending);
    }

    [Fact]
    public async Task Per_command_error_is_reported_children_are_skipped_and_the_rest_continues()
    {
        var fake = new FakeTodoist();
        fake.FailContent["Report"] = "INVALID_ARGUMENT_VALUE";
        var run = await Transfer(Rich(), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.DoesNotContain(fake.Items, i => i.Content is "Report" or "a" or "b");
        Assert.Single(fake.Items, i => i.Content == "Weekly");
        Assert.Single(fake.Items, i => i.Content == "Milk");
        var error = Assert.Single(run.State.Errors, e => e.LossKind == LossKinds.TodoistError);
        Assert.Equal("INVALID_ARGUMENT_VALUE", error.Tag);
        Assert.Equal("Work", error.List);
        Assert.Equal("Report", error.Task);
        Assert.Equal(2, run.State.Errors.Count(e => e.LossKind == LossKinds.ParentNotCreated));
        Assert.Equal(new TransferCounts(2, 2, 0, 0, 0, 1, 2), TransferCounts.From(run.Plan, run.State));

        var md = TodoistTransferReport.ToFiles(Rich(), run.Plan, run.State, run.Result, TimeSpan.FromSeconds(1))[0].GetText();
        Assert.StartsWith("# todo-exit transfer to Todoist\n\nThe transfer finished.", md);
        Assert.Contains("### Todoist (direct transfer)", md);
        Assert.Contains("Refused by the test (INVALID_ARGUMENT_VALUE)", md);
        Assert.Contains("parent-not-created", md);
    }

    [Fact]
    public async Task Failed_parent_skips_children_in_the_same_and_in_later_requests()
    {
        // Big(30, 3): 1 + 24 * 4 = 97, so "Big 24" and two of its steps are in request 1 and the third step in request 2.
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.FailContent["Big 24"] = "SOME_ERROR";
        var run = await Transfer(Big(30, 3), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Single(run.State.Errors, e => e.LossKind == LossKinds.TodoistError && e.Tag == "SOME_ERROR" && e.Task == "Big 24");
        Assert.Equal(3, run.State.Errors.Count(e => e.LossKind == LossKinds.ParentNotCreated && e.Task == "Big 24"));
        Assert.DoesNotContain(fake.Writes.ElementAt(1).Commands!, c => (string?)c!["args"]!["content"] == "step 24.2");
        Assert.Equal(29 * 4, fake.Items.Count);

        // Big(60, 1): "Big 49" is the last command of request 1; its step would be in request 2 and is never sent.
        fake = new FakeTodoist { MaxTasks = 1000 };
        fake.FailContent["Big 49"] = "SOME_ERROR";
        run = await Transfer(Big(60, 1), fake);
        Assert.Equal("Big 49", (string?)fake.Writes.First().Commands![99]!["args"]!["content"]);
        Assert.DoesNotContain(fake.Writes.SelectMany(w => w.Commands!), c => (string?)c!["args"]!["content"] == "step 49.0");
        Assert.Single(run.State.Errors, e => e.LossKind == LossKinds.ParentNotCreated);
        Assert.Equal(59 * 2, fake.Items.Count);
    }

    [Fact]
    public async Task Large_list_is_split_into_parts_by_the_account_limit()
    {
        var fake = new FakeTodoist { MaxTasks = 5, ActiveLimit = 5 };
        var run = await Transfer(Big(7, 1), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Empty(run.State.Errors);
        Assert.Equal(["Big - part 1", "Big - part 2", "Big - part 3", "Big - part 4"], run.Plan.Projects.Select(p => p.Name));
        Assert.All(run.Plan.Projects, p => Assert.InRange(p.Items.Count, 1, 5));
        Assert.Contains(run.Plan.Losses, l => l.Kind == LossKinds.ListSplit && l.Count == 4);
        Assert.Equal(14, fake.Items.Count);
    }

    [Fact]
    public async Task Completed_tasks_are_interleaved_so_the_active_limit_is_never_exceeded()
    {
        var fake = new FakeTodoist { MaxTasks = 5, ActiveLimit = 5 };
        var tasks = Enumerable.Range(0, 4).Select(i => TestData.Task($"Open {i}", $$"""{ "id": "o{{i}}" }"""))
            .Concat(Enumerable.Range(0, 6).Select(i => TestData.Task($"Done {i}", $$"""{ "id": "d{{i}}", "status": "completed" }""")))
            .ToArray();
        var run = await Transfer(TestData.Archive((TestData.List("L"), tasks)), fake, includeCompleted: true);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Empty(run.State.Errors);
        Assert.Single(run.Plan.Projects);
        Assert.Equal(10, fake.Items.Count);
        Assert.Equal(4, fake.Active.Count());
    }

    [Fact]
    public async Task Too_many_projects_stops_the_plan_and_lists_selection_fits()
    {
        var fake = new FakeTodoist();
        for (var i = 0; i < 4; i++)
        {
            fake.AddProject($"Existing {i}");
        }

        using var http = new HttpClient(fake, disposeHandler: false);
        var account = await new TodoistClient(http, new TodoistToken(FakeTodoist.Token)).ReadAccountAsync(CancellationToken.None);
        var plan = TodoistPlanner.Build(Rich(), new TodoistPlanSettings { PlannedAt = Now }, account);

        Assert.False(plan.CanStart);
        Assert.Contains("needs 2 new Todoist projects", Assert.Single(plan.Problems));
        Assert.Contains("only 1 more fit", plan.Problems[0]);
        Assert.True(plan.TooManyProjects);
        Assert.Equal(
            ["Make room for 1 more project in Todoist (archive or delete one), or choose fewer lists with --lists so that at most 1 project is needed."],
            TodoistPlanText.Fixes(plan, PlanAudience.CommandLine));
        Assert.Equal(
            ["Make room for 1 more project in Todoist (archive or delete one), or choose fewer lists so that at most 1 project is needed."],
            TodoistPlanText.Fixes(plan, PlanAudience.Web));

        var chosen = TodoistPlanner.Build(Rich(), new TodoistPlanSettings { PlannedAt = Now, Lists = ["Home"] }, account);
        Assert.True(chosen.CanStart);
        Assert.Equal(["Home"], chosen.Projects.Select(p => p.Name));
        Assert.Contains(chosen.Losses, l => l.Kind == LossKinds.ListNotSelected && l.List == "Work" && l.Count == 3);
        Assert.Equal(["Missing"], TodoistPlanner.UnknownLists(Rich(), ["Home", "Missing"]));

        var run = await Transfer(Rich(), fake, lists: ["Home"]);
        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Equal(["Milk"], fake.Items.Select(i => i.Content));
    }

    [Fact]
    public async Task Existing_project_name_stops_the_plan_unless_duplicates_are_allowed_and_existing_data_is_never_touched()
    {
        var fake = new FakeTodoist();
        var existing = fake.AddProject("work");
        fake.Items.Add(new FakeTodoist.Item { Id = "old-1", ProjectId = existing.Id, Content = "Old task" });

        using var http = new HttpClient(fake, disposeHandler: false);
        var account = await new TodoistClient(http, new TodoistToken(FakeTodoist.Token)).ReadAccountAsync(CancellationToken.None);
        var plan = TodoistPlanner.Build(Rich(), new TodoistPlanSettings { PlannedAt = Now }, account);
        Assert.False(plan.CanStart);
        Assert.Contains("already has a project named \"Work\"", Assert.Single(plan.Problems));
        Assert.Equal(["Work"], plan.NameClashes);
        Assert.DoesNotContain("--", plan.Problems[0]);
        Assert.Equal(["Rename or remove the Todoist project named \"Work\", or leave that list out."], TodoistPlanText.Fixes(plan, PlanAudience.Web));
        Assert.Contains("--allow-duplicate-names", Assert.Single(TodoistPlanText.Fixes(plan, PlanAudience.CommandLine)));

        var run = await Transfer(Rich(), fake, allowDuplicates: true);
        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Equal("work", existing.Name);
        var old = Assert.Single(fake.Items, i => i.ProjectId == existing.Id);
        Assert.Equal("Old task", old.Content);
        Assert.False(old.Checked);
        Assert.Contains(run.State.PreexistingProjectIds, id => id == existing.Id);
        foreach (var cmd in fake.Writes.SelectMany(w => w.Commands!))
        {
            Assert.Contains((string)cmd!["type"]!, new[] { "project_add", "item_add", "item_complete", "reminder_add" });
            Assert.NotEqual(existing.Id, (string?)cmd["args"]!["project_id"]);
            Assert.NotEqual("old-1", (string?)cmd["args"]!["id"]);
        }
    }

    [Fact]
    public async Task Token_rejected_stops_with_a_clear_message()
    {
        var fake = new FakeTodoist();
        using var http = new HttpClient(fake, disposeHandler: false);
        var client = new TodoistClient(http, new TodoistToken("wrong"));
        var ex = await Assert.ThrowsAsync<TodoistApiException>(() => client.ReadAccountAsync(CancellationToken.None));
        Assert.Equal(TodoistFailure.TokenRejected, ex.Failure);
        Assert.Contains("rejected the token", ex.Message);
        Assert.DoesNotContain("wrong", ex.Message);
    }

    [Fact]
    public async Task Redirect_is_not_followed_and_the_token_goes_to_api_todoist_com_only()
    {
        var fake = new FakeTodoist { RedirectTo = "https://evil.example.com/steal" };
        using var http = new HttpClient(fake, disposeHandler: false);
        var client = new TodoistClient(http, new TodoistToken(FakeTodoist.Token));
        var ex = await Assert.ThrowsAsync<TodoistApiException>(() => client.ReadAccountAsync(CancellationToken.None));

        Assert.Equal(TodoistFailure.UnexpectedResponse, ex.Failure);
        var request = Assert.Single(fake.Requests);
        Assert.Equal("https://api.todoist.com/api/v1/sync", request.Url);

        Assert.True(TodoistClient.IsTodoist(new Uri("https://api.todoist.com/api/v1/sync")));
        Assert.False(TodoistClient.IsTodoist(new Uri("http://api.todoist.com/api/v1/sync")));
        Assert.False(TodoistClient.IsTodoist(new Uri("https://api.todoist.com:8443/api/v1/sync")));
        Assert.False(TodoistClient.IsTodoist(new Uri("https://api.todoist.com.evil.example/api/v1/sync")));
        Assert.False(TodoistClient.IsTodoist(new Uri("https://todoist.com/api/v1/sync")));
        Assert.False(TodoistClient.IsTodoist(null));
    }

    [Fact]
    public void Token_is_hidden_from_ToString_and_bearer_prefix_is_stripped()
    {
        var token = new TodoistToken("  Bearer " + FakeTodoist.Token + "\n");
        Assert.DoesNotContain(FakeTodoist.Token, token.ToString());
        Assert.Equal(FakeTodoist.Token, token.Value);
        Assert.Throws<ArgumentException>(() => new TodoistToken("  "));
    }

    [Fact]
    public async Task Request_has_only_authorization_and_content_type_headers()
    {
        var captured = new List<HttpRequestMessage>();
        var handler = new CaptureHandler(captured);
        using var http = new HttpClient(handler);
        await new TodoistClient(http, new TodoistToken("t")).ReadAccountAsync(CancellationToken.None);

        var request = Assert.Single(captured);
        Assert.Equal(["Authorization"], request.Headers.Select(h => h.Key));
        Assert.Equal(["Content-Type"], request.Content!.Headers.Where(h => h.Key != "Content-Length").Select(h => h.Key));
        Assert.Equal("application/x-www-form-urlencoded", request.Content.Headers.ContentType!.ToString());
    }

    private sealed class CaptureHandler(List<HttpRequestMessage> captured) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            captured.Add(request);
            return Task.FromResult(FakeTodoist.Json("{}"));
        }
    }

    [Fact]
    public void Uuids_and_temp_ids_are_deterministic_per_transfer_and_distinct()
    {
        var a = new TodoistTransferState { TransferId = "11111111-1111-1111-1111-111111111111" };
        var b = new TodoistTransferState { TransferId = "22222222-2222-2222-2222-222222222222" };

        Assert.Equal(a.Uuid("item_add", "t:x"), a.Uuid("item_add", "t:x"));
        Assert.NotEqual(a.Uuid("item_add", "t:x"), b.Uuid("item_add", "t:x"));
        Assert.NotEqual(a.Uuid("item_add", "t:x"), a.Uuid("item_complete", "t:x"));
        Assert.NotEqual(a.Uuid("item_add", "t:x"), a.TempId("t:x"));
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", a.Uuid("item_add", "t:x"));
        Assert.True(Guid.TryParse(a.TempId("t:x"), out _));
    }

    [Fact]
    public async Task State_round_trips_through_json()
    {
        var fake = new FakeTodoist { RemindersAllowed = true };
        fake.FailContent["Milk"] = "X";
        var run = await Transfer(Rich(), fake, includeCompleted: true, lists: ["Work", "Home"]);

        var bytes = run.State.ToUtf8Bytes();
        var copy = TodoistTransferState.Parse(bytes);
        Assert.Equal(bytes, copy.ToUtf8Bytes());
        Assert.Equal(run.State.Settings with { Lists = null }, copy.Settings with { Lists = null });
        Assert.Equal(["Work", "Home"], copy.Settings.Lists);
        Assert.Throws<InvalidDataException>(() => TodoistTransferState.Parse(Encoding.UTF8.GetBytes("""{"format":"other"}""")));
    }

    [Fact]
    public async Task Hundred_and_one_commands_are_refused_by_the_fake_like_the_real_api()
    {
        var fake = new FakeTodoist();
        var commands = new JsonArray(Enumerable.Range(0, 101).Select(i => (JsonNode)new JsonObject
        {
            ["type"] = "project_add",
            ["uuid"] = Guid.NewGuid().ToString(),
            ["temp_id"] = Guid.NewGuid().ToString(),
            ["args"] = new JsonObject { ["name"] = $"P{i}" },
        }).ToArray());
        using var http = new HttpClient(fake);
        var request = new HttpRequestMessage(HttpMethod.Post, TodoistClient.SyncUrl)
        {
            Content = new FormUrlEncodedContent([new("commands", commands.ToJsonString())]),
        };
        request.Headers.Authorization = new("Bearer", FakeTodoist.Token);
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("LIMITS_REACHED_COMMANDS", await response.Content.ReadAsStringAsync());
        Assert.Single(fake.Projects);
    }

    [Fact]
    public void Csv_and_api_share_the_mapping_of_content_description_labels_and_dates()
    {
        var archive = Rich();
        var plan = TodoistPlanner.Build(archive, new TodoistPlanSettings { TimeZoneId = "Europe/Belgrade", PlannedAt = Now }, null);
        var csv = TodoistCsvExporter.Export(archive, false);
        var work = csv[0].GetText().Split("\r\n");
        var weekly = plan.Projects[0].Items.Single(i => i.TaskTitle == "Weekly" && !i.IsSubTask);
        Assert.Equal("task,Weekly,,4,1,,,every mon starting 2026-11-23,en,,,None", work[4]);
        Assert.Equal("every mon starting 2026-11-23", weekly.DueString);
        Assert.Equal(work.Length - 2, plan.Projects[0].Items.Count); // header and trailing empty line
    }

    [Fact]
    public void Partial_archive_and_partial_tasks_give_a_warning_and_a_loss()
    {
        var archive = TestData.Archive((TestData.List("L"), [TestData.Task("Half", """{ "todoExitPartial": true }""")])) with
        {
            ReadNotes = new ReadNotes([]) { Interrupted = true, InterruptedReason = "token expired" },
        };
        var plan = TodoistPlanner.Build(archive, new TodoistPlanSettings { PlannedAt = Now }, null);

        Assert.True(plan.CanStart);
        Assert.Contains(plan.Warnings, w => w.Contains("archive is incomplete") && w.Contains("token expired"));
        Assert.Contains(plan.Warnings, w => w.Contains("partly readable"));
        Assert.Contains(plan.Losses, l => l.Kind == LossKinds.PartialTask && l.Task == "Half");
    }

    [Fact]
    public async Task Cancellation_stops_between_requests_with_state_saved_and_resume_finishes()
    {
        var fake = new FakeTodoist { MaxTasks = 1000 };
        var archive = Big(60, 1);
        using var http = new HttpClient(fake, disposeHandler: false);
        var client = new TodoistClient(http, new TodoistToken(FakeTodoist.Token));
        var account = await client.ReadAccountAsync(CancellationToken.None);
        var plan = TodoistPlanner.Build(archive, new TodoistPlanSettings { MaxTasksPerProject = 1000, PlannedAt = Now }, account);
        var state = TodoistTransfer.NewState(plan, "f", Now);
        using var cts = new CancellationTokenSource();
        byte[]? saved = null;
        var result = await TodoistTransfer.RunAsync(plan, state, client, (s, _) =>
        {
            saved = s.ToUtf8Bytes();
            if (fake.WriteRequests == 1 && s.Pending.Count == 0)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        }, null, cts.Token);

        Assert.Equal(TransferStatus.Interrupted, result.Status);
        Assert.Equal(1, fake.WriteRequests);
        var resumed = TodoistTransferState.Parse(saved!);
        Assert.Equal(100, resumed.Done.Count);

        var second = await TodoistTransfer.RunAsync(TodoistPlanner.Build(archive, resumed.Settings, account, resumed), resumed, client,
            (_, _) => Task.CompletedTask, null, CancellationToken.None);
        Assert.Equal(TransferStatus.Completed, second.Status);
        AssertEachOnce(fake, 60, 1);
    }

    [Fact]
    public async Task Progress_reports_commands_and_waits()
    {
        var fake = new FakeTodoist { Throttle = 1 };
        using var http = new HttpClient(fake, disposeHandler: false);
        var client = new TodoistClient(http, new TodoistToken(FakeTodoist.Token), (_, _) => Task.CompletedTask);
        var account = await client.ReadAccountAsync(CancellationToken.None);
        var plan = TodoistPlanner.Build(Rich(), new TodoistPlanSettings { PlannedAt = Now }, account);
        var reports = new List<TransferProgress>();
        await TodoistTransfer.RunAsync(plan, TodoistTransfer.NewState(plan, "f", Now), client, (_, _) => Task.CompletedTask,
            new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(new TransferProgress(0, 7, null), reports[0]);
        Assert.Contains(reports, r => r.Waiting == TimeSpan.FromSeconds(7) && r.WaitingReason!.Contains("429"));
        Assert.Equal(7, reports[^1].CommandsDone);
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}

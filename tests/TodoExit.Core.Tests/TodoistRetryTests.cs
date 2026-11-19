using System.Text.Json.Nodes;
using TodoExit.Cli;
using TodoExit.Core.Archive;
using TodoExit.Core.Export;
using TodoExit.Core.Model;
using TodoExit.Core.Todoist;

namespace TodoExit.Core.Tests;

/// <summary>
/// Per-command errors inside an HTTP 200: a temporary one (http_code 408, 429 or 5xx) is retried with a wait and a new
/// uuid, a few times per run; if it still fails the transfer is interrupted, not completed, with the command and what
/// depends on it left for a resumed run. A refusal (4xx) is reported and the rest continues. Nothing is created twice,
/// also when Todoist ran a command and still answered it with an error. Also: id recovery after a lost response reads
/// what changed since a sync_token taken before the first write, so a full sync that lags behind does not lose tasks.
/// </summary>
public class TodoistRetryTests
{
    internal sealed class Run
    {
        public required TodoistPlan Plan { get; init; }

        public required TodoistTransferState State { get; init; }

        public required TodoistTransferResult Result { get; init; }

        public List<TimeSpan> Delays { get; } = new();

        public List<string> Waits { get; } = new();

        public byte[] Saved { get; set; } = [];
    }

    internal static async Task<Run> Transfer(
        TodoArchive archive, FakeTodoist fake, bool includeCompleted = false, TodoistTransferState? state = null, int maxAttempts = 6)
    {
        var delays = new List<TimeSpan>();
        var waits = new List<string>();
        byte[] saved = [];
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
            TimeZoneId = "Europe/Belgrade",
            MaxTasksPerProject = account.MaxTasks ?? 300,
            Reminders = account.Reminders,
            PlannedAt = TodoistTransferTests.Now,
        };
        var plan = TodoistPlanner.Build(archive, settings, account, state);
        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));
        state ??= TodoistTransfer.NewState(plan, "fingerprint", TodoistTransferTests.Now);
        var result = await TodoistTransfer.RunAsync(plan, state, client, (s, _) =>
        {
            saved = s.ToUtf8Bytes();
            return Task.CompletedTask;
        }, new Progress(p =>
        {
            if (p.WaitingReason is { } reason)
            {
                waits.Add(reason);
            }
        }), CancellationToken.None);
        var run = new Run { Plan = plan, State = state, Result = result, Saved = saved };
        run.Delays.AddRange(delays);
        run.Waits.AddRange(waits);
        return run;
    }

    private sealed class Progress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    /// <summary>Resumes from the state file the run saved last, as the CLI does.</summary>
    private static Task<Run> Resume(TodoArchive archive, FakeTodoist fake, Run previous) =>
        Transfer(archive, fake, state: TodoistTransferState.Parse(previous.Saved));

    internal static TodoArchive Big(int tasks, int steps, string name = "Big") =>
        TestData.Archive((TestData.List(name), Enumerable.Range(0, tasks).Select(i => TestData.Task($"{name} {i}", $$"""
            { "id": "{{name}}-{{i}}", "checklistItems": [ {{string.Join(",", Enumerable.Range(0, steps).Select(s => $$"""{ "displayName": "step {{i}}.{{s}}" }"""))}} ] }
            """)).ToArray()));

    internal static void AssertEachOnce(FakeTodoist fake, int tasks, int steps, string name = "Big")
    {
        var project = Assert.Single(fake.Projects, p => p.Name == name);
        for (var i = 0; i < tasks; i++)
        {
            var task = Assert.Single(fake.Items, it => it.Content == $"{name} {i}");
            Assert.Equal(project.Id, task.ProjectId);
            for (var s = 0; s < steps; s++)
            {
                Assert.Equal(task.Id, Assert.Single(fake.Items, it => it.Content == $"step {i}.{s}").ParentId);
            }
        }

        Assert.Equal(tasks * (1 + steps), fake.Items.Count);
    }

    private static List<JsonObject> Sent(FakeTodoist fake, string type, string content) =>
        fake.Writes.SelectMany(w => w.Commands!).Select(c => c!.AsObject())
            .Where(c => (string)c["type"]! == type && (string?)c["args"]!["content"] == content).ToList();

    // ------------------------------------------------------------------ fix 6: temporary errors of single commands

    [Fact]
    public async Task Temporary_error_of_one_task_interrupts_and_resume_creates_it_exactly_once()
    {
        // The reviewer's case: project_add works, the item_add answers http_code 503. Before, the task was marked done,
        // the transfer said Completed, and a resume sent nothing.
        var fake = new FakeTodoist();
        fake.Transient["item_add:Milk"] = 100;
        var first = await Transfer(TodoistTransferTests.Rich(), fake);

        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);
        Assert.Contains("failed 1 command 5 times in a row (HTTP 503 SERVICE_UNAVAILABLE", first.Result.Message);
        Assert.False(first.State.Finished);
        Assert.Single(fake.Projects, p => p.Name == "Home");
        Assert.DoesNotContain(fake.Items, i => i.Content == "Milk");
        Assert.DoesNotContain(first.State.Errors, e => e.LossKind == LossKinds.TodoistError);
        Assert.Equal(5, Sent(fake, "item_add", "Milk").Count); // 1 + 4 retries in this run
        Assert.Equal(5, Sent(fake, "item_add", "Milk").Select(c => (string)c["uuid"]!).Distinct().Count());
        Assert.Equal(5, Sent(fake, "item_add", "Milk").Select(c => (string)c["temp_id"]!).Distinct().Count());
        Assert.Equal([2, 4, 8, 16], first.Delays.Select(d => d.TotalSeconds));
        Assert.Contains(first.Waits, w => w.Contains("1 command failed for now: HTTP 503", StringComparison.Ordinal));

        // Todoist recovers; the resumed run sends the task once more, with a new uuid, and it is created once.
        fake.Transient.Clear();
        var second = await Resume(TodoistTransferTests.Rich(), fake, first);

        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        Assert.Equal(fake.ProjectNamed("Home").Id, Assert.Single(fake.Items, i => i.Content == "Milk").ProjectId);
        Assert.Equal(6, Sent(fake, "item_add", "Milk").Count);
        Assert.Equal(new TransferCounts(2, 3, 2, 0, 0, 0, 0), TransferCounts.From(second.Plan, second.State));
        Assert.Equal(5, fake.Items.Count);
    }

    [Fact]
    public async Task Temporary_error_that_clears_within_the_run_completes_the_transfer()
    {
        var fake = new FakeTodoist();
        fake.Transient["item_add:Milk"] = 2;
        var run = await Transfer(TodoistTransferTests.Rich(), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.DoesNotContain(run.State.Errors, e => e.LossKind != LossKinds.ReminderNotCarried);
        Assert.Single(fake.Items, i => i.Content == "Milk");
        Assert.Equal([2, 4], run.Delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task Temporary_failure_of_a_parent_holds_its_children_in_the_same_and_later_requests()
    {
        // Big(30, 3): "Big 24" and two of its steps are in request 1, the third step in request 2. The two steps in
        // request 1 fail because their parent's temp_id was not created; they are not refused, they wait.
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.Transient["item_add:Big 24"] = 1;
        var run = await Transfer(Big(30, 3), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Empty(run.State.Errors);
        AssertEachOnce(fake, 30, 3);

        // With the retries used up, the parent and all three steps stay for a resumed run; nothing is skipped.
        fake = new FakeTodoist { MaxTasks = 1000 };
        fake.Transient["item_add:Big 24"] = 100;
        var first = await Transfer(Big(30, 3), fake);

        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);
        Assert.Empty(first.State.Errors);
        Assert.DoesNotContain(fake.Items, i => i.Content is "Big 24" or "step 24.0" or "step 24.1" or "step 24.2");

        // A step is only ever sent in the same request as an attempt of its parent, referring to that attempt's temp_id.
        foreach (var write in fake.Writes.Select(w => w.Commands!.Select(c => c!.AsObject()).ToList()))
        {
            foreach (var step in write.Where(c => ((string?)c["args"]!["content"])?.StartsWith("step 24.", StringComparison.Ordinal) == true))
            {
                var parent = Assert.Single(write, c => (string?)c["args"]!["content"] == "Big 24");
                Assert.Equal((string)parent["temp_id"]!, (string)step["args"]!["parent_id"]!);
            }
        }
        Assert.Equal(29 * 4, fake.Items.Count);
        var saved = TodoistTransferState.Parse(first.Saved);
        Assert.Equal(4, first.Plan.Projects[0].Items.Count(i => i.TaskTitle == "Big 24" && !saved.Done.Contains(saved.Uuid("item_add", i.Key))));

        fake.Transient.Clear();
        var second = await Resume(Big(30, 3), fake, first);
        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        Assert.Empty(second.State.Errors);
        AssertEachOnce(fake, 30, 3);
    }

    [Fact]
    public async Task Temporary_failure_of_project_add_holds_the_tasks_of_that_project()
    {
        var fake = new FakeTodoist();
        fake.Transient["project_add:Work"] = 1;
        var run = await Transfer(TodoistTransferTests.Rich(), fake);
        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.DoesNotContain(run.State.Errors, e => e.LossKind != LossKinds.ReminderNotCarried);
        Assert.Single(fake.Projects, p => p.Name == "Work");
        Assert.Equal(4, fake.ItemsIn("Work").Count);

        fake = new FakeTodoist();
        fake.Transient["project_add:Work"] = 100;
        var first = await Transfer(TodoistTransferTests.Rich(), fake);

        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);
        Assert.DoesNotContain(fake.Projects, p => p.Name == "Work");
        Assert.Equal(["Milk"], fake.Items.Select(i => i.Content)); // the other project went on
        Assert.DoesNotContain(first.State.Errors, e => e.LossKind is LossKinds.TodoistError or LossKinds.ParentNotCreated);

        fake.Transient.Clear();
        var second = await Resume(TodoistTransferTests.Rich(), fake, first);
        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        Assert.Single(fake.Projects, p => p.Name == "Work");
        Assert.Equal(["Report", "a", "b", "Weekly"], fake.ItemsIn("Work").Select(i => i.Content));
        Assert.Equal(5, fake.Items.Count);
    }

    [Fact]
    public async Task Refusal_is_reported_and_does_not_block_the_rest_while_a_temporary_error_is_retried()
    {
        var fake = new FakeTodoist();
        fake.FailContent["Report"] = "INVALID_ARGUMENT_VALUE";
        fake.Transient["item_add:Milk"] = 1;
        var run = await Transfer(TodoistTransferTests.Rich(), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Equal("INVALID_ARGUMENT_VALUE", Assert.Single(run.State.Errors, e => e.LossKind == LossKinds.TodoistError).Tag);
        Assert.Equal(2, run.State.Errors.Count(e => e.LossKind == LossKinds.ParentNotCreated));
        Assert.Single(Sent(fake, "item_add", "Report")); // a refusal is not retried
        Assert.Single(fake.Items, i => i.Content == "Milk");
        Assert.Single(fake.Items, i => i.Content == "Weekly");
    }

    [Fact]
    public async Task Commands_that_ran_but_were_answered_with_an_error_are_not_created_twice()
    {
        // Todoist ran each of these and still answered http_code 503 without a temp_id_mapping. Before sending a command
        // again with a new uuid, the transfer reads what changed and takes the object it finds.
        var fake = new FakeTodoist { RemindersAllowed = true };
        fake.AppliedButFailed["item_add:Milk"] = 1;
        fake.AppliedButFailed["item_add:Report"] = 1; // its steps in the same request still find it by temp_id
        fake.AppliedButFailed["project_add:Home"] = 1;
        fake.AppliedButFailed["item_complete:Done"] = 1;
        fake.AppliedButFailed["reminder_add:Report"] = 1;
        var run = await Transfer(TodoistTransferTests.Rich(), fake, includeCompleted: true);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Empty(run.State.Errors);
        Assert.Single(fake.Projects, p => p.Name == "Home");
        var milk = Assert.Single(fake.Items, i => i.Content == "Milk");
        Assert.Equal(fake.ProjectNamed("Home").Id, milk.ProjectId);
        var report = Assert.Single(fake.Items, i => i.Content == "Report");
        Assert.Equal(["a", "b", "c"], fake.Items.Where(i => i.ParentId == report.Id).Select(i => i.Content));
        Assert.True(Assert.Single(fake.Items, i => i.Content == "Done").Checked);
        Assert.Equal(report.Id, Assert.Single(fake.Reminders).ItemId);
        Assert.Equal(8, fake.Items.Count);
        Assert.Single(Sent(fake, "item_add", "Milk"));
        Assert.Equal(milk.Id, run.State.Ids[run.Plan.Projects.Single(p => p.Name == "Home").Items[0].Key]);
        Assert.Equal(new TransferCounts(2, 4, 4, 3, 1, 0, 0), TransferCounts.From(run.Plan, run.State));
    }

    [Fact]
    public async Task Cli_exits_3_with_the_state_saved_when_a_command_keeps_failing_and_resume_finishes()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "transfer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var archive = Path.Combine(dir, "archive.json");
        File.WriteAllBytes(archive, TestData.Document(TodoistTransferTests.RichLists()).ToUtf8Bytes());
        var outDir = Path.Combine(dir, "out");
        var fake = new FakeTodoist();
        fake.Transient["item_add:Milk"] = 100;

        async Task<(int, string, string)> Cli(params string[] args)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = await Program.RunAsync(args, stdout, stderr, fake, CancellationToken.None, (_, _) => Task.CompletedTask,
                clock: () => TodoistTransferTests.Now, environment: _ => null);
            return (code, stdout.ToString(), stderr.ToString());
        }

        var (code, _, err) = await Cli("transfer", "--from-archive", archive, "--out", outDir, "--todoist-token", FakeTodoist.Token, "--yes");
        Assert.Equal(Program.ExitInterrupted, code);
        var statePath = Path.Combine(outDir, TransferCommand.StateFileName);
        Assert.True(File.Exists(statePath));
        Assert.Contains("Continue with: todo-exit transfer", err);
        Assert.Contains("not done yet", err);
        Assert.Contains("The transfer was interrupted.", File.ReadAllText(Path.Combine(outDir, TodoistTransferReport.MarkdownFileName)));

        fake.Transient.Clear();
        (code, _, _) = await Cli("transfer", "--from-archive", archive, "--resume", statePath, "--todoist-token", FakeTodoist.Token, "--yes");
        Assert.Equal(Program.ExitOk, code);
        Assert.Single(fake.Items, i => i.Content == "Milk");
    }

    [Fact]
    public async Task Web_session_offers_continue_when_a_command_keeps_failing()
    {
        var fake = new FakeTodoist();
        fake.Transient["item_add:Work 1"] = 100;
        var document = TestData.Document((TestData.List("Work"),
            Enumerable.Range(0, 3).Select(i => TestData.Task($"Work {i}", $$"""{ "id": "w{{i}}" }""")).ToArray()));
        var workspace = new ArchiveWorkspace(() => new HttpClient(fake, disposeHandler: false), (_, _) => Task.CompletedTask);
        Assert.True(workspace.TrySetArchive(document, ArchiveParser.Parse(document, TimeZoneInfo.Utc)));
        var session = workspace.Session!;
        Assert.NotNull(await session.PlanAsync(new TodoistToken(FakeTodoist.Token), null, false, null, CancellationToken.None));

        var first = await session.RunAsync(new TodoistToken(FakeTodoist.Token), new Progress(_ => { }), CancellationToken.None);
        Assert.Equal(TransferStatus.Interrupted, first.Status);
        Assert.True(session.CanContinue);
        Assert.False(session.Finished);

        fake.Transient.Clear();
        var second = await session.RunAsync(new TodoistToken(FakeTodoist.Token), new Progress(_ => { }), CancellationToken.None);
        Assert.Equal(TransferStatus.Completed, second.Status);
        Assert.Equal(["Work 0", "Work 2", "Work 1"], fake.ItemsIn("Work").Select(i => i.Content));
    }

    [Fact]
    public void Temporary_means_http_code_408_429_or_5xx()
    {
        Assert.True(new TodoistCommandResult(false, HttpCode: 503).IsTemporary);
        Assert.True(new TodoistCommandResult(false, HttpCode: 500).IsTemporary);
        Assert.True(new TodoistCommandResult(false, HttpCode: 429).IsTemporary);
        Assert.True(new TodoistCommandResult(false, HttpCode: 408).IsTemporary);
        Assert.False(new TodoistCommandResult(false, "PREMIUM_ONLY", 32, "Premium only", 403).IsTemporary);
        Assert.False(new TodoistCommandResult(false, "INVALID_ARGUMENT_VALUE", 20, "x", 400).IsTemporary);
        Assert.False(new TodoistCommandResult(false, "UNKNOWN").IsTemporary);
        Assert.False(TodoistCommandResult.Success.IsTemporary);
    }

    [Fact]
    public async Task State_of_the_previous_version_resumes_and_new_fields_round_trip()
    {
        var fake = new FakeTodoist();
        fake.Transient["item_add:Milk"] = 100;
        var first = await Transfer(TodoistTransferTests.Rich(), fake);
        var json = JsonNode.Parse(first.Saved)!.AsObject();
        Assert.NotNull((string?)json["syncToken"]);
        Assert.Equal(5, (int)json["attempts"]!.AsObject().Single().Value!);
        var copy = TodoistTransferState.Parse(first.Saved);
        Assert.Equal(first.Saved, copy.ToUtf8Bytes());

        // A state written before these fields existed: no token, no attempts.
        fake = new FakeTodoist { MaxTasks = 1000 };
        fake.LoseResponse.Add(2);
        var archive = Big(120, 3);
        var interrupted = await Transfer(archive, fake, maxAttempts: 1);
        Assert.Equal(TransferStatus.Interrupted, interrupted.Result.Status);
        var old = JsonNode.Parse(interrupted.Saved)!.AsObject();
        old.Remove("syncToken");
        old.Remove("syncTokenDate");
        old.Remove("attempts");
        var oldState = TodoistTransferState.Parse(System.Text.Encoding.UTF8.GetBytes(old.ToJsonString()));
        Assert.Null(oldState.SyncToken);

        var resumed = await Transfer(archive, fake, state: oldState);
        Assert.Equal(TransferStatus.Completed, resumed.Result.Status);
        Assert.Empty(resumed.State.Errors);
        AssertEachOnce(fake, 120, 3);
        Assert.Null(resumed.State.SyncToken); // a token taken after writes would hide earlier objects; it is not taken
    }

    // ------------------------------------------------------------------ fix 7: recovery with incremental sync

    [Fact]
    public async Task Lost_response_with_a_stale_full_sync_creates_every_task_exactly_once()
    {
        // The reviewer's case: 150 tasks, the first response is lost, and a full sync still shows the account as it
        // was before the transfer. Before, 99 of 150 tasks were created and 51 were skipped, and a resume sent nothing.
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.FreezeFullSync();
        fake.LoseResponse.Add(1);
        var run = await Transfer(Big(150, 0), fake);

        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Empty(run.State.Errors);
        AssertEachOnce(fake, 150, 0);
        Assert.Empty(run.State.Unresolved);
        Assert.Equal(151, run.State.Ids.Count);
        Assert.All(run.State.Ids.Values, id => Assert.False(string.IsNullOrEmpty(id)));

        // The recovery read asked for the changes since the token taken before the first write.
        var token = run.State.SyncToken!;
        var tokenRead = fake.Requests.FindIndex(r => !r.Form.ContainsKey("commands") && r.Form["resource_types"] == """["projects","items"]""");
        Assert.True(tokenRead < fake.Requests.FindIndex(r => r.Form.ContainsKey("commands")));
        Assert.Contains(fake.Requests, r => r.Form.TryGetValue("sync_token", out var t) && t == token);
    }

    [Fact]
    public async Task Item_not_found_yet_stays_unresolved_its_children_wait_and_resume_finishes()
    {
        // Big(60, 1): request 1 holds the project and the first 49.5 tasks with their steps. Its response is lost and
        // "Big 10" is not visible in any read yet.
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.LoseResponse.Add(1);
        fake.HiddenFromReads.Add("Big 10");
        var first = await Transfer(Big(60, 1), fake);

        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);
        Assert.Contains("Todoist confirmed 2 items, but they were not found in the account yet", first.Result.Message);
        Assert.Empty(first.State.Errors);
        Assert.Equal(["s:Big-10/0", "t:Big-10"], first.State.Unresolved.Order(StringComparer.Ordinal)); // the step's parent has no id
        Assert.Single(fake.Items, i => i.Content == "Big 10");
        Assert.Single(fake.Items, i => i.Content == "step 10.0"); // it was in the lost request, so it exists
        Assert.Equal(120, fake.Items.Count);
        Assert.Equal([2, 2, 4, 8, 16, 32], first.Delays.Select(d => d.TotalSeconds)); // the lost request, then patient searches
        Assert.Contains(first.Waits, w => w.Contains("looking for 2 created items", StringComparison.Ordinal));

        fake.HiddenFromReads.Clear();
        var second = await Resume(Big(60, 1), fake, first);
        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        Assert.Empty(second.State.Errors);
        Assert.Empty(second.State.Unresolved);
        AssertEachOnce(fake, 60, 1);
    }

    [Fact]
    public async Task Children_of_an_item_not_found_yet_are_not_sent_until_it_is_found()
    {
        // Big(30, 3): request 1 ends with "Big 24" and two of its steps; the third step is in request 2. With
        // request 1 lost and "Big 24" not visible, the third step must not be sent with a guessed parent.
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.LoseResponse.Add(1);
        fake.HiddenFromReads.Add("Big 24");
        var first = await Transfer(Big(30, 3), fake);

        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);
        Assert.Empty(first.State.Errors);
        Assert.Empty(Sent(fake, "item_add", "step 24.2"));
        Assert.Single(fake.Items, i => i.Content == "Big 25"); // independent tasks went on

        fake.HiddenFromReads.Clear();
        var second = await Resume(Big(30, 3), fake, first);
        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        AssertEachOnce(fake, 30, 3);
    }

    [Fact]
    public async Task Old_state_without_a_token_recovers_through_a_full_sync_and_the_changes_after_it()
    {
        var fake = new FakeTodoist { MaxTasks = 1000 };
        fake.LoseResponse.Add(1);
        var first = await Transfer(Big(150, 0), fake, maxAttempts: 1);
        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);
        Assert.Equal(100, TodoistTransferState.Parse(first.Saved).Pending.Count);

        // Saved by the previous version: no token. Meanwhile a full sync lags behind the transfer's first request.
        var json = JsonNode.Parse(first.Saved)!.AsObject();
        json.Remove("syncToken");
        json.Remove("syncTokenDate");
        var state = TodoistTransferState.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        fake.FullSyncFrozenAt = fake.Projects.Single(p => p.Name == "Big").Created - 1;

        var second = await Transfer(Big(150, 0), fake, state: state);
        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        Assert.Empty(second.State.Errors);
        AssertEachOnce(fake, 150, 0);
        var reads = fake.Requests.Where(r => r.Form.TryGetValue("resource_types", out var t) && t == """["projects","items"]""").ToList();
        Assert.Contains(reads, r => r.Form["sync_token"] == "*");
        Assert.Contains(reads, r => r.Form["sync_token"] != "*");
    }

    // ------------------------------------------------------------------ fix 9: team projects

    private static async Task<TodoistAccount> Account(FakeTodoist fake)
    {
        using var http = new HttpClient(fake, disposeHandler: false);
        return await new TodoistClient(http, new TodoistToken(FakeTodoist.Token)).ReadAccountAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Team_projects_do_not_count_against_the_personal_project_limit()
    {
        // The reviewer's case: a personal limit of 5, no personal projects, 5 team projects.
        var fake = new FakeTodoist { MaxProjects = 5 };
        for (var i = 0; i < 5; i++)
        {
            fake.AddTeamProject($"Team {i}");
        }

        var account = await Account(fake);
        Assert.Equal(0, account.CountedProjects);
        Assert.Equal(5, account.TeamProjects);
        var plan = TodoistPlanner.Build(TodoistTransferTests.Rich(), new TodoistPlanSettings { PlannedAt = TodoistTransferTests.Now }, account);
        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));
        Assert.Equal(5, plan.ProjectsAvailable);
        Assert.Contains("Team projects: 5.", TransferCommand.FormatPlan(plan, null));

        var run = await Transfer(TodoistTransferTests.Rich(), fake);
        Assert.Equal(TransferStatus.Completed, run.Result.Status);
        Assert.Null(fake.ProjectNamed("Work").WorkspaceId);
    }

    [Fact]
    public async Task Only_personal_projects_count_in_a_mix_and_a_null_workspace_id_is_personal()
    {
        var fake = new FakeTodoist { MaxProjects = 5 };
        for (var i = 0; i < 3; i++)
        {
            fake.AddProject($"Mine {i}");
            fake.AddTeamProject($"Team {i}", workspaceId: $"ws-{i}");
        }

        fake.AddTeamProject("Team 3");
        var plan = TodoistPlanner.Build(TodoistTransferTests.Rich(), new TodoistPlanSettings { PlannedAt = TodoistTransferTests.Now }, await Account(fake));
        Assert.Equal(2, plan.ProjectsAvailable);
        Assert.True(plan.CanStart);

        fake.Projects.Add(new FakeTodoist.Project { Id = "p-null", Name = "Null workspace", NullWorkspaceKey = true });
        var account = await Account(fake);
        Assert.Null(account.Projects.Single(p => p.Id == "p-null").WorkspaceId);
        Assert.Equal(4, account.CountedProjects);
        plan = TodoistPlanner.Build(TodoistTransferTests.Rich(), new TodoistPlanSettings { PlannedAt = TodoistTransferTests.Now }, account);
        Assert.Equal(1, plan.ProjectsAvailable);
        Assert.False(plan.CanStart);
        Assert.True(plan.TooManyProjects);
    }

    [Fact]
    public async Task Team_project_with_a_planned_name_gives_a_warning_and_recovery_ignores_team_projects()
    {
        var fake = new FakeTodoist();
        fake.AddTeamProject("Work");
        var plan = TodoistPlanner.Build(TodoistTransferTests.Rich(), new TodoistPlanSettings { PlannedAt = TodoistTransferTests.Now }, await Account(fake));
        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));
        Assert.Empty(plan.NameClashes);
        Assert.Contains(plan.Warnings, w => w.Contains("same name as team projects: \"Work\"", StringComparison.Ordinal));

        // The response to the first request is lost; before the resume, someone creates a team project "Home".
        // Only the personal "Home" this transfer created can be the one it is looking for.
        fake.LoseResponse.Add(1);
        var first = await Transfer(TodoistTransferTests.Rich(), fake, maxAttempts: 1);
        Assert.Equal(TransferStatus.Interrupted, first.Result.Status);
        var team = fake.AddTeamProject("Home");

        var second = await Resume(TodoistTransferTests.Rich(), fake, first);
        Assert.Equal(TransferStatus.Completed, second.Result.Status);
        Assert.DoesNotContain(second.State.Errors, e => e.LossKind != LossKinds.ReminderNotCarried);
        var home = second.Plan.Projects.Single(p => p.Name == "Home");
        Assert.NotEqual(team.Id, second.State.Ids[home.Key]);
        Assert.Null(fake.Projects.Single(p => p.Id == second.State.Ids[home.Key]).WorkspaceId);
        Assert.Equal(2, fake.Projects.Count(p => p.Name == "Work"));
    }
}

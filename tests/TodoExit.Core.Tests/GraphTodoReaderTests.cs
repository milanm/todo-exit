using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TodoExit.Core.Archive;
using TodoExit.Core.Graph;
using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

public class GraphTodoReaderTests
{
    private const string Base = FakeGraph.Base;

    /// <summary>
    /// Most tests here exercise the batch machinery, so by default steps are NOT inline and --verify-steps is on:
    /// steps then travel through $batch exactly as they did before steps were known to come inline.
    /// </summary>
    private static (GraphTodoReader Reader, List<TimeSpan> Delays) Create(FakeGraph graph, bool verifySteps = true)
    {
        if (verifySteps)
        {
            graph.InlineSteps = false;
        }

        var delays = new List<TimeSpan>();
        var reader = new GraphTodoReader(
            new HttpClient(graph),
            (d, _) =>
            {
                lock (delays)
                {
                    delays.Add(d);
                }

                return Task.CompletedTask;
            },
            () => new DateTimeOffset(2026, 11, 19, 13, 30, 0, TimeSpan.Zero))
        {
            VerifySteps = verifySteps,
        };
        return (reader, delays);
    }

    private static JsonArray StepsFor(string name) =>
        new((JsonNode)new JsonObject { ["displayName"] = name, ["isChecked"] = false });

    private static int SubRequestCount(FakeGraph.Request post) => post.Body!["requests"]!.AsArray().Count;

    private static List<string> SubUrls(FakeGraph graph) =>
        graph.Posts.SelectMany(p => p.Body!["requests"]!.AsArray()).Select(r => (string)r!["url"]!).ToList();

    /// <summary>A failed read surfaces as GraphReadInterruptedException; returns the Graph error inside it.</summary>
    private static async Task<GraphRequestException> ReadFailsAsync(GraphTodoReader reader)
    {
        var interrupted = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => reader.ReadAsync());
        Assert.True(interrupted.PartialArchive.ReadNotes.Interrupted);
        return Assert.IsType<GraphRequestException>(interrupted.InnerException);
    }

    private static List<JsonObject> ArchiveTasks(TodoArchiveDocument doc, int list = 0) =>
        doc.Root["lists"]![list]!["tasks"]!.AsArray().Select(n => n!.AsObject()).ToList();

    [Fact]
    public async Task Default_run_reads_steps_inline_and_sends_only_the_page_requests()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "Big", 250, i => new JsonObject
        {
            ["id"] = $"t{i}",
            ["title"] = $"Task {i}",
            ["status"] = i < 3 ? "completed" : "notStarted",
        });
        graph.Steps["t7"] = StepsFor("inline step");

        var (reader, _) = Create(graph, verifySteps: false);
        var progress = new List<ReadProgress>();
        var doc = await reader.ReadAsync(new SyncProgress(progress.Add));

        Assert.Equal(
        [
            $"{Base}me/todo/lists",
            $"{Base}me/todo/lists/L1/tasks?$top=100",
            $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=100",
            $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=200",
        ], graph.Requests.Select(r => r.Url));
        Assert.Empty(graph.Posts);
        Assert.Equal("inline", reader.StepReadMode);
        Assert.False(doc.Root.ContainsKey("readNotes"));

        var tasks = ArchiveTasks(doc);
        Assert.Equal("inline step", (string)tasks[7]["checklistItems"]![0]!["displayName"]!);
        Assert.True(tasks[7].ContainsKey("checklistItems@odata.context"));
        Assert.All(tasks.Where((_, i) => i != 7), t => Assert.Empty(t["checklistItems"]!.AsArray()));
        Assert.DoesNotContain(progress, p => p.Phase == ReadPhase.Steps);
        Assert.Equal([ReadPhase.Lists, ReadPhase.Tasks, ReadPhase.Attachments], progress.Select(p => p.Phase).Distinct());
    }

    [Fact]
    public async Task Verify_steps_reads_only_tasks_without_inline_steps_and_counts_what_it_finds()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 5);
        graph.Steps["L1-t1"] = StepsFor("inline");
        graph.Steps["L1-t3"] = StepsFor("hidden");
        graph.Intercept = null;

        var reader = new GraphTodoReader(new HttpClient(graph), (_, _) => Task.CompletedTask) { VerifySteps = true };
        // t3's steps are not inline on this "account": model it by removing them from the page only.
        graph.InlineSteps = true;
        var hidden = graph.Steps["L1-t3"];
        graph.Steps.Remove("L1-t3");
        graph.Intercept = (r, _) =>
        {
            if (r.Method == "POST")
            {
                graph.Steps["L1-t3"] = hidden; // the endpoint knows them
            }

            return null;
        };

        var doc = await reader.ReadAsync();

        var stepUrls = SubUrls(graph).Where(u => u.EndsWith("/checklistItems", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, stepUrls.Count);
        Assert.DoesNotContain("/me/todo/lists/L1/tasks/L1-t1/checklistItems", stepUrls);
        Assert.Equal(1, reader.StepsFoundByVerification);
        Assert.Equal(1, doc.ReadNotes.StepsFoundByVerification);
        Assert.Equal(1, (int)doc.Root["readNotes"]!["stepsFoundByVerification"]!);
        Assert.Equal("hidden", (string)ArchiveTasks(doc)[3]["checklistItems"]![0]!["displayName"]!);
        Assert.Equal("inline", (string)ArchiveTasks(doc)[1]["checklistItems"]![0]!["displayName"]!);
    }

    [Fact]
    public async Task Request_sequence_for_250_tasks_with_verify_steps_uses_13_batches()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "Big", 250, i => new JsonObject
        {
            ["id"] = $"t{i}",
            ["title"] = $"Task {i}",
            ["status"] = i < 3 ? "completed" : "notStarted",
        });

        var (reader, _) = Create(graph);
        var progress = new List<ReadProgress>();
        var doc = await reader.ReadAsync(new SyncProgress(progress.Add));

        var gets = graph.Requests.Where(r => r.Method == "GET").Select(r => r.Url).ToList();
        Assert.Equal(
        [
            $"{Base}me/todo/lists",
            $"{Base}me/todo/lists/L1/tasks?$top=100",
            $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=100",
            $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=200",
        ], gets);
        Assert.Equal(13, graph.Posts.Count());
        Assert.All(graph.Posts, p => Assert.Equal($"{Base}$batch", p.Url));
        Assert.Equal(250, graph.Posts.Sum(SubRequestCount));
        Assert.DoesNotContain(graph.Requests, r => r.Url.Contains("$expand"));
        Assert.Equal("batch", reader.StepReadMode);
        Assert.Equal(250, ArchiveTasks(doc).Count);
        Assert.False(doc.Root.ContainsKey("readNotes"));

        Assert.Contains(progress, p => p.Phase == ReadPhase.Steps && p.Total == 250 && p.Done == 250);
        Assert.Contains(progress, p => p.Phase == ReadPhase.Tasks && p.TasksRead == 250);
    }

    [Fact]
    public async Task Batches_of_20_and_steps_land_on_the_right_tasks_when_answers_are_out_of_order()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 45);
        for (var i = 0; i < 45; i++)
        {
            graph.Steps[$"L1-t{i}"] = StepsFor($"step of {i}");
        }

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Equal([20, 20, 5], graph.Posts.Select(SubRequestCount).OrderByDescending(x => x));
        Assert.All(graph.Posts.SelectMany(p => p.Body!["requests"]!.AsArray()), r =>
        {
            Assert.Equal("GET", (string)r!["method"]!);
            Assert.Matches("^/me/todo/lists/L1/tasks/L1-t\\d+/checklistItems$", (string)r["url"]!);
        });

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        for (var i = 0; i < 45; i++)
        {
            Assert.Equal($"step of {i}", Assert.Single(archive.Lists[0].Tasks[i].Steps).Title);
        }
    }

    [Fact]
    public async Task Throttled_sub_responses_are_retried_in_a_new_batch_after_the_longest_retry_after()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 20);
        foreach (var (id, ra) in new[] { ("L1-t2", "3"), ("L1-t5", "9"), ("L1-t7", "1") })
        {
            graph.ThrottleOnce.Add(id);
            graph.RetryAfterFor[id] = ra;
        }

        graph.Steps["L1-t5"] = StepsFor("five");

        var (reader, delays) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Equal([TimeSpan.FromSeconds(9)], delays);
        var posts = graph.Posts.ToList();
        Assert.Equal(2, posts.Count);
        var retry = posts[1].Body!["requests"]!.AsArray().Select(r => (string)r!["url"]!).ToList();
        Assert.Equal(
        [
            "/me/todo/lists/L1/tasks/L1-t2/checklistItems",
            "/me/todo/lists/L1/tasks/L1-t5/checklistItems",
            "/me/todo/lists/L1/tasks/L1-t7/checklistItems",
        ], retry);
        Assert.Equal("five", ArchiveParser.Parse(doc, TimeZoneInfo.Utc).Lists[0].Tasks[5].Steps.Single().Title);
        Assert.Empty(doc.ReadNotes.Errors);
    }

    [Fact]
    public async Task Batch_shrinks_to_four_after_a_throttle_and_grows_back_after_five_clean_batches()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 60);
        graph.ThrottleOnce.Add("L1-t0");

        var (reader, _) = Create(graph);
        await reader.ReadAsync();

        Assert.Equal([20, 4, 4, 4, 4, 4, 20, 1], reader.BatchSizesSent);
        Assert.Equal(reader.BatchSizesSent, graph.Posts.Select(SubRequestCount));
        Assert.Equal("/me/todo/lists/L1/tasks/L1-t0/checklistItems", (string)graph.Posts.ElementAt(1).Body!["requests"]![0]!["url"]!);
    }

    [Fact]
    public async Task Ten_throttle_rounds_then_steps_unreadable()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 3);
        graph.ThrottleTimes["L1-t1"] = 100;
        graph.RetryAfterFor["L1-t1"] = "1";
        graph.Steps["L1-t0"] = StepsFor("ok");

        var (reader, delays) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Equal(GraphTodoReader.MaxThrottleRounds, graph.Posts.Count());
        Assert.Equal(new[] { 2, 4, 8, 16, 32, 64, 128, 128, 128 }.Select(x => TimeSpan.FromSeconds(x)), delays);
        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.StepsUnreadable, "L1-t1"), (error.Kind, error.TaskId!));
        Assert.Contains("10 attempts", error.Message);
        Assert.Equal("ok", ArchiveParser.Parse(doc, TimeZoneInfo.Utc).Lists[0].Tasks[0].Steps.Single().Title);
    }

    [Fact]
    public async Task A_single_throttle_wait_is_capped_at_five_minutes()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 2);
        graph.ThrottleOnce.Add("L1-t0");
        graph.RetryAfterFor["L1-t0"] = "900";

        var (reader, delays) = Create(graph);
        await reader.ReadAsync();

        Assert.Equal([TimeSpan.FromMinutes(5)], delays);
        Assert.Equal(TimeSpan.FromMinutes(5), GraphTodoReader.ThrottleDelay(TimeSpan.FromHours(1), 0));
        Assert.Equal(TimeSpan.FromSeconds(30), GraphTodoReader.ThrottleDelay(TimeSpan.FromSeconds(30), 0));
        Assert.Equal(TimeSpan.FromSeconds(8), GraphTodoReader.ThrottleDelay(TimeSpan.FromSeconds(1), 2));
    }

    [Fact]
    public async Task Progress_reports_the_throttle_wait()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 2);
        graph.ThrottleOnce.Add("L1-t1");
        graph.RetryAfterFor["L1-t1"] = "30";

        var (reader, _) = Create(graph);
        var progress = new List<ReadProgress>();
        await reader.ReadAsync(new SyncProgress(progress.Add));

        var waiting = Assert.Single(progress, p => p.IsWaiting);
        Assert.Equal(30, waiting.WaitingSeconds);
        Assert.Equal(ReadPhase.Steps, waiting.Phase);
        Assert.Equal("One", waiting.CurrentList);
        var after = progress[progress.IndexOf(waiting) + 1];
        Assert.False(after.IsWaiting);
    }

    [Fact]
    public async Task Throttled_page_get_uses_the_patient_policy()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 1);
        graph.Intercept = (r, _) =>
        {
            if (!r.Url.EndsWith("tasks?$top=100", StringComparison.Ordinal) || graph.Requests.Count(x => x.Url == r.Url) > 7)
            {
                return null;
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(string.Empty) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };

        var (reader, delays) = Create(graph);
        var progress = new List<ReadProgress>();
        var doc = await reader.ReadAsync(new SyncProgress(progress.Add));

        // Seven throttles: more than MaxRetries (5), within MaxThrottleRounds (10).
        Assert.Equal(new[] { 30, 30, 30, 30, 32, 64, 128 }.Select(x => TimeSpan.FromSeconds(x)), delays);
        Assert.Single(ArchiveTasks(doc));
        Assert.Equal(7, progress.Count(p => p.IsWaiting));
    }

    [Fact]
    public async Task Batch_400_falls_back_to_individual_gets_for_the_rest_of_the_run()
    {
        var graph = new FakeGraph { BatchStatus = HttpStatusCode.BadRequest };
        graph.AddList("L1", "One", 3);
        graph.AddList("L2", "Two", 2);
        graph.Steps["L1-t1"] = StepsFor("s");

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Single(graph.Posts);
        foreach (var id in new[] { "L1/tasks/L1-t0", "L1/tasks/L1-t1", "L1/tasks/L1-t2", "L2/tasks/L2-t0", "L2/tasks/L2-t1" })
        {
            Assert.Contains($"{Base}me/todo/lists/{id}/checklistItems", graph.Requests.Select(r => r.Url));
        }

        Assert.Equal("individual", reader.StepReadMode);
        Assert.Equal("s", ArchiveParser.Parse(doc, TimeZoneInfo.Utc).Lists[0].Tasks[1].Steps.Single().Title);
    }

    [Fact]
    public async Task Default_run_sends_no_batch_when_no_task_has_attachments_and_one_for_attachments_otherwise()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 3);
        graph.Steps["L1-t0"] = StepsFor("s");
        var (reader, _) = Create(graph, verifySteps: false);
        await reader.ReadAsync();
        Assert.Empty(graph.Posts);
        Assert.DoesNotContain(graph.Requests, r => r.Url.Contains("checklistItems"));

        var withAttachments = new FakeGraph();
        withAttachments.AddList("L1", "One", 3, i => new JsonObject { ["id"] = $"t{i}", ["title"] = $"T{i}", ["hasAttachments"] = i == 1 });
        withAttachments.Attachments["t1"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "a", ["name"] = "f.png", ["size"] = 3 });
        (reader, _) = Create(withAttachments, verifySteps: false);
        var doc = await reader.ReadAsync();

        Assert.Equal(["/me/todo/lists/L1/tasks/t1/attachments"], SubUrls(withAttachments));
        Assert.Equal("f.png", (string)ArchiveTasks(doc)[1]["attachments"]![0]!["name"]!);
        Assert.Empty(doc.ReadNotes.Errors);
    }

    private static JsonObject RichTask(int i) => new()
    {
        ["@odata.etag"] = $"W/\"etag{i}\"",
        ["importance"] = "high",
        ["isReminderOn"] = false,
        ["status"] = "inProgress",
        ["title"] = $"Task {i}",
        ["createdDateTime"] = "2026-10-01T08:00:00.0000000Z",
        ["lastModifiedDateTime"] = "2026-11-02T09:30:00.0000000Z",
        ["hasAttachments"] = false,
        ["categories"] = new JsonArray("Work", "Blue category"),
        ["id"] = $"t{i}",
        ["body"] = new JsonObject { ["content"] = $"Notes for {i}", ["contentType"] = "text" },
        ["completedDateTime"] = new JsonObject { ["dateTime"] = "2026-11-03T00:00:00.0000000", ["timeZone"] = "UTC" },
        ["dueDateTime"] = new JsonObject { ["dateTime"] = "2026-11-24T23:00:00.0000000", ["timeZone"] = "UTC" },
        ["reminderDateTime"] = new JsonObject { ["dateTime"] = "2026-11-25T08:00:00.0000000", ["timeZone"] = "UTC" },
        ["linkedResources"] = new JsonArray(),
    };

    [Fact]
    public async Task Poisoned_page_is_retried_once_bisected_and_the_bad_task_salvaged_from_its_broken_body()
    {
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "Big", 250, RichTask);
        list.Poisoned[137] = "linked";
        graph.Steps["t137"] = StepsFor("step of the broken task");

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        var urls = graph.Requests.Select(r => r.Url).ToList();
        Assert.Equal(2, urls.Count(u => u == $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=100"));
        Assert.Contains($"{Base}me/todo/lists/L1/tasks?$top=50&$skip=100", urls);
        Assert.Equal(2, urls.Count(u => u == $"{Base}me/todo/lists/L1/tasks?$top=1&$skip=137"));
        Assert.DoesNotContain(graph.Requests, r => r.Url.Contains("$select", StringComparison.OrdinalIgnoreCase));

        var tasks = ArchiveTasks(doc);
        Assert.Equal(Enumerable.Range(0, 250).Select(i => $"t{i}"), tasks.Select(t => (string)t["id"]!));

        var partial = tasks[137];
        Assert.True((bool)partial["todoExitPartial"]!);
        Assert.Equal("Task 137", (string)partial["title"]!);
        Assert.Equal("inProgress", (string)partial["status"]!);
        Assert.Equal("Notes for 137", (string)partial["body"]!["content"]!);
        Assert.Equal("2026-11-24T23:00:00.0000000", (string)partial["dueDateTime"]!["dateTime"]!);
        Assert.Equal("2026-10-01T08:00:00.0000000Z", (string)partial["createdDateTime"]!);
        Assert.Equal(["Work", "Blue category"], partial["categories"]!.AsArray().Select(c => (string)c!));
        Assert.False(partial.ContainsKey("linkedResources@odata.context"));
        Assert.Equal("step of the broken task", (string)partial["checklistItems"]![0]!["displayName"]!);
        Assert.Contains(graph.Posts.SelectMany(p => p.Body!["requests"]!.AsArray()), r => ((string)r!["url"]!).Contains("/t137/"));

        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.TaskPartial, 137, "t137", "Task 137"), (error.Kind, error.Position!.Value, error.TaskId!, error.TaskTitle!));
        Assert.Contains("linkedResources", error.Message);
        Assert.Contains("InternalServerError", error.Message);

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        var item = archive.Lists[0].Tasks[137];
        Assert.True(item.IsPartial);
        Assert.Empty(item.LinkedResources);
        Assert.Equal(new DateOnly(2026, 11, 24), item.DueDate);
        Assert.Equal(1, Inventory.From(archive).Totals.Partial);
    }

    [Fact]
    public async Task Single_task_list_with_a_poisoned_task_is_salvaged()
    {
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "One", 1, RichTask);
        list.Poisoned[0] = "linked";

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        var task = Assert.Single(ArchiveTasks(doc));
        Assert.Equal("t0", (string)task["id"]!);
        Assert.True((bool)task["todoExitPartial"]!);
        Assert.Equal(ReadErrorKinds.TaskPartial, Assert.Single(doc.ReadNotes.Errors).Kind);
        Assert.DoesNotContain(graph.Requests, r => r.Url.Contains("$select"));
    }

    [Fact]
    public async Task Task_that_breaks_before_its_id_is_skipped_and_recorded_with_its_position()
    {
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "One", 10, RichTask);
        list.Poisoned[5] = "early";

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        var tasks = ArchiveTasks(doc);
        Assert.Equal(9, tasks.Count);
        Assert.DoesNotContain(tasks, t => (string)t["id"]! == "t5");

        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal(ReadErrorKinds.TaskUnreadable, error.Kind);
        Assert.Equal(5, error.Position);
        Assert.Null(error.TaskId);
        Assert.Equal("L1", error.ListId);
        Assert.Contains("InternalServerError", error.Message);

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal(1, Inventory.From(archive).Totals.Unreadable);
        Assert.Equal(1, Inventory.From(archive).PerList[0].Counts.Unreadable);
    }

    [Fact]
    public async Task Plain_truncation_is_salvaged_up_to_the_last_complete_property()
    {
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "One", 3, RichTask);
        list.Poisoned[1] = "truncated";

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        var tasks = ArchiveTasks(doc);
        Assert.Equal(3, tasks.Count);
        var partial = tasks[1];
        Assert.Equal("t1", (string)partial["id"]!);
        Assert.Equal("Task 1", (string)partial["title"]!);
        Assert.Equal("2026-11-02T09:30:00.0000000Z", (string)partial["lastModifiedDateTime"]!);
        Assert.False(partial.ContainsKey("body"));
        Assert.False(partial.ContainsKey("dueDateTime"));
        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal(ReadErrorKinds.TaskPartial, error.Kind);
        Assert.Contains("'body'", error.Message);
    }

    [Fact]
    public async Task Error_status_on_a_sub_window_during_bisection_is_recorded_and_the_run_continues()
    {
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "One", 150, RichTask);
        list.Poisoned[10] = "linked";
        // During bisection, the single-task window at position 60 answers 400.
        graph.Intercept = (r, _) => r.Url == $"{Base}me/todo/lists/L1/tasks?$top=1&$skip=60"
            ? FakeGraph.Json("""{ "error": { "code": "invalidRequest", "message": "Invalid request" } }""", HttpStatusCode.BadRequest)
            : null;
        // Make 60 fail at every larger window too, so bisection has to reach it.
        list.Poisoned[60] = "early";

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        var tasks = ArchiveTasks(doc);
        Assert.Equal(149, tasks.Count);
        Assert.Equal("t149", (string)tasks[^1]["id"]!);
        Assert.Equal(2, doc.ReadNotes.Errors.Count);
        var unreadable = doc.ReadNotes.Errors.Single(e => e.Kind == ReadErrorKinds.TaskUnreadable);
        Assert.Equal(60, unreadable.Position);
        Assert.Contains("400", unreadable.Message);
        Assert.Contains(doc.ReadNotes.Errors, e => e.Kind == ReadErrorKinds.TaskPartial && e.Position == 10);
    }

    [Fact]
    public async Task Fake_graph_rejects_select_with_title_like_the_live_api()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 1);
        var http = new HttpClient(graph);
        var response = await http.GetAsync($"{Base}me/todo/lists/L1/tasks?$top=1&$select=id,title");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalidRequest", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_list_that_fails_everywhere_does_not_loop_forever()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 3);
        graph.Intercept = (r, _) => r.Url.Contains("/tasks?")
            ? FakeGraph.Json("""{ "error": { "code": "invalidRequest", "message": "Invalid request" } }""", HttpStatusCode.BadRequest)
            : null;

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Empty(ArchiveTasks(doc));
        Assert.All(doc.ReadNotes.Errors, e => Assert.Equal(ReadErrorKinds.TaskUnreadable, e.Kind));
        Assert.Equal(2 * GraphTodoReader.PageSize, doc.ReadNotes.Errors.Count);
    }

    [Fact]
    public async Task Network_exception_is_retried()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 1);
        graph.Intercept = (_, index) => index == 0 ? throw new HttpRequestException("connection reset") : null;

        var (reader, delays) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Single(delays);
        Assert.Single(ArchiveTasks(doc));
    }

    [Fact]
    public async Task Server_error_500_is_retried()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 1);
        graph.Intercept = (r, _) => r.Url.EndsWith("tasks?$top=100") && graph.Requests.Count(x => x.Url == r.Url) == 1
            ? FakeGraph.Json("""{ "error": { "code": "InternalServerError", "message": "oops" } }""", HttpStatusCode.InternalServerError)
            : null;

        var (reader, delays) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Single(delays);
        Assert.Single(ArchiveTasks(doc));
    }

    [Fact]
    public async Task Gives_up_after_ten_throttle_rounds()
    {
        var graph = new FakeGraph();
        graph.Intercept = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(string.Empty) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        };

        var (reader, delays) = Create(graph);
        var ex = await ReadFailsAsync(reader);

        Assert.Equal(new[] { 7, 7, 8, 16, 32, 64, 128, 128, 128, 128 }.Select(x => TimeSpan.FromSeconds(x)), delays);
        Assert.Equal(11, graph.Requests.Count);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task Server_errors_give_up_after_five_retries()
    {
        var graph = new FakeGraph();
        graph.Intercept = (_, _) => FakeGraph.Json("""{ "error": { "code": "InternalServerError", "message": "oops" } }""", HttpStatusCode.InternalServerError);

        var (reader, delays) = Create(graph);
        var ex = await ReadFailsAsync(reader);

        Assert.Equal(new[] { 2, 4, 8, 16, 32 }.Select(x => TimeSpan.FromSeconds(x)), delays);
        Assert.Equal(6, graph.Requests.Count);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Unauthorized_says_the_token_must_be_replaced_and_never_contains_it()
    {
        var graph = new FakeGraph();
        graph.Intercept = (_, _) => FakeGraph.Json("""{ "error": { "code": "InvalidAuthenticationToken", "message": "Access token has expired." } }""", HttpStatusCode.Unauthorized);

        var http = new HttpClient(graph);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token-value");
        var reader = new GraphTodoReader(http, (_, _) => Task.CompletedTask);

        var ex = await ReadFailsAsync(reader);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("401", ex.Message);
        Assert.Contains("missing, invalid or expired", ex.Message);
        Assert.Contains("new token", ex.Message);
        Assert.DoesNotContain("secret-token-value", ex.Message);
        Assert.Single(graph.Requests);
    }

    [Fact]
    public async Task Never_more_than_one_batch_or_request_in_flight()
    {
        var graph = new FakeGraph { BatchLatency = TimeSpan.FromMilliseconds(15) };
        graph.AddList("L1", "One", 200, i => new JsonObject { ["id"] = $"t{i}", ["title"] = $"T{i}", ["hasAttachments"] = i % 50 == 0 });

        var (reader, _) = Create(graph);
        await reader.ReadAsync();

        Assert.Equal(1, GraphTodoReader.MaxBatchesInFlight);
        Assert.Equal(11, graph.Posts.Count()); // 10 step batches + 1 attachment batch (4 tasks)
        Assert.Equal(4, SubRequestCount(graph.Posts.Last()));
        Assert.All(graph.Posts.Last().Body!["requests"]!.AsArray(), r => Assert.EndsWith("/attachments", (string)r!["url"]!));
        Assert.Equal(1, graph.MaxBatchesInFlight);
        Assert.Equal(1, graph.MaxRequestsInFlight);
    }

    [Fact]
    public async Task Sub_response_next_link_is_followed_and_404_is_recorded()
    {
        var graph = new FakeGraph { StepsPageSize = 2 };
        graph.AddList("L1", "One", 2);
        graph.Steps["L1-t0"] = new JsonArray(Enumerable.Range(0, 5)
            .Select(i => (JsonNode?)new JsonObject { ["displayName"] = $"s{i}", ["isChecked"] = false }).ToArray());
        graph.NotFound.Add("L1-t1");

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal(["s0", "s1", "s2", "s3", "s4"], archive.Lists[0].Tasks[0].Steps.Select(s => s.Title));
        Assert.Empty(archive.Lists[0].Tasks[1].Steps);

        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.StepsUnreadable, "L1-t1"), (error.Kind, error.TaskId!));
        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task Fetches_attachments_only_when_has_attachments_is_true()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 2, i => new JsonObject { ["id"] = $"t{i}", ["title"] = $"T{i}", ["hasAttachments"] = i == 0 });
        graph.Attachments["t0"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "A1", ["name"] = "f.png", ["size"] = 10, ["contentType"] = "image/png" });

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        var urls = graph.Requests.Select(r => r.Url).ToList();
        Assert.Contains("/me/todo/lists/L1/tasks/t0/attachments", SubUrls(graph));
        Assert.DoesNotContain("/me/todo/lists/L1/tasks/t1/attachments", SubUrls(graph));
        Assert.DoesNotContain(urls, u => u.EndsWith("/attachments", StringComparison.Ordinal));
        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal("f.png", archive.Lists[0].Tasks[0].Attachments.Single().Name);
        Assert.Empty(archive.Lists[0].Tasks[1].Attachments);
        Assert.IsType<JsonArray>(ArchiveTasks(doc)[1]["attachments"]);
    }

    [Theory]
    [InlineData("https://evil.example.com/v1.0/me/todo/lists?$skiptoken=secret", "https://evil.example.com/v1.0/me/todo/lists")]
    [InlineData("http://graph.microsoft.com/v1.0/me/todo/lists?$skiptoken=secret", "http://graph.microsoft.com/v1.0/me/todo/lists")]
    [InlineData("https://graph.microsoft.com.evil.example/v1.0/me/todo/lists?$skiptoken=secret", "https://graph.microsoft.com.evil.example/v1.0/me/todo/lists")]
    public async Task Refuses_next_links_to_other_hosts_or_schemes(string nextLink, string expectedInMessage)
    {
        var graph = new FakeGraph();
        graph.Intercept = (_, _) => FakeGraph.Json($$"""{ "value": [ { "id": "L1", "displayName": "One" } ], "@odata.nextLink": "{{nextLink}}" }""");

        var (reader, _) = Create(graph);
        var ex = await ReadFailsAsync(reader);

        Assert.Contains("nextLink", ex.Message);
        Assert.Contains(expectedInMessage, ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.Single(graph.Requests);
    }

    [Fact]
    public async Task Task_page_next_link_is_validated_too()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 1);
        graph.Intercept = (r, _) => r.Url.Contains("/tasks?")
            ? FakeGraph.Json("""{ "value": [ { "id": "t", "title": "x" } ], "@odata.nextLink": "https://evil.example.com/next?$skip=1" }""")
            : null;

        var (reader, _) = Create(graph);
        var ex = await ReadFailsAsync(reader);
        Assert.Contains("evil.example.com", ex.Message);
    }

    private sealed class SyncProgress(Action<ReadProgress> handler) : IProgress<ReadProgress>
    {
        public void Report(ReadProgress value)
        {
            lock (this)
            {
                handler(value);
            }
        }
    }
}

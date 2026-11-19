using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TodoExit.Cli;
using TodoExit.Core.Archive;
using TodoExit.Core.Graph;
using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

/// <summary>
/// An outage (network errors, timeouts, 429 or 5xx that outlast the retries) must stop the read with a checkpoint,
/// never be bisected, never be recorded as unreadable data, and never mark a list complete. Also: tasks salvaged from
/// a broken body get their steps from the steps endpoint.
/// </summary>
public class GraphOutageTests
{
    private const string Base = FakeGraph.Base;

    private static GraphTodoReader Reader(FakeGraph graph, bool verifySteps = false) =>
        new(new HttpClient(graph), (_, _) => Task.CompletedTask, () => new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero))
        {
            VerifySteps = verifySteps,
        };

    private static JsonObject RichTask(int i) => new()
    {
        ["@odata.etag"] = $"W/\"etag{i}\"",
        ["importance"] = "normal",
        ["status"] = "notStarted",
        ["title"] = $"Task {i}",
        ["createdDateTime"] = "2026-10-01T08:00:00.0000000Z",
        ["hasAttachments"] = false,
        ["id"] = $"t{i}",
        ["body"] = new JsonObject { ["content"] = $"Notes for {i}", ["contentType"] = "text" },
        ["linkedResources"] = new JsonArray(),
    };

    private static FakeGraph Account(int tasks = 250, int? poisonedAt = null)
    {
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "Big", tasks, RichTask);
        if (poisonedAt is { } p)
        {
            list.Poisoned[p] = "linked";
        }

        return graph;
    }

    private static List<JsonObject> Tasks(TodoArchiveDocument doc) =>
        doc.Lists.OfType<JsonObject>().Single()["tasks"]!.AsArray().Select(t => t!.AsObject()).ToList();

    private static JsonObject Normalized(TodoArchiveDocument doc)
    {
        var root = doc.Root.DeepClone().AsObject();
        root.Remove("exportedAt");
        return root;
    }

    private static HttpResponseMessage Status(HttpStatusCode status)
    {
        var response = FakeGraph.Json($$"""{ "error": { "code": "{{status}}", "message": "not now" } }""", status);
        if (status == HttpStatusCode.TooManyRequests)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
        }

        return response;
    }

    /// <summary>Reads until the outage stops the read; returns the partial archive after a JSON round trip.</summary>
    private static async Task<(TodoArchiveDocument Partial, GraphReadInterruptedException Error)> ReadUntilOutageAsync(
        FakeGraph graph, bool verifySteps = false)
    {
        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph, verifySteps).ReadAsync());
        Assert.False(ex.WasCancelled);
        var inner = Assert.IsType<GraphRequestException>(ex.InnerException);
        Assert.True(inner.IsOutage);
        var partial = TodoArchiveDocument.Parse(ex.PartialArchive.ToJsonString());
        Assert.True(partial.ReadNotes.Interrupted);
        return (partial, ex);
    }

    private static void AssertEveryTaskOnce(TodoArchiveDocument doc, int count)
    {
        Assert.Equal(Enumerable.Range(0, count).Select(i => $"t{i}"), Tasks(doc).Select(t => (string)t["id"]!));
        Assert.False(doc.ReadNotes.Interrupted);
    }

    // ------------------------------------------------------------------ task pages

    [Fact]
    public async Task Network_outage_on_every_task_request_interrupts_and_resume_reads_all_250_tasks_once()
    {
        // The reviewer's scenario: the list request succeeds, every task request fails with a network error.
        var graph = Account();
        graph.Intercept = (r, _) => r.Url.Contains("/tasks?") ? throw new HttpRequestException("connection reset") : null;

        var (partial, error) = await ReadUntilOutageAsync(graph);

        Assert.Contains("network error", error.Reason);
        Assert.Empty(partial.ReadNotes.Errors);
        Assert.Equal(new ListReadProgress("L1", false, 0, false, false), Assert.Single(partial.ReadNotes.Progress));
        Assert.Empty(Tasks(partial));
        // The list, then one task request with its retries. No bisection.
        Assert.Equal(1 + GraphTodoReader.MaxRetries + 1, graph.Requests.Count);
        Assert.DoesNotContain(graph.Requests, r => r.Url.Contains("$top=50"));

        graph.Intercept = null;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        AssertEveryTaskOnce(resumed, 250);
        Assert.Empty(resumed.ReadNotes.Errors);
        Assert.True(JsonNode.DeepEquals(Normalized(await Reader(Account()).ReadAsync()), Normalized(resumed)));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Persistent_unavailable_status_interrupts_without_bisection_and_resume_reads_all_tasks(HttpStatusCode status)
    {
        var graph = Account();
        graph.Intercept = (r, _) => r.Url.Contains("/tasks?") ? Status(status) : null;

        var (partial, error) = await ReadUntilOutageAsync(graph);

        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), error.Reason);
        Assert.Empty(partial.ReadNotes.Errors);
        Assert.False(Assert.Single(partial.ReadNotes.Progress).TasksComplete);
        var attempts = status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests
            ? GraphTodoReader.MaxThrottleRounds + 1
            : GraphTodoReader.MaxRetries + 1;
        Assert.Equal(1 + attempts, graph.Requests.Count);
        Assert.All(graph.Requests.Skip(1), r => Assert.Equal($"{Base}me/todo/lists/L1/tasks?$top=100", r.Url));

        graph.Intercept = null;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        AssertEveryTaskOnce(resumed, 250);
        Assert.Empty(resumed.ReadNotes.Errors);
    }

    [Fact]
    public async Task Persistent_500_is_bisected_only_until_it_is_clearly_an_outage()
    {
        // A 500 could come from one record, so it is bisected; when nothing in the window ever answers, it is an outage.
        var graph = Account();
        graph.Intercept = (r, _) => r.Url.Contains("/tasks?") ? Status(HttpStatusCode.InternalServerError) : null;

        var (partial, error) = await ReadUntilOutageAsync(graph);

        Assert.Contains("answered no request for positions 0 to 99", error.Reason);
        Assert.Empty(partial.ReadNotes.Errors);
        Assert.False(Assert.Single(partial.ReadNotes.Progress).TasksComplete);
        // Window, 6 left halves down to one task, one more single-task window; each with its retries.
        var pageRequests = graph.Requests.Skip(1).Select(r => r.Url).Distinct().Count();
        Assert.Equal(8, pageRequests);
        Assert.Equal(1 + 8 * (GraphTodoReader.MaxRetries + 1), graph.Requests.Count);

        graph.Intercept = null;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        AssertEveryTaskOnce(resumed, 250);
    }

    [Fact]
    public async Task Outage_that_starts_during_bisection_of_a_broken_page_keeps_the_window_for_resume()
    {
        // Window 100 is broken by task 137. Bisection reads [100, 150) fine, then the network goes away.
        var graph = Account(poisonedAt: 137);
        var down = false;
        graph.Intercept = (r, _) =>
        {
            if (r.Url == $"{Base}me/todo/lists/L1/tasks?$top=50&$skip=100")
            {
                down = true;
                return null;
            }

            return down ? throw new HttpRequestException("network is unreachable") : null;
        };

        var (partial, _) = await ReadUntilOutageAsync(graph);

        Assert.Equal(new ListReadProgress("L1", false, 100, false, false), Assert.Single(partial.ReadNotes.Progress));
        Assert.Equal(100, Tasks(partial).Count);
        Assert.Empty(partial.ReadNotes.Errors);

        graph.Intercept = null;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        AssertEveryTaskOnce(resumed, 250);
        var error = Assert.Single(resumed.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.TaskPartial, "t137"), (error.Kind, error.TaskId!));
        Assert.True((bool)Tasks(resumed)[137]["todoExitPartial"]!);
        Assert.True(JsonNode.DeepEquals(Normalized(await Reader(Account(poisonedAt: 137)).ReadAsync()), Normalized(resumed)));
    }

    [Fact]
    public async Task Single_task_answering_500_while_others_answer_is_still_recorded_as_unreadable()
    {
        // The window answers 500 because of task 60, the rest of the window answers: a record-specific error.
        var graph = Account(150);
        graph.Intercept = (r, _) =>
        {
            var u = new Uri(r.Url);
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            if (!r.Url.Contains("/tasks?"))
            {
                return null;
            }

            var top = int.Parse(q["$top"]!);
            var skip = int.Parse(q["$skip"] ?? "0");
            return skip <= 60 && 60 < skip + top ? Status(HttpStatusCode.InternalServerError) : null;
        };

        var doc = await Reader(graph).ReadAsync();

        Assert.Equal(149, Tasks(doc).Count);
        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.TaskUnreadable, 60), (error.Kind, error.Position!.Value));
        Assert.Contains("500", error.Message);
        Assert.False(doc.ReadNotes.Interrupted);
    }

    [Fact]
    public async Task Two_windows_without_a_readable_task_stop_the_read_without_marking_the_list_complete()
    {
        // Graph answers (with broken bodies), but no task in 200 positions has an id. The reader gives up on probing,
        // keeps what it recorded, and leaves the list incomplete so a resumed read continues after those windows.
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "Big", 250, RichTask);
        for (var i = 0; i < 200; i++)
        {
            list.Poisoned[i] = "early";
        }

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync());
        var partial = TodoArchiveDocument.Parse(ex.PartialArchive.ToJsonString());

        Assert.Contains("no readable task in 200 positions", ex.Reason);
        Assert.Equal(new ListReadProgress("L1", false, 200, false, false), Assert.Single(partial.ReadNotes.Progress));
        Assert.Equal(200, partial.ReadNotes.Errors.Count(e => e.Kind == ReadErrorKinds.TaskUnreadable));

        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        Assert.Equal(Enumerable.Range(200, 50).Select(i => $"t{i}"), Tasks(resumed).Select(t => (string)t["id"]!));
        Assert.Equal(200, resumed.ReadNotes.Errors.Count);
        Assert.False(resumed.ReadNotes.Interrupted);
    }

    [Fact]
    public async Task Cli_exits_3_with_a_partial_archive_on_an_outage()
    {
        var graph = Account(20);
        graph.Intercept = (r, _) => r.Url.Contains("/tasks?") ? throw new HttpRequestException("connection reset") : null;
        var outDir = Path.Combine(AppContext.BaseDirectory, "cli-tests", Guid.NewGuid().ToString("N"));
        var stderr = new StringWriter();

        var code = await Program.RunAsync(["export", "--token", "x", "--out", outDir], new StringWriter(), stderr, graph,
            CancellationToken.None, (_, _) => Task.CompletedTask);

        Assert.Equal(Program.ExitInterrupted, code);
        var partialPath = Path.Combine(outDir, "archive.partial.json");
        Assert.True(File.Exists(partialPath));
        Assert.False(File.Exists(Path.Combine(outDir, "archive.json")));
        Assert.Contains("When Microsoft Graph is available again, run the same command with --resume", stderr.ToString());
        var partial = TodoArchiveDocument.Parse(await File.ReadAllBytesAsync(partialPath));
        Assert.False(Assert.Single(partial.ReadNotes.Progress).TasksComplete);
    }

    // ------------------------------------------------------------------ steps and attachments

    [Fact]
    public async Task Batch_outage_during_steps_interrupts_and_resume_reads_the_steps()
    {
        var graph = Account(30);
        graph.InlineSteps = false;
        graph.Steps["t25"] = new JsonArray((JsonNode)new JsonObject { ["displayName"] = "late step", ["isChecked"] = false });
        var posts = 0;
        graph.Intercept = (r, _) => r.Method == "POST" && ++posts >= 2 ? throw new HttpRequestException("connection reset") : null;

        var (partial, _) = await ReadUntilOutageAsync(graph, verifySteps: true);

        Assert.Equal(new ListReadProgress("L1", true, 30, false, false), Assert.Single(partial.ReadNotes.Progress));
        Assert.Empty(partial.ReadNotes.Errors);
        Assert.Equal(1 + GraphTodoReader.MaxRetries + 1, posts);
        Assert.All(Tasks(partial).Skip(20), t => Assert.Null(t["checklistItems"]));

        graph.Intercept = null;
        var resumed = await Reader(graph, verifySteps: true).ReadAsync(resumeFrom: partial);
        Assert.Equal("late step", (string)Tasks(resumed)[25]["checklistItems"]![0]!["displayName"]!);
        Assert.Empty(resumed.ReadNotes.Errors);
        Assert.False(resumed.ReadNotes.Interrupted);
    }

    [Fact]
    public async Task Every_sub_request_throttled_is_an_outage_not_unreadable_steps()
    {
        var graph = Account(3);
        graph.InlineSteps = false;
        foreach (var id in new[] { "t0", "t1", "t2" })
        {
            graph.ThrottleTimes[id] = 100;
        }

        var (partial, error) = await ReadUntilOutageAsync(graph, verifySteps: true);

        Assert.Contains("kept throttling", error.Reason);
        Assert.Empty(partial.ReadNotes.Errors);
        Assert.False(Assert.Single(partial.ReadNotes.Progress).StepsComplete);
        Assert.Equal(GraphTodoReader.MaxThrottleRounds, graph.Posts.Count());
    }

    [Fact]
    public async Task Sub_request_500_is_retried_and_recorded_only_when_others_answer()
    {
        var graph = Account(3);
        graph.InlineSteps = false;
        graph.Steps["t1"] = new JsonArray((JsonNode)new JsonObject { ["displayName"] = "s", ["isChecked"] = false });
        var subCalls = 0;
        graph.Intercept = (r, _) =>
        {
            if (r.Method != "POST")
            {
                return null;
            }

            // t1's sub-request answers 500 twice, then succeeds; t2's always answers 500.
            var responses = new JsonArray();
            foreach (var sub in r.Body!["requests"]!.AsArray())
            {
                var url = (string)sub!["url"]!;
                var id = (string)sub["id"]!;
                var fail = url.Contains("/t2/") || (url.Contains("/t1/") && ++subCalls <= 2);
                responses.Add((JsonNode)(fail
                    ? new JsonObject { ["id"] = id, ["status"] = 500, ["body"] = new JsonObject { ["error"] = new JsonObject { ["code"] = "InternalServerError" } } }
                    : new JsonObject
                    {
                        ["id"] = id,
                        ["status"] = 200,
                        ["body"] = new JsonObject { ["value"] = url.Contains("/t1/") ? graph.Steps["t1"].DeepClone() : new JsonArray() },
                    }));
            }

            return FakeGraph.Json(new JsonObject { ["responses"] = responses }.ToJsonString());
        };

        var doc = await Reader(graph, verifySteps: true).ReadAsync();

        Assert.Equal("s", (string)Tasks(doc)[1]["checklistItems"]![0]!["displayName"]!);
        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.StepsUnreadable, "t2"), (error.Kind, error.TaskId!));
        Assert.Contains("server error", error.Message);
        Assert.False(doc.ReadNotes.Interrupted);
    }

    [Fact]
    public async Task Individual_mode_500_everywhere_is_an_outage_and_500_on_one_task_is_recorded()
    {
        var everywhere = Account(3);
        everywhere.InlineSteps = false;
        everywhere.BatchStatus = HttpStatusCode.BadRequest;
        everywhere.Intercept = (r, _) => r.Url.EndsWith("/checklistItems", StringComparison.Ordinal) ? Status(HttpStatusCode.InternalServerError) : null;

        var (partial, error) = await ReadUntilOutageAsync(everywhere, verifySteps: true);
        Assert.Contains("answered no request for the steps", error.Reason);
        Assert.Empty(partial.ReadNotes.Errors);
        Assert.False(Assert.Single(partial.ReadNotes.Progress).StepsComplete);

        var one = Account(3);
        one.InlineSteps = false;
        one.BatchStatus = HttpStatusCode.BadRequest;
        one.Intercept = (r, _) => r.Url.EndsWith("/t0/checklistItems", StringComparison.Ordinal) ? Status(HttpStatusCode.InternalServerError) : null;

        var doc = await Reader(one, verifySteps: true).ReadAsync();
        var recorded = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.StepsUnreadable, "t0"), (recorded.Kind, recorded.TaskId!));
        Assert.False(doc.ReadNotes.Interrupted);
        // t0 is tried once with its retries, deferred, then once more after t1 answered.
        Assert.Equal(2 * (GraphTodoReader.MaxRetries + 1), one.Requests.Count(r => r.Url.EndsWith("/t0/checklistItems", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Attachment_batch_outage_interrupts_and_resume_reads_the_attachments()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 3, i => new JsonObject { ["id"] = $"t{i}", ["title"] = $"T{i}", ["hasAttachments"] = i == 1 });
        graph.Attachments["t1"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "a", ["name"] = "f.png", ["size"] = 3 });
        graph.Intercept = (r, _) => r.Method == "POST" ? Status(HttpStatusCode.ServiceUnavailable) : null;

        var (partial, _) = await ReadUntilOutageAsync(graph);
        Assert.Equal(new ListReadProgress("L1", true, 3, true, false), Assert.Single(partial.ReadNotes.Progress));
        Assert.Empty(partial.ReadNotes.Errors);

        graph.Intercept = null;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        Assert.Equal("f.png", (string)Tasks(resumed)[1]["attachments"]![0]!["name"]!);
        Assert.Empty(resumed.ReadNotes.Errors);
    }

    // ------------------------------------------------------------------ steps of salvaged tasks (default mode)

    [Fact]
    public async Task Salvaged_task_gets_its_steps_from_the_steps_endpoint_in_the_default_mode()
    {
        var graph = Account(10, poisonedAt: 3);
        graph.Steps["t3"] = new JsonArray(
            (JsonNode)new JsonObject { ["displayName"] = "first", ["isChecked"] = false },
            (JsonNode)new JsonObject { ["displayName"] = "second", ["isChecked"] = true });
        graph.Steps["t5"] = new JsonArray((JsonNode)new JsonObject { ["displayName"] = "inline", ["isChecked"] = false });

        var reader = Reader(graph);
        var doc = await reader.ReadAsync();

        var tasks = Tasks(doc);
        Assert.True((bool)tasks[3]["todoExitPartial"]!);
        Assert.Equal(["first", "second"], tasks[3]["checklistItems"]!.AsArray().Select(s => (string)s!["displayName"]!));
        Assert.Equal("inline", (string)tasks[5]["checklistItems"]![0]!["displayName"]!);

        // One batch with one sub-request, for the salvaged task only. No step requests for normal tasks.
        var post = Assert.Single(graph.Posts);
        Assert.Equal(["/me/todo/lists/L1/tasks/t3/checklistItems"], post.Body!["requests"]!.AsArray().Select(r => (string)r!["url"]!));
        Assert.DoesNotContain(graph.Requests, r => r.Method == "GET" && r.Url.Contains("checklistItems"));
        Assert.Equal("inline", reader.StepReadMode);
        Assert.Equal(0, reader.StepsFoundByVerification);
        Assert.Equal(ReadErrorKinds.TaskPartial, Assert.Single(doc.ReadNotes.Errors).Kind);

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal(2, archive.Lists[0].Tasks[3].Steps.Count);
    }

    [Fact]
    public async Task Salvaged_task_whose_steps_cannot_be_read_is_recorded_as_steps_unreadable()
    {
        var graph = Account(10, poisonedAt: 3);
        graph.NotFound.Add("t3");

        var doc = await Reader(graph).ReadAsync();

        Assert.Empty(Tasks(doc)[3]["checklistItems"]!.AsArray());
        Assert.Equal(2, doc.ReadNotes.Errors.Count);
        var steps = doc.ReadNotes.Errors.Single(e => e.Kind == ReadErrorKinds.StepsUnreadable);
        Assert.Equal("t3", steps.TaskId);
        Assert.Contains("404", steps.Message);
        Assert.Contains(doc.ReadNotes.Errors, e => e.Kind == ReadErrorKinds.TaskPartial && e.TaskId == "t3");
    }

    [Fact]
    public async Task Outage_while_reading_steps_of_a_salvaged_task_interrupts_and_resume_reads_them()
    {
        var graph = Account(10, poisonedAt: 3);
        graph.Steps["t3"] = new JsonArray((JsonNode)new JsonObject { ["displayName"] = "kept", ["isChecked"] = false });
        graph.Intercept = (r, _) => r.Method == "POST" ? throw new HttpRequestException("connection reset") : null;

        var (partial, _) = await ReadUntilOutageAsync(graph);

        // A list whose last window was bisected ends with the paging position after that window.
        Assert.Equal(new ListReadProgress("L1", true, 100, false, false), Assert.Single(partial.ReadNotes.Progress));
        Assert.Null(Tasks(partial)[3]["checklistItems"]);
        Assert.Equal(ReadErrorKinds.TaskPartial, Assert.Single(partial.ReadNotes.Errors).Kind);

        graph.Intercept = null;
        var mark = graph.Requests.Count;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        Assert.Equal("kept", (string)Tasks(resumed)[3]["checklistItems"]![0]!["displayName"]!);
        Assert.Single(graph.Requests.Skip(mark), r => r.Method == "POST");
        Assert.Single(resumed.ReadNotes.Errors);
        Assert.False(resumed.ReadNotes.Interrupted);
        Assert.DoesNotContain(graph.Requests.Skip(mark), r => r.Url.Contains("/tasks?"));
    }
}

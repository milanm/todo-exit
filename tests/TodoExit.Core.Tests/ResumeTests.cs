using System.Net;
using System.Text.Json.Nodes;
using TodoExit.Core.Archive;
using TodoExit.Core.Export;
using TodoExit.Core.Graph;
using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

public class ResumeTests
{
    private const string Base = FakeGraph.Base;

    /// <summary>A fake account: "Big" with 230 tasks (paging, steps, attachments, completed tasks) and "Small" with 5.</summary>
    private static FakeGraph Account(int? poisonedAt = null)
    {
        var graph = new FakeGraph();
        var big = graph.AddList("L1", "Big", 230, i => new JsonObject
        {
            ["id"] = $"t{i}",
            ["title"] = $"Task {i}",
            ["status"] = i % 11 == 0 ? "completed" : "notStarted",
            ["hasAttachments"] = i % 40 == 0,
        });
        graph.AddList("L2", "Small", 5, i => new JsonObject { ["id"] = $"s{i}", ["title"] = $"Small {i}", ["hasAttachments"] = i == 2 });

        for (var i = 0; i < 230; i += 7)
        {
            graph.Steps[$"t{i}"] = new JsonArray((JsonNode)new JsonObject { ["displayName"] = $"step {i}", ["isChecked"] = i % 2 == 0 });
        }

        foreach (var id in new[] { "t0", "t40", "t80", "t120", "t160", "t200", "s2" })
        {
            graph.Attachments[id] = new JsonArray((JsonNode)new JsonObject { ["id"] = $"a-{id}", ["name"] = $"{id}.pdf", ["size"] = 100, ["contentBytes"] = "AAAA" });
        }

        if (poisonedAt is { } p)
        {
            big.Poisoned[p] = "early";
        }

        return graph;
    }

    private static GraphTodoReader Reader(FakeGraph graph, bool verifySteps = false) =>
        new(new HttpClient(graph), (_, _) => Task.CompletedTask, () => new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero))
        {
            VerifySteps = verifySteps,
        };

    private static HttpResponseMessage Unauthorized() =>
        FakeGraph.Json("""{ "error": { "code": "InvalidAuthenticationToken", "message": "Lifetime validation failed, the token is expired." } }""", HttpStatusCode.Unauthorized);

    /// <summary>Normalized JSON for comparison: without exportedAt.</summary>
    private static JsonObject Normalized(TodoArchiveDocument doc)
    {
        var root = doc.Root.DeepClone().AsObject();
        root.Remove("exportedAt");
        return root;
    }

    private static TodoArchiveDocument Baseline(int? poisonedAt = null, bool verifySteps = false)
    {
        var graph = Account(poisonedAt);
        graph.InlineSteps = !verifySteps;
        return Reader(graph, verifySteps).ReadAsync().GetAwaiter().GetResult();
    }

    /// <summary>Runs a read that is interrupted by the intercept, and returns the partial archive after a file round trip.</summary>
    private static async Task<TodoArchiveDocument> InterruptAsync(FakeGraph graph, Func<FakeGraph.Request, bool> failWhen, bool verifySteps = false)
    {
        var fired = false;
        graph.Intercept = (r, _) =>
        {
            if (!fired && failWhen(r))
            {
                fired = true;
                return Unauthorized();
            }

            return null;
        };

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph, verifySteps).ReadAsync());
        graph.Intercept = null;
        Assert.IsType<GraphRequestException>(ex.InnerException);
        Assert.Contains("401", ex.Reason);
        Assert.False(ex.WasCancelled);
        return TodoArchiveDocument.Parse(ex.PartialArchive.ToJsonString());
    }

    private static ListReadProgress ProgressOf(TodoArchiveDocument doc, string listId) =>
        doc.ReadNotes.Progress.Single(p => p.ListId == listId);

    private static List<JsonObject> Tasks(TodoArchiveDocument doc, string listId) =>
        doc.Lists.OfType<JsonObject>().Single(l => (string)l["list"]!["id"]! == listId)["tasks"]!.AsArray().Select(t => t!.AsObject()).ToList();

    private static List<string> SubUrls(IEnumerable<FakeGraph.Request> requests) =>
        requests.Where(r => r.Method == "POST").SelectMany(p => p.Body!["requests"]!.AsArray()).Select(r => (string)r!["url"]!).ToList();

    [Fact]
    public void Complete_archive_has_no_interruption_markers()
    {
        var doc = Baseline();
        var notes = doc.Root["readNotes"] as JsonObject;
        Assert.True(notes is null || (!notes.ContainsKey("interrupted") && !notes.ContainsKey("interruptedReason") && !notes.ContainsKey("progress")));
        Assert.False(doc.ReadNotes.Interrupted);
        Assert.All(Tasks(doc, "L1").Where(t => t["attachments"]!.AsArray().Count > 0),
            t => Assert.Null(t["attachments"]![0]!["contentBytes"]));
    }

    [Fact]
    public async Task Interrupted_during_paging_then_resumed()
    {
        var graph = Account();
        var partial = await InterruptAsync(graph, r => r.Url == $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=200");

        var raw = partial.Root["readNotes"]!.AsObject();
        Assert.True((bool)raw["interrupted"]!);
        Assert.Contains("401", (string)raw["interruptedReason"]!);
        Assert.Equal(2, raw["pendingLists"]!.AsArray().Count);
        Assert.Equal(new ListReadProgress("L1", false, 200, false, false), ProgressOf(partial, "L1"));
        Assert.Single(partial.ReadNotes.Progress);
        Assert.Single(partial.Lists);
        Assert.Equal(200, Tasks(partial, "L1").Count);

        var mark = graph.Requests.Count;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        var after = graph.Requests.Skip(mark).ToList();

        Assert.True(JsonNode.DeepEquals(Normalized(Baseline()), Normalized(resumed)));
        Assert.DoesNotContain(after, r => r.Url == $"{Base}me/todo/lists/L1/tasks?$top=100");
        Assert.DoesNotContain(after, r => r.Url == $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=100");
        Assert.Contains(after, r => r.Url == $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=200");
        Assert.False(resumed.ReadNotes.Interrupted);
    }

    [Fact]
    public async Task Interrupted_during_step_verification_then_resumed_without_rereading_finished_steps()
    {
        var graph = Account();
        graph.InlineSteps = false;
        var posts = 0;
        var partial = await InterruptAsync(graph, r => r.Method == "POST" && ++posts == 3, verifySteps: true);

        Assert.Equal(new ListReadProgress("L1", true, 230, false, false), ProgressOf(partial, "L1"));
        var tasks = Tasks(partial, "L1");
        Assert.Equal(230, tasks.Count);
        Assert.Equal(40, tasks.Count(t => t["checklistItems"] is JsonArray));

        var mark = graph.Requests.Count;
        var resumed = await Reader(graph, verifySteps: true).ReadAsync(resumeFrom: partial);
        var after = graph.Requests.Skip(mark).ToList();

        Assert.True(JsonNode.DeepEquals(Normalized(Baseline(verifySteps: true)), Normalized(resumed)));
        Assert.Equal(33, resumed.ReadNotes.StepsFoundByVerification);
        Assert.DoesNotContain(after, r => r.Url.StartsWith($"{Base}me/todo/lists/L1/tasks?", StringComparison.Ordinal));
        var stepUrls = SubUrls(after).Where(u => u.EndsWith("/checklistItems", StringComparison.Ordinal) && u.Contains("/L1/")).ToList();
        Assert.Equal(190, stepUrls.Count);
        Assert.DoesNotContain("/me/todo/lists/L1/tasks/t0/checklistItems", stepUrls);
        Assert.DoesNotContain("/me/todo/lists/L1/tasks/t39/checklistItems", stepUrls);
    }

    [Fact]
    public async Task Interrupted_during_attachments_then_resumed()
    {
        var graph = Account();
        var partial = await InterruptAsync(graph, r => r.Method == "POST" && r.Body!.ToJsonString().Contains("/attachments"));

        Assert.Equal(new ListReadProgress("L1", true, 230, true, false), ProgressOf(partial, "L1"));
        Assert.All(Tasks(partial, "L1"), t => Assert.IsType<JsonArray>(t["checklistItems"]));

        var mark = graph.Requests.Count;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        var after = graph.Requests.Skip(mark).ToList();

        Assert.True(JsonNode.DeepEquals(Normalized(Baseline()), Normalized(resumed)));
        var subs = SubUrls(after);
        Assert.DoesNotContain(subs, u => u.Contains("/L1/") && u.EndsWith("/checklistItems", StringComparison.Ordinal));
        Assert.Equal(6, subs.Count(u => u.Contains("/L1/") && u.EndsWith("/attachments", StringComparison.Ordinal)));
        Assert.DoesNotContain(after, r => r.Url.StartsWith($"{Base}me/todo/lists/L1/tasks?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resume_keeps_earlier_read_errors_without_duplicates()
    {
        // Poisoned task at 120: its error is recorded when window 100 is finished; the read stops at window 200.
        // The resumed run starts at 200 and must not record the error again.
        var graph = Account(poisonedAt: 120);
        var partial = await InterruptAsync(graph, r => r.Url == $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=200");

        Assert.Equal(new ListReadProgress("L1", false, 200, false, false), ProgressOf(partial, "L1"));
        Assert.Equal(199, Tasks(partial, "L1").Count);
        var error = Assert.Single(partial.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.TaskUnreadable, 120), (error.Kind, error.Position!.Value));

        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);

        Assert.Single(resumed.ReadNotes.Errors);
        Assert.Equal(229, Tasks(resumed, "L1").Count);
        Assert.True(JsonNode.DeepEquals(Normalized(Baseline(poisonedAt: 120)), Normalized(resumed)));
    }

    [Fact]
    public async Task Read_stopped_inside_a_bisected_window_keeps_neither_its_tasks_nor_its_errors()
    {
        // The error for 120 is found during bisection of window 100, then the read stops before that window is
        // finished. Like its tasks, the window's errors are not kept; the resumed run reads the window again.
        var graph = Account(poisonedAt: 120);
        var partial = await InterruptAsync(graph, r => r.Url == $"{Base}me/todo/lists/L1/tasks?$top=50&$skip=150");

        Assert.Equal(new ListReadProgress("L1", false, 100, false, false), ProgressOf(partial, "L1"));
        Assert.Equal(100, Tasks(partial, "L1").Count);
        Assert.Empty(partial.ReadNotes.Errors);

        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);

        var error = Assert.Single(resumed.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.TaskUnreadable, 120), (error.Kind, error.Position!.Value));
        Assert.Equal(229, Tasks(resumed, "L1").Count);
        Assert.True(JsonNode.DeepEquals(Normalized(Baseline(poisonedAt: 120)), Normalized(resumed)));
    }

    [Fact]
    public async Task Resume_reads_new_lists_and_keeps_lists_that_disappeared()
    {
        var graph = new FakeGraph();
        graph.AddList("A", "First", 5);
        graph.AddList("B", "Second", 230);
        var partial = await InterruptAsync(graph, r => r.Url == $"{Base}me/todo/lists/B/tasks?$top=100&$skip=100");
        Assert.True(ProgressOf(partial, "A").IsComplete);
        Assert.False(ProgressOf(partial, "B").TasksComplete);

        graph.RemoveList("A");
        graph.AddList("C", "Third", 3);
        var mark = graph.Requests.Count;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        var after = graph.Requests.Skip(mark).ToList();

        Assert.Equal(["A", "B", "C"], resumed.Lists.OfType<JsonObject>().Select(l => (string)l["list"]!["id"]!));
        Assert.True(JsonNode.DeepEquals(new JsonArray(Tasks(partial, "A").Select(t => (JsonNode)t.DeepClone()).ToArray()),
            new JsonArray(Tasks(resumed, "A").Select(t => (JsonNode)t.DeepClone()).ToArray())));
        Assert.Equal(230, Tasks(resumed, "B").Count);
        Assert.Equal(3, Tasks(resumed, "C").Count);
        Assert.DoesNotContain(after, r => r.Url.Contains("/lists/A/") || SubUrls([r]).Any(u => u.Contains("/lists/A/")));
        Assert.False(resumed.ReadNotes.Interrupted);
    }

    [Fact]
    public async Task Resuming_a_partial_archive_without_steps_sends_no_step_requests_by_default()
    {
        var graph = Account();
        graph.InlineSteps = false;
        var posts = 0;
        var partial = await InterruptAsync(graph, r => r.Method == "POST" && ++posts == 2, verifySteps: true);
        Assert.Contains(Tasks(partial, "L1"), t => t["checklistItems"] is null);

        var mark = graph.Requests.Count;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        var after = graph.Requests.Skip(mark).ToList();

        Assert.DoesNotContain(SubUrls(after), u => u.EndsWith("/checklistItems", StringComparison.Ordinal));
        Assert.DoesNotContain(after, r => r.Url.Contains("checklistItems"));
        Assert.All(Tasks(resumed, "L1"), t => Assert.IsType<JsonArray>(t["checklistItems"]));
        Assert.False(resumed.ReadNotes.Interrupted);
    }

    [Fact]
    public async Task Cancellation_produces_a_partial_archive()
    {
        var graph = Account();
        graph.InlineSteps = false;
        using var cts = new CancellationTokenSource();
        var posts = 0;
        graph.Intercept = (r, _) =>
        {
            if (r.Method == "POST" && ++posts == 2)
            {
                cts.Cancel();
            }

            return null;
        };

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph, verifySteps: true).ReadAsync(cancellationToken: cts.Token));

        Assert.True(ex.WasCancelled);
        Assert.Equal("The read was cancelled.", ex.Reason);
        var progress = ex.PartialArchive.ReadNotes.Progress.Single();
        Assert.True(progress.TasksComplete);
        Assert.False(progress.StepsComplete);
        Assert.Equal(40, Tasks(ex.PartialArchive, "L1").Count(t => t["checklistItems"] is JsonArray));
    }

    [Fact]
    public async Task Unexpected_exception_is_wrapped_with_the_partial_archive()
    {
        var graph = Account();
        graph.Intercept = (r, _) => r.Method == "POST" ? throw new InvalidOperationException("boom") : null;

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync());

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("boom", ex.Reason);
        Assert.Equal(230, Tasks(ex.PartialArchive, "L1").Count);
    }

    [Fact]
    public async Task Exporters_and_report_work_on_a_partial_archive()
    {
        var graph = Account();
        var partial = await InterruptAsync(graph, r => r.Url == $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=200");

        var archive = ArchiveParser.Parse(partial, TimeZoneInfo.Utc);
        var result = ExportService.Export(partial, archive, new ExportOptions());

        Assert.Contains(result.Files, f => f.Path == "todoist/Big.csv");
        Assert.Contains(result.Files, f => f.Path == "markdown/Big.md");

        var md = result.Files.Single(f => f.Path == "report.md").GetText();
        Assert.StartsWith("# todo-exit report\n\n**This archive is incomplete.**", md);
        Assert.Contains("List in progress: **Big** (tasks read up to position 200", md);
        Assert.Contains("--resume", md);

        var json = JsonNode.Parse(result.Files.Single(f => f.Path == "report.json").GetText())!.AsObject();
        Assert.True((bool)json["interrupted"]!);
        Assert.Contains("401", (string)json["interruptedReason"]!);
        Assert.Equal("Big", (string)json["listInProgress"]!);
        Assert.Equal("interrupted", json.First().Key);

        var complete = ExportService.Export(Baseline(), ArchiveParser.Parse(Baseline(), TimeZoneInfo.Utc), new ExportOptions());
        var completeJson = JsonNode.Parse(complete.Files.Single(f => f.Path == "report.json").GetText())!.AsObject();
        Assert.False((bool)completeJson["interrupted"]!);
        Assert.DoesNotContain("incomplete", complete.Files.Single(f => f.Path == "report.md").GetText());
    }

    [Fact]
    public void Partial_archive_read_notes_round_trip()
    {
        var notes = new ReadNotes([])
        {
            Interrupted = true,
            InterruptedReason = "token expired",
            Progress = [new ListReadProgress("L1", false, 4100, false, false)],
            PendingLists = new JsonArray((JsonNode)new JsonObject { ["id"] = "L1", ["displayName"] = "Big" }),
        };
        var doc = TodoArchiveDocument.Create(DateTimeOffset.UnixEpoch, [], notes);
        var reread = TodoArchiveDocument.Parse(doc.ToJsonString());

        Assert.True(reread.ReadNotes.Interrupted);
        Assert.Equal("token expired", reread.ReadNotes.InterruptedReason);
        Assert.Equal(notes.Progress, reread.ReadNotes.Progress);
        Assert.Equal("Big", reread.ReadNotes.ListName("L1"));
        Assert.Equal(doc.ToJsonString(), reread.ToJsonString());
        var raw = reread.Root["readNotes"]!["progress"]![0]!.AsObject();
        Assert.Equal(["listId", "tasksComplete", "nextSkip", "stepsComplete", "attachmentsComplete"], raw.Select(p => p.Key));
    }
}

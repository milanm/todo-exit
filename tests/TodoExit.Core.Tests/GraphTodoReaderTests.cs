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

    private static (GraphTodoReader Reader, List<TimeSpan> Delays) Create(FakeGraph graph, bool includeCompletedSteps = true)
    {
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
            IncludeStepsForCompletedTasks = includeCompletedSteps,
        };
        return (reader, delays);
    }

    private static JsonArray StepsFor(string name) =>
        new((JsonNode)new JsonObject { ["displayName"] = name, ["isChecked"] = false });

    private static int SubRequestCount(FakeGraph.Request post) => post.Body!["requests"]!.AsArray().Count;

    private static List<JsonObject> ArchiveTasks(TodoArchiveDocument doc, int list = 0) =>
        doc.Root["lists"]![list]!["tasks"]!.AsArray().Select(n => n!.AsObject()).ToList();

    [Fact]
    public async Task Request_sequence_for_250_tasks_uses_top_skip_pages_and_13_batches()
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

        Assert.Contains(progress, p => p.Phase == ReadPhase.Steps && p.StepsTotal == 250 && p.StepsDone == 250);
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
    public async Task Throttled_sub_responses_are_retried_alone_after_retry_after()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 25);
        graph.Steps["L1-t3"] = StepsFor("three");
        graph.Steps["L1-t22"] = StepsFor("twenty-two");
        graph.ThrottleOnce.UnionWith(["L1-t3", "L1-t22"]);

        var (reader, delays) = Create(graph);
        var doc = await reader.ReadAsync();

        var posts = graph.Posts.ToList();
        Assert.Equal(3, posts.Count);
        var retry = posts[2].Body!["requests"]!.AsArray().Select(r => (string)r!["url"]!).ToList();
        Assert.Equal(["/me/todo/lists/L1/tasks/L1-t3/checklistItems", "/me/todo/lists/L1/tasks/L1-t22/checklistItems"], retry);
        Assert.Equal([TimeSpan.FromSeconds(4)], delays);

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal("three", archive.Lists[0].Tasks[3].Steps.Single().Title);
        Assert.Equal("twenty-two", archive.Lists[0].Tasks[22].Steps.Single().Title);
        Assert.True(doc.ReadNotes.IsEmpty);
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
    public async Task Skipping_steps_of_completed_tasks_sends_no_request_for_them_and_is_recorded()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 3, i => new JsonObject
        {
            ["id"] = $"t{i}",
            ["title"] = $"T{i}",
            ["status"] = i == 1 ? "completed" : "notStarted",
            ["hasAttachments"] = i == 1,
        });
        graph.Steps["t1"] = StepsFor("never read");
        graph.Attachments["t1"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "a", ["name"] = "f.png", ["size"] = 3 });

        var (reader, _) = Create(graph, includeCompletedSteps: false);
        var doc = await reader.ReadAsync();

        var subUrls = graph.Posts.SelectMany(p => p.Body!["requests"]!.AsArray()).Select(r => (string)r!["url"]!).ToList();
        Assert.Equal(2, subUrls.Count);
        Assert.DoesNotContain(subUrls, u => u.Contains("/t1/"));
        Assert.Contains($"{Base}me/todo/lists/L1/tasks/t1/attachments", graph.Requests.Select(r => r.Url));

        Assert.Empty(ArchiveTasks(doc)[1]["checklistItems"]!.AsArray());
        Assert.True((bool)doc.Root["readNotes"]!["stepsForCompletedTasksSkipped"]!);
        Assert.True(doc.ReadNotes.StepsForCompletedTasksSkipped);
        Assert.Empty(doc.ReadNotes.Errors);
    }

    [Fact]
    public async Task Poisoned_page_is_retried_once_bisected_and_the_bad_task_salvaged_as_partial()
    {
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "Big", 250, i => new JsonObject
        {
            ["id"] = $"t{i}",
            ["title"] = $"Task {i}",
            ["status"] = "notStarted",
            ["importance"] = "high",
            ["createdDateTime"] = "2026-10-01T08:00:00Z",
        });
        list.Poisoned[137] = "wide";

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        var urls = graph.Requests.Select(r => r.Url).ToList();
        Assert.Equal(2, urls.Count(u => u == $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=100"));
        Assert.Contains($"{Base}me/todo/lists/L1/tasks?$top=50&$skip=100", urls);
        Assert.Contains($"{Base}me/todo/lists/L1/tasks?$top=1&$skip=137&$select={GraphTodoReader.SalvageSelectWide}", urls);

        var tasks = ArchiveTasks(doc);
        Assert.Equal(Enumerable.Range(0, 250).Select(i => $"t{i}"), tasks.Select(t => (string)t["id"]!));
        var partial = tasks[137];
        Assert.True((bool)partial["todoExitPartial"]!);
        Assert.Null(partial["importance"]);
        Assert.Equal("Task 137", (string)partial["title"]!);
        Assert.Equal("high", (string)tasks[136]["importance"]!);
        Assert.Equal("high", (string)tasks[138]["importance"]!);

        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.TaskPartial, 137, "t137", "Big"), (error.Kind, error.Position!.Value, error.TaskId!, error.ListName!));

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.True(archive.Lists[0].Tasks[137].IsPartial);
        Assert.Equal(1, Inventory.From(archive).Totals.Partial);
    }

    [Fact]
    public async Task Narrow_select_is_the_second_salvage_step()
    {
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "One", 4);
        list.Poisoned[2] = "narrow";

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Contains($"{Base}me/todo/lists/L1/tasks?$top=1&$skip=2&$select={GraphTodoReader.SalvageSelectNarrow}", graph.Requests.Select(r => r.Url));
        var tasks = ArchiveTasks(doc);
        Assert.Equal(4, tasks.Count);
        Assert.True((bool)tasks[2]["todoExitPartial"]!);
        Assert.Null(tasks[2]["status"]);
    }

    [Fact]
    public async Task Unsalvageable_task_is_skipped_and_recorded_with_its_position()
    {
        var graph = new FakeGraph();
        var list = graph.AddList("L1", "One", 10);
        list.Poisoned[5] = "none";

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        var tasks = ArchiveTasks(doc);
        Assert.Equal(9, tasks.Count);
        Assert.DoesNotContain(tasks, t => (string)t["id"]! == "L1-t5");

        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal(ReadErrorKinds.TaskUnreadable, error.Kind);
        Assert.Equal(5, error.Position);
        Assert.Null(error.TaskId);
        Assert.Equal("L1", error.ListId);

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal(1, Inventory.From(archive).Totals.Unreadable);
        Assert.Equal(1, Inventory.From(archive).PerList[0].Counts.Unreadable);
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
    public async Task Gives_up_after_five_retries()
    {
        var graph = new FakeGraph();
        graph.Intercept = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(string.Empty) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        };

        var (reader, delays) = Create(graph);
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => reader.ReadAsync());

        Assert.Equal(5, delays.Count);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(7), d));
        Assert.Equal(6, graph.Requests.Count);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task Unauthorized_says_the_token_must_be_replaced_and_never_contains_it()
    {
        var graph = new FakeGraph();
        graph.Intercept = (_, _) => FakeGraph.Json("""{ "error": { "code": "InvalidAuthenticationToken", "message": "Access token has expired." } }""", HttpStatusCode.Unauthorized);

        var http = new HttpClient(graph);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token-value");
        var reader = new GraphTodoReader(http, (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => reader.ReadAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("401", ex.Message);
        Assert.Contains("missing, invalid or expired", ex.Message);
        Assert.Contains("new token", ex.Message);
        Assert.DoesNotContain("secret-token-value", ex.Message);
        Assert.Single(graph.Requests);
    }

    [Fact]
    public async Task Never_more_than_four_batches_in_flight()
    {
        var graph = new FakeGraph { BatchLatency = TimeSpan.FromMilliseconds(30) };
        graph.AddList("L1", "One", 200);

        var (reader, _) = Create(graph);
        await reader.ReadAsync();

        Assert.Equal(10, graph.Posts.Count());
        Assert.InRange(graph.MaxBatchesInFlight, 2, GraphTodoReader.MaxBatchesInFlight);
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
        Assert.Contains($"{Base}me/todo/lists/L1/tasks/t0/attachments", urls);
        Assert.DoesNotContain($"{Base}me/todo/lists/L1/tasks/t1/attachments", urls);
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
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => reader.ReadAsync());

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
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => reader.ReadAsync());
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

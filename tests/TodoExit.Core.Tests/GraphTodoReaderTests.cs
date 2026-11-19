using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using TodoExit.Core.Graph;
using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

public class GraphTodoReaderTests
{
    private const string Base = "https://graph.microsoft.com/v1.0/";

    /// <summary>Answers requests from a URL to response map and records every request.</summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _routes = new();

        public List<string> Requests { get; } = new();

        public void On(string url, params Func<HttpResponseMessage>[] responses)
        {
            if (!_routes.TryGetValue(url, out var queue))
            {
                _routes[url] = queue = new Queue<Func<HttpResponseMessage>>();
            }

            foreach (var r in responses)
            {
                queue.Enqueue(r);
            }
        }

        public void OnJson(string url, string json) => On(url, () => Json(json));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            Assert.Equal(HttpMethod.Get, request.Method);
            if (_routes.TryGetValue(url, out var queue) && queue.Count > 0)
            {
                var factory = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                return Task.FromResult(factory());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($$"""{ "error": { "code": "NotMocked", "message": "{{url}}" } }"""),
            });
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Tasks(string listId, bool expand = true) =>
        $"{Base}me/todo/lists/{listId}/tasks?$top=100" + (expand ? "&$expand=checklistItems" : string.Empty);

    private static (GraphTodoReader Reader, List<TimeSpan> Delays) Create(FakeGraph graph)
    {
        var delays = new List<TimeSpan>();
        var reader = new GraphTodoReader(
            new HttpClient(graph),
            (d, _) =>
            {
                delays.Add(d);
                return Task.CompletedTask;
            },
            () => new DateTimeOffset(2026, 11, 19, 13, 30, 0, TimeSpan.Zero));
        return (reader, delays);
    }

    [Fact]
    public async Task Follows_next_links_for_lists_and_tasks()
    {
        var graph = new FakeGraph();
        graph.OnJson($"{Base}me/todo/lists", $$"""{ "value": [ { "id": "L1", "displayName": "One" } ], "@odata.nextLink": "{{Base}}me/todo/lists?$skiptoken=p2" }""");
        graph.OnJson($"{Base}me/todo/lists?$skiptoken=p2", """{ "value": [ { "id": "L2", "displayName": "Two" } ] }""");
        graph.OnJson(Tasks("L1"), $$"""{ "value": [ { "id": "T1", "title": "a", "checklistItems": [ { "displayName": "s" } ] } ], "@odata.nextLink": "{{Base}}me/todo/lists/L1/tasks?$skiptoken=t2" }""");
        graph.OnJson($"{Base}me/todo/lists/L1/tasks?$skiptoken=t2", """{ "value": [ { "id": "T2", "title": "b", "checklistItems": [] } ] }""");
        graph.OnJson(Tasks("L2"), """{ "value": [] }""");

        var (reader, _) = Create(graph);
        var progress = new List<ReadProgress>();
        var doc = await reader.ReadAsync(new SyncProgress(progress.Add));

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal(["One", "Two"], archive.Lists.Select(l => l.Name));
        Assert.Equal(["a", "b"], archive.Lists[0].Tasks.Select(t => t.Title));
        Assert.Single(archive.Lists[0].Tasks[0].Steps);
        Assert.False(reader.UsedChecklistFallback);
        Assert.False(reader.UsedPerTaskChecklistRequests);
        Assert.DoesNotContain(graph.Requests, r => r.Contains("/checklistItems"));

        // Every task gets both merged arrays.
        var task2 = doc.Root["lists"]![0]!["tasks"]![1]!.AsObject();
        Assert.IsType<JsonArray>(task2["checklistItems"]);
        Assert.IsType<JsonArray>(task2["attachments"]);

        Assert.Equal("2026-11-19T13:30:00Z", (string)doc.Root["exportedAt"]!);
        var last = progress[^1];
        Assert.Equal((2, 2, 2), (last.ListsDone, last.ListsTotal, last.TasksRead));
    }

    [Fact]
    public async Task Falls_back_to_per_task_checklist_requests_when_expand_is_rejected()
    {
        var graph = new FakeGraph();
        graph.OnJson($"{Base}me/todo/lists", """{ "value": [ { "id": "L1", "displayName": "One" }, { "id": "L2", "displayName": "Two" } ] }""");
        graph.On(Tasks("L1"), () => Json("""{ "error": { "code": "BadRequest", "message": "Expand not supported" } }""", HttpStatusCode.BadRequest));
        graph.OnJson(Tasks("L1", expand: false), """{ "value": [ { "id": "T1", "title": "a" } ] }""");
        graph.OnJson(Tasks("L2", expand: false), """{ "value": [ { "id": "T2", "title": "b" } ] }""");
        graph.OnJson($"{Base}me/todo/lists/L1/tasks/T1/checklistItems", """{ "value": [ { "displayName": "s1", "isChecked": true } ] }""");
        graph.OnJson($"{Base}me/todo/lists/L2/tasks/T2/checklistItems", """{ "value": [] }""");

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.True(reader.UsedChecklistFallback);
        Assert.True(reader.UsedPerTaskChecklistRequests);
        Assert.DoesNotContain(Tasks("L2"), graph.Requests);
        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal([new Step("s1", true)], archive.Lists[0].Tasks[0].Steps);
        Assert.Empty(archive.Lists[1].Tasks[0].Steps);
    }

    [Fact]
    public async Task Fetches_attachments_only_when_has_attachments_is_true()
    {
        var graph = new FakeGraph();
        graph.OnJson($"{Base}me/todo/lists", """{ "value": [ { "id": "L1", "displayName": "One" } ] }""");
        graph.OnJson(Tasks("L1"), """
            { "value": [
                { "id": "T1", "title": "with", "hasAttachments": true, "checklistItems": [] },
                { "id": "T2", "title": "without", "hasAttachments": false, "checklistItems": [] } ] }
            """);
        graph.OnJson($"{Base}me/todo/lists/L1/tasks/T1/attachments", """
            { "value": [ { "id": "A1", "name": "f.png", "size": 10, "contentType": "image/png", "lastModifiedDateTime": "2026-11-01T00:00:00Z" } ] }
            """);

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Contains($"{Base}me/todo/lists/L1/tasks/T1/attachments", graph.Requests);
        Assert.DoesNotContain($"{Base}me/todo/lists/L1/tasks/T2/attachments", graph.Requests);
        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal("f.png", archive.Lists[0].Tasks[0].Attachments.Single().Name);
        Assert.Empty(archive.Lists[0].Tasks[1].Attachments);
    }

    [Fact]
    public async Task Retries_429_using_retry_after()
    {
        var graph = new FakeGraph();
        graph.On($"{Base}me/todo/lists",
            () => Throttled(HttpStatusCode.TooManyRequests, 7),
            () => Throttled(HttpStatusCode.ServiceUnavailable, 3),
            () => Json("""{ "value": [] }"""));

        var (reader, delays) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(3)], delays);
        Assert.Equal(3, graph.Requests.Count);
        Assert.Empty(doc.Lists);
    }

    [Fact]
    public async Task Gives_up_after_five_retries()
    {
        var graph = new FakeGraph();
        graph.On($"{Base}me/todo/lists", () => Throttled(HttpStatusCode.TooManyRequests, 1));

        var (reader, delays) = Create(graph);
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => reader.ReadAsync());

        Assert.Equal(5, delays.Count);
        Assert.Equal(6, graph.Requests.Count);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task Other_errors_include_status_and_graph_message_but_not_the_token()
    {
        var graph = new FakeGraph();
        graph.On($"{Base}me/todo/lists", () => Json("""{ "error": { "code": "InvalidAuthenticationToken", "message": "Access token has expired." } }""", HttpStatusCode.Unauthorized));

        var http = new HttpClient(graph);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token-value");
        var reader = new GraphTodoReader(http, (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => reader.ReadAsync());
        Assert.Contains("401", ex.Message);
        Assert.Contains("Access token has expired.", ex.Message);
        Assert.DoesNotContain("secret-token-value", ex.Message);
    }

    [Fact]
    public async Task Fetches_steps_per_task_when_expand_is_silently_ignored()
    {
        var graph = new FakeGraph();
        graph.OnJson($"{Base}me/todo/lists", """{ "value": [ { "id": "L1", "displayName": "One" } ] }""");
        graph.OnJson(Tasks("L1"), """{ "value": [ { "id": "T1", "title": "a" }, { "id": "T2", "title": "b", "checklistItems": [ { "displayName": "inline" } ] } ] }""");
        graph.OnJson($"{Base}me/todo/lists/L1/tasks/T1/checklistItems", """{ "value": [ { "displayName": "s1", "isChecked": false }, { "displayName": "s2", "isChecked": true } ] }""");

        var (reader, _) = Create(graph);
        var doc = await reader.ReadAsync();

        Assert.False(reader.UsedChecklistFallback);
        Assert.True(reader.UsedPerTaskChecklistRequests);
        Assert.Contains($"{Base}me/todo/lists/L1/tasks/T1/checklistItems", graph.Requests);
        Assert.DoesNotContain($"{Base}me/todo/lists/L1/tasks/T2/checklistItems", graph.Requests);

        var steps = doc.Root["lists"]![0]!["tasks"]![0]!["checklistItems"]!.AsArray();
        Assert.Equal(2, steps.Count);
        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal([new Step("s1", false), new Step("s2", true)], archive.Lists[0].Tasks[0].Steps);
        Assert.Equal([new Step("inline", false)], archive.Lists[0].Tasks[1].Steps);
    }

    [Theory]
    [InlineData("https://evil.example.com/v1.0/me/todo/lists?$skiptoken=secret", "https://evil.example.com/v1.0/me/todo/lists")]
    [InlineData("http://graph.microsoft.com/v1.0/me/todo/lists?$skiptoken=secret", "http://graph.microsoft.com/v1.0/me/todo/lists")]
    [InlineData("https://graph.microsoft.com.evil.example/v1.0/me/todo/lists?$skiptoken=secret", "https://graph.microsoft.com.evil.example/v1.0/me/todo/lists")]
    public async Task Refuses_next_links_to_other_hosts_or_schemes(string nextLink, string expectedInMessage)
    {
        var graph = new FakeGraph();
        graph.OnJson($"{Base}me/todo/lists", $$"""{ "value": [ { "id": "L1", "displayName": "One" } ], "@odata.nextLink": "{{nextLink}}" }""");

        var (reader, _) = Create(graph);
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => reader.ReadAsync());

        Assert.Contains("nextLink", ex.Message);
        Assert.Contains(expectedInMessage, ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.Single(graph.Requests);
    }

    private static HttpResponseMessage Throttled(HttpStatusCode status, int seconds)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    private sealed class SyncProgress(Action<ReadProgress> handler) : IProgress<ReadProgress>
    {
        public void Report(ReadProgress value) => handler(value);
    }
}

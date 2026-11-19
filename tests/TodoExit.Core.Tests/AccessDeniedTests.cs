using System.Net;
using System.Text.Json.Nodes;
using TodoExit.Core.Archive;
using TodoExit.Core.Export;
using TodoExit.Core.Graph;
using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

public class AccessDeniedTests
{
    private const string Base = FakeGraph.Base;

    /// <summary>"Big": 120 tasks, every 4th has attachments (30, more than one batch). "Small": 3 tasks, one with attachments.</summary>
    private static FakeGraph Account()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "Big", 120, i => new JsonObject { ["id"] = $"t{i}", ["title"] = $"Task {i}", ["hasAttachments"] = i % 4 == 0 });
        graph.AddList("L2", "Small", 3, i => new JsonObject { ["id"] = $"s{i}", ["title"] = $"Small {i}", ["hasAttachments"] = i == 1 });
        graph.Steps["t1"] = new JsonArray((JsonNode)new JsonObject { ["displayName"] = "step", ["isChecked"] = false });
        foreach (var i in Enumerable.Range(0, 120).Where(i => i % 4 == 0))
        {
            graph.Attachments[$"t{i}"] = new JsonArray((JsonNode)new JsonObject { ["id"] = $"a{i}", ["name"] = $"file{i}.pdf", ["size"] = 10 });
        }

        graph.Attachments["s1"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "as1", ["name"] = "small.pdf", ["size"] = 10 });
        return graph;
    }

    private static GraphTodoReader Reader(FakeGraph graph) =>
        new(new HttpClient(graph), (_, _) => Task.CompletedTask, () => DateTimeOffset.UnixEpoch);

    private static List<JsonObject> Tasks(TodoArchiveDocument doc) =>
        doc.Lists.OfType<JsonObject>().SelectMany(l => l["tasks"]!.AsArray()).Select(t => t!.AsObject()).ToList();

    private static int AttachmentPosts(FakeGraph graph) =>
        graph.Posts.Count(p => p.Body!["requests"]!.AsArray().Any(r => ((string)r!["url"]!).EndsWith("/attachments", StringComparison.Ordinal)));

    private static void AssertDenied(TodoArchiveDocument doc)
    {
        Assert.False(doc.ReadNotes.Interrupted);
        Assert.True(doc.ReadNotes.AttachmentsDenied);
        Assert.True((bool)doc.Root["readNotes"]!["attachmentsDenied"]!);
        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal(ReadErrorKinds.AttachmentsDenied, error.Kind);
        Assert.Null(error.TaskId);
        Assert.Contains("401 accessDenied", error.Message);
        Assert.Contains("Tasks.Read", error.Message);

        var tasks = Tasks(doc);
        Assert.Equal(123, tasks.Count);
        Assert.All(tasks, t => Assert.Empty(t["attachments"]!.AsArray()));
        Assert.Equal(31, tasks.Count(t => (bool?)t["hasAttachments"] == true));
        Assert.Equal("step", (string)tasks[1]["checklistItems"]![0]!["displayName"]!);
    }

    [Fact]
    public async Task Attachments_denied_in_a_batch_stops_further_attachment_requests_and_the_read_completes()
    {
        var graph = Account();
        graph.AttachmentsStatus = HttpStatusCode.Unauthorized;

        var doc = await Reader(graph).ReadAsync();

        AssertDenied(doc);
        Assert.Equal(1, AttachmentPosts(graph));
        Assert.DoesNotContain(graph.Requests, r => r.Url.EndsWith("/attachments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attachments_denied_on_a_single_get_stops_further_attachment_requests()
    {
        var graph = Account();
        graph.AttachmentsStatus = HttpStatusCode.Unauthorized;
        graph.BatchStatus = HttpStatusCode.BadRequest; // batching unavailable: individual GETs

        var doc = await Reader(graph).ReadAsync();

        AssertDenied(doc);
        Assert.Equal(1, graph.Requests.Count(r => r.Method == "GET" && r.Url.EndsWith("/attachments", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Forbidden_with_access_denied_is_also_an_attachment_denial()
    {
        var graph = Account();
        graph.AttachmentsStatus = HttpStatusCode.Forbidden;
        graph.AttachmentsErrorCode = "ErrorAccessDenied";

        var doc = await Reader(graph).ReadAsync();

        Assert.True(doc.ReadNotes.AttachmentsDenied);
        Assert.Contains("403 ErrorAccessDenied", Assert.Single(doc.ReadNotes.Errors).Message);
    }

    [Fact]
    public async Task Invalid_token_on_a_page_is_fatal_with_a_partial_archive()
    {
        var graph = Account();
        graph.Intercept = (r, _) => r.Url == $"{Base}me/todo/lists/L1/tasks?$top=100&$skip=100"
            ? FakeGraph.Json("""{ "error": { "code": "InvalidAuthenticationToken", "message": "Lifetime validation failed, the token is expired." } }""", HttpStatusCode.Unauthorized)
            : null;

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync());

        var inner = Assert.IsType<GraphRequestException>(ex.InnerException);
        Assert.True(inner.IsTokenProblem);
        Assert.Contains("401 Unauthorized: InvalidAuthenticationToken - Lifetime validation failed, the token is expired.", ex.Reason);
        Assert.Contains("missing, invalid or expired", ex.Reason);
        Assert.Equal(100, Tasks(ex.PartialArchive).Count);
        Assert.Equal(new ListReadProgress("L1", false, 100, false, false), ex.PartialArchive.ReadNotes.Progress.Single());
    }

    [Fact]
    public async Task Unauthorized_without_a_graph_error_is_a_token_problem()
    {
        var graph = Account();
        graph.Intercept = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) };

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync());

        Assert.True(Assert.IsType<GraphRequestException>(ex.InnerException).IsTokenProblem);
        Assert.Contains("expired", ex.Reason);
    }

    [Fact]
    public async Task Access_denied_on_the_lists_collection_is_fatal_with_the_tasks_read_hint()
    {
        var graph = Account();
        graph.Intercept = (r, _) => r.Url == $"{Base}me/todo/lists"
            ? FakeGraph.Json("""{ "error": { "code": "accessDenied", "message": "Access denied" } }""", HttpStatusCode.Forbidden)
            : null;

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync());

        Assert.Contains("403 Forbidden: accessDenied - Access denied", ex.Reason);
        Assert.Contains("Tasks.Read", ex.Reason);
        Assert.DoesNotContain("expired", ex.Reason);
        Assert.Empty(ex.PartialArchive.Lists);
    }

    [Fact]
    public async Task Access_denied_on_a_tasks_page_is_fatal_with_the_hint()
    {
        var graph = Account();
        graph.Intercept = (r, _) => r.Url.StartsWith($"{Base}me/todo/lists/L2/tasks?", StringComparison.Ordinal)
            ? FakeGraph.Json("""{ "error": { "code": "accessDenied", "message": "Access denied" } }""", HttpStatusCode.Unauthorized)
            : null;

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync());

        Assert.Contains("Tasks.Read", ex.Reason);
        Assert.Contains("accessDenied", ex.Reason);
        // No bisection of a denied window.
        Assert.Single(graph.Requests, r => r.Url.StartsWith($"{Base}me/todo/lists/L2/tasks?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_token_inside_a_batch_sub_response_is_fatal()
    {
        var graph = Account();
        graph.Intercept = (r, _) => r.Method == "POST"
            ? FakeGraph.Json("""
                { "responses": [ { "id": "0", "status": 401, "body": { "error": { "code": "InvalidAuthenticationToken", "message": "expired" } } } ] }
                """)
            : null;

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync());

        Assert.True(Assert.IsType<GraphRequestException>(ex.InnerException).IsTokenProblem);
        Assert.Contains("InvalidAuthenticationToken - expired", ex.Reason);
    }

    [Fact]
    public async Task Access_denied_on_steps_is_per_task_and_not_fatal()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "One", 2);
        graph.Intercept = (r, _) => r.Method == "POST" && ((string)r.Body!["requests"]![0]!["url"]!).EndsWith("/checklistItems", StringComparison.Ordinal)
            ? FakeGraph.Json("""
                { "responses": [
                  { "id": "0", "status": 401, "body": { "error": { "code": "accessDenied", "message": "Access denied" } } },
                  { "id": "1", "status": 200, "body": { "value": [] } } ] }
                """)
            : null;

        var doc = await Reader(graph).ReadAsync();

        var error = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.StepsUnreadable, "L1-t0"), (error.Kind, error.TaskId!));
        Assert.Contains("accessDenied - Access denied", error.Message);
    }

    [Fact]
    public async Task Exception_text_contains_graph_code_and_message_for_other_statuses()
    {
        var graph = Account();
        graph.Intercept = (_, _) => FakeGraph.Json("""{ "error": { "code": "InternalServerError", "message": "The server broke." } }""", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync());

        Assert.Contains("500 InternalServerError: InternalServerError - The server broke.", ex.Reason);

        var bad = GraphTodoReader.ErrorFor(HttpStatusCode.BadRequest, "invalidRequest", "Invalid request", $"{Base}me/todo/lists?$top=1");
        Assert.Equal($"Microsoft Graph request failed with 400 BadRequest: invalidRequest - Invalid request for {Base}me/todo/lists", bad.Message);
    }

    [Fact]
    public async Task Resume_retries_denied_attachments_once_and_clears_the_flag_when_it_works()
    {
        var graph = Account();
        graph.AttachmentsStatus = HttpStatusCode.Unauthorized;
        var first = TodoArchiveDocument.Parse((await Reader(graph).ReadAsync()).ToJsonString());
        Assert.True(first.ReadNotes.AttachmentsDenied);

        graph.AttachmentsStatus = null;
        var mark = graph.Requests.Count;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: first);
        var after = graph.Requests.Skip(mark).ToList();

        Assert.False(resumed.ReadNotes.AttachmentsDenied);
        Assert.Empty(resumed.ReadNotes.Errors);
        Assert.Null(resumed.Root["readNotes"]);
        var tasks = Tasks(resumed);
        Assert.Equal("file0.pdf", (string)tasks[0]["attachments"]![0]!["name"]!);
        Assert.Equal("small.pdf", (string)tasks[121]["attachments"]![0]!["name"]!);
        Assert.DoesNotContain(after, r => r.Url.Contains("/tasks?"));
        Assert.All(after.Where(r => r.Method == "POST").SelectMany(p => p.Body!["requests"]!.AsArray()),
            r => Assert.EndsWith("/attachments", (string)r!["url"]!));
    }

    [Fact]
    public async Task Resume_tries_denied_attachments_only_once_when_still_denied()
    {
        var graph = Account();
        graph.AttachmentsStatus = HttpStatusCode.Unauthorized;
        var first = await Reader(graph).ReadAsync();

        var mark = graph.Requests.Count;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: first);
        var after = graph.Requests.Skip(mark).ToList();

        Assert.Equal(1, after.Count(r => r.Method == "POST"));
        Assert.True(resumed.ReadNotes.AttachmentsDenied);
        Assert.Equal(ReadErrorKinds.AttachmentsDenied, Assert.Single(resumed.ReadNotes.Errors).Kind);
    }

    [Fact]
    public void Exporters_handle_unknown_attachment_names()
    {
        var notes = new ReadNotes(false,
            [new ReadError(ReadErrorKinds.AttachmentsDenied, null, null, null, null, null, "Microsoft Graph refused to list attachments with the read-only Tasks.Read permission (401 accessDenied).")])
        {
            AttachmentsDenied = true,
        };
        var doc = TodoArchiveDocument.Create(DateTimeOffset.UnixEpoch,
            [new ArchivedList(TestData.List("Home"), [TestData.Task("Plan garden", """{ "hasAttachments": true, "attachments": [] }""")])],
            notes);
        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);
        Assert.Equal(1, Inventory.From(archive).Totals.WithAttachments);

        var result = ExportService.Export(doc, archive, new ExportOptions());
        Assert.Contains("    - attachment: (name not available)\n", result.Files.Single(f => f.Path == "markdown/Home.md").GetText());
        Assert.Contains("Home,Plan garden,open,normal,,,,,,,,,(names not available),,", result.Files.Single(f => f.Path == "tasks.csv").GetText());

        var attachmentLosses = result.Report.Losses.Where(l => l.Kind == LossKinds.AttachmentNotCarried).ToList();
        Assert.Equal(["todoist", "markdown", "csv"], attachmentLosses.Select(l => l.Format));
        Assert.All(attachmentLosses, l => Assert.Contains("names could not be read", l.Message));

        var md = result.Files.Single(f => f.Path == "report.md").GetText();
        Assert.Contains("## Could not be read from Microsoft Graph", md);
        Assert.Contains("| attachments-denied | 1 |", md);
        Assert.Contains("refused to list attachments", md);
    }
}

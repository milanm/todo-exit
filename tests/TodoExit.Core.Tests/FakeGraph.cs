using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TodoExit.Core.Tests;

/// <summary>
/// An in-memory stand-in for the parts of Microsoft Graph the reader uses: lists, $top/$skip task pages,
/// per-task checklistItems and attachments, and $batch. Records every request. Never sleeps unless asked to.
/// </summary>
internal sealed class FakeGraph : HttpMessageHandler
{
    public const string Base = "https://graph.microsoft.com/v1.0/";

    private readonly List<FakeList> _lists = new();
    private readonly object _lock = new();
    private int _inFlightBatches;
    private int _inFlightAll;

    public sealed record Request(string Method, string Url, JsonObject? Body)
    {
        /// <summary>The Authorization header as sent, if any.</summary>
        public string? Authorization { get; init; }
    }

    public sealed class FakeList(string id, string name)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public List<JsonObject> Tasks { get; } = new();

        /// <summary>
        /// Position -> how the task breaks: "linked" (real shape: properties streamed, then linkedResources fails),
        /// "early" (breaks right after @odata.etag, before id) or "truncated" (cut mid-string inside body, no error object).
        /// </summary>
        public Dictionary<int, string> Poisoned { get; } = new();
    }

    public List<Request> Requests { get; } = new();

    /// <summary>taskId -> steps. Tasks without an entry have no steps.</summary>
    public Dictionary<string, JsonArray> Steps { get; } = new();

    /// <summary>taskId -> attachment metadata.</summary>
    public Dictionary<string, JsonArray> Attachments { get; } = new();

    /// <summary>Task ids whose next batch sub-response is 429 (one shot each).</summary>
    public HashSet<string> ThrottleOnce { get; } = new();

    /// <summary>taskId -> how many more batch sub-responses for it are 429.</summary>
    public Dictionary<string, int> ThrottleTimes { get; } = new();

    /// <summary>taskId -> Retry-After value on its throttled sub-responses. Default "4".</summary>
    public Dictionary<string, string> RetryAfterFor { get; } = new();

    /// <summary>Highest number of requests of any kind in flight at once.</summary>
    public int MaxRequestsInFlight { get; private set; }

    /// <summary>Task ids whose batch sub-response is 404.</summary>
    public HashSet<string> NotFound { get; } = new();

    /// <summary>When set, every attachments request (GET or batch sub-request) answers this status. Live: 401 accessDenied.</summary>
    public HttpStatusCode? AttachmentsStatus { get; set; }

    public string AttachmentsErrorCode { get; set; } = "accessDenied";

    /// <summary>When set, every $batch POST returns this status.</summary>
    public HttpStatusCode? BatchStatus { get; set; }

    /// <summary>Delay inside each $batch POST, to make concurrency observable.</summary>
    public TimeSpan BatchLatency { get; set; }

    /// <summary>Steps pages: when a task's steps exceed this, the sub-response carries a nextLink.</summary>
    public int StepsPageSize { get; set; } = int.MaxValue;

    /// <summary>Called before normal handling; return a response to override it.</summary>
    public Func<Request, int, HttpResponseMessage?>? Intercept { get; set; }

    public int MaxBatchesInFlight { get; private set; }

    public IEnumerable<Request> Posts => Requests.Where(r => r.Method == "POST");

    public FakeList AddList(string id, string name, int taskCount = 0, Func<int, JsonObject>? task = null)
    {
        var list = new FakeList(id, name);
        for (var i = 0; i < taskCount; i++)
        {
            list.Tasks.Add(task?.Invoke(i) ?? new JsonObject { ["id"] = $"{id}-t{i}", ["title"] = $"Task {i}", ["status"] = "notStarted" });
        }

        _lists.Add(list);
        return list;
    }

    public void RemoveList(string id) => _lists.RemoveAll(l => l.Id == id);

    /// <summary>The lists as they are now; tests change their tasks to model edits in To Do.</summary>
    public IReadOnlyList<FakeList> AddedLists => _lists;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        JsonObject? body = null;
        if (request.Content is not null)
        {
            body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();
        }

        var all = Interlocked.Increment(ref _inFlightAll);
        lock (_lock)
        {
            MaxRequestsInFlight = Math.Max(MaxRequestsInFlight, all);
        }

        try
        {
            return await HandleAsync(request, url, body, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightAll);
        }
    }

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, string url, JsonObject? body, CancellationToken cancellationToken)
    {
        var record = new Request(request.Method.Method, url, body) { Authorization = request.Headers.Authorization?.ToString() };
        int index;
        lock (_lock)
        {
            Requests.Add(record);
            index = Requests.Count - 1;
        }

        if (Intercept?.Invoke(record, index) is { } overridden)
        {
            return overridden;
        }

        if (request.Method == HttpMethod.Post && url == Base + "$batch")
        {
            var now = Interlocked.Increment(ref _inFlightBatches);
            lock (_lock)
            {
                MaxBatchesInFlight = Math.Max(MaxBatchesInFlight, now);
            }

            try
            {
                if (BatchLatency > TimeSpan.Zero)
                {
                    await Task.Delay(BatchLatency, cancellationToken);
                }

                return HandleBatch(body!);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightBatches);
            }
        }

        Assert.Equal(HttpMethod.Get, request.Method);
        var (status, text) = HandleGet(url);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    private HttpResponseMessage HandleBatch(JsonObject body)
    {
        if (BatchStatus is { } forced)
        {
            return new HttpResponseMessage(forced) { Content = new StringContent("""{ "error": { "code": "BadRequest", "message": "no batch" } }""") };
        }

        var responses = new JsonArray();
        var subs = body["requests"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.True(subs.Count <= 20);

        // Answer in reverse order: Graph does not promise any order.
        foreach (var sub in Enumerable.Reverse(subs))
        {
            var id = (string)sub["id"]!;
            var subUrl = (string)sub["url"]!;
            Assert.Equal("GET", (string)sub["method"]!);
            var taskId = subUrl.Split('/')[^2];

            lock (_lock)
            {
                var throttle = ThrottleOnce.Remove(taskId);
                if (!throttle && ThrottleTimes.TryGetValue(taskId, out var left) && left > 0)
                {
                    ThrottleTimes[taskId] = left - 1;
                    throttle = true;
                }

                if (throttle)
                {
                    responses.Add((JsonNode)new JsonObject
                    {
                        ["id"] = id,
                        ["status"] = 429,
                        ["headers"] = new JsonObject { ["Retry-After"] = RetryAfterFor.GetValueOrDefault(taskId, "4") },
                        ["body"] = new JsonObject { ["error"] = new JsonObject { ["code"] = "TooManyRequests", ["message"] = "slow down" } },
                    });
                    continue;
                }
            }

            if (NotFound.Contains(taskId))
            {
                responses.Add((JsonNode)new JsonObject
                {
                    ["id"] = id,
                    ["status"] = 404,
                    ["body"] = new JsonObject { ["error"] = new JsonObject { ["code"] = "ErrorItemNotFound", ["message"] = "gone" } },
                });
                continue;
            }

            var (status, text) = HandleGet(Base + subUrl.TrimStart('/'));
            responses.Add((JsonNode)new JsonObject
            {
                ["id"] = id,
                ["status"] = (int)status,
                ["headers"] = new JsonObject { ["Content-Type"] = "application/json" },
                ["body"] = JsonNode.Parse(text),
            });
        }

        var json = new JsonObject { ["responses"] = responses }.ToJsonString();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private (HttpStatusCode, string) HandleGet(string url)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath; // /v1.0/me/todo/lists/...
        var query = ParseQuery(uri.Query);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray(); // drop v1.0

        if (segments is ["me", "todo", "lists"])
        {
            var lists = new JsonArray();
            foreach (var l in _lists)
            {
                lists.Add((JsonNode)new JsonObject { ["id"] = l.Id, ["displayName"] = l.Name, ["isOwner"] = true, ["isShared"] = false });
            }

            return (HttpStatusCode.OK, new JsonObject { ["value"] = lists }.ToJsonString());
        }

        var list = segments.Length > 3 ? _lists.FirstOrDefault(l => l.Id == Uri.UnescapeDataString(segments[3])) : null;
        if (list is null)
        {
            return NotMocked(url);
        }

        if (segments.Length == 5 && segments[4] == "tasks")
        {
            Assert.False(query.ContainsKey("$expand"), "the reader must not use $expand");
            return TasksPage(list, query);
        }

        if (segments.Length == 7 && segments[4] == "tasks")
        {
            var taskId = Uri.UnescapeDataString(segments[5]);
            if (segments[6] == "checklistItems")
            {
                var all = Steps.TryGetValue(taskId, out var s) ? s.Select(n => n!.DeepClone()).ToList() : [];
                var skip = query.TryGetValue("$skip", out var sk) ? int.Parse(sk) : 0;
                var page = all.Skip(skip).Take(StepsPageSize).ToList();
                var result = new JsonObject { ["value"] = new JsonArray(page.ToArray()) };
                if (skip + StepsPageSize < all.Count)
                {
                    result["@odata.nextLink"] = $"{Base}me/todo/lists/{list.Id}/tasks/{taskId}/checklistItems?$skip={skip + StepsPageSize}";
                }

                return (HttpStatusCode.OK, result.ToJsonString());
            }

            if (segments[6] == "attachments" && AttachmentsStatus is { } denied)
            {
                return (denied, "{\"error\":{\"code\":\"" + AttachmentsErrorCode + "\",\"message\":\"Access denied\"}}");
            }

            if (segments[6] == "attachments")
            {
                var items = Attachments.TryGetValue(taskId, out var a) ? a.DeepClone() : new JsonArray();
                return (HttpStatusCode.OK, new JsonObject { ["value"] = items }.ToJsonString());
            }
        }

        return NotMocked(url);
    }

    private (HttpStatusCode, string) TasksPage(FakeList list, Dictionary<string, string> query)
    {
        if (query.TryGetValue("$select", out var select)
            && select.Split(',').Any(f => f.Trim() is "title" or "status"))
        {
            // Measured on a live account: any $select naming title or status is rejected for every task.
            return (HttpStatusCode.BadRequest, """{ "error": { "code": "invalidRequest", "message": "Invalid request" } }""");
        }

        var top = query.TryGetValue("$top", out var t) ? int.Parse(t) : 10;
        var skip = query.TryGetValue("$skip", out var s) ? int.Parse(s) : 0;
        var end = Math.Min(skip + top, list.Tasks.Count);
        var nextLink = end < list.Tasks.Count
            ? $"{Base}me/todo/lists/{list.Id}/tasks?$top={top}&$skip={end}"
            : null;

        var parts = new List<string>();
        for (var i = skip; i < end; i++)
        {
            if (list.Poisoned.TryGetValue(i, out var kind))
            {
                // Graph streams @odata.context and @odata.nextLink before value.
                var head = "{\"@odata.context\":\"https://graph.microsoft.com/v1.0/$metadata#tasks\","
                    + (nextLink is null ? string.Empty : $"\"@odata.nextLink\":\"{nextLink}\",")
                    + "\"value\":[" + string.Join(",", parts) + (parts.Count > 0 ? "," : string.Empty);
                return (HttpStatusCode.OK, head + BrokenTask(list.Tasks[i], kind));
            }

            parts.Add(WithInlineSteps(list.Tasks[i]).ToJsonString());
        }

        var page = "{\"value\":[" + string.Join(",", parts) + "]";
        if (nextLink is not null)
        {
            page += $",\"@odata.nextLink\":\"{nextLink}\"";
        }

        return (HttpStatusCode.OK, page + "}");
    }

    /// <summary>
    /// Like the live API: task pages carry "checklistItems" (with its @odata.context) only on tasks that have steps.
    /// Set to false to model an account where steps are not inline, so only --verify-steps finds them.
    /// </summary>
    public bool InlineSteps { get; set; } = true;

    private JsonObject WithInlineSteps(JsonObject task)
    {
        var copy = task.DeepClone().AsObject();
        if (InlineSteps && copy["id"] is JsonValue idValue && Steps.TryGetValue((string)idValue!, out var steps) && steps.Count > 0)
        {
            copy["checklistItems@odata.context"] = "https://graph.microsoft.com/v1.0/$metadata#checklistItems";
            copy["checklistItems"] = steps.DeepClone();
        }

        return copy;
    }

    public const string AppendedError =
        "{\"error\":{\"code\":\"InternalServerError\",\"message\":\"An internal server error occurred.\","
        + "\"innerError\":{\"date\":\"2026-11-20T10:00:00\",\"request-id\":\"r\",\"client-request-id\":\"c\"}}}";

    /// <summary>The task as Graph streams it when it breaks, modelled on a body captured from a live account.</summary>
    private static string BrokenTask(JsonObject task, string kind)
    {
        string[] order =
        [
            "@odata.etag", "importance", "isReminderOn", "status", "title", "createdDateTime", "lastModifiedDateTime",
            "hasAttachments", "categories", "id", "body", "completedDateTime", "dueDateTime", "reminderDateTime",
        ];

        var props = order.Where(task.ContainsKey).Select(k => $"\"{k}\":{task[k]!.ToJsonString()}").ToList();
        switch (kind)
        {
            case "early":
                return "{" + props[0] + AppendedError;
            case "truncated":
            {
                // Everything up to and including id, then body cut in the middle of its content string.
                var upTo = props.TakeWhile(p => !p.StartsWith("\"body\"", StringComparison.Ordinal)).ToList();
                return "{" + string.Join(",", upTo) + ",\"body\":{\"content\":\"Notes fo";
            }

            default:
                return "{" + string.Join(",", props)
                    + ",\"linkedResources@odata.context\":\"https://graph.microsoft.com/v1.0/$metadata#linkedResources\""
                    + ",\"linkedResources\":[" + AppendedError;
        }
    }

    private static (HttpStatusCode, string) NotMocked(string url) =>
        (HttpStatusCode.NotFound, $$"""{ "error": { "code": "NotMocked", "message": "{{url}}" } }""");

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            result[key] = eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..]);
        }

        return result;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

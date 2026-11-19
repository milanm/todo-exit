using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TodoExit.Core.Archive;

namespace TodoExit.Core.Graph;

/// <summary>Progress of a Graph read.</summary>
public sealed record ReadProgress(int ListsDone, int ListsTotal, int TasksRead, string? CurrentList);

/// <summary>A Graph request failed. The message carries the status code and Graph's error text, never a token.</summary>
public sealed class GraphRequestException : Exception
{
    public GraphRequestException(string message, HttpStatusCode? statusCode = null, string? graphErrorCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        GraphErrorCode = graphErrorCode;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? GraphErrorCode { get; }
}

/// <summary>
/// Reads every To Do list, task, checklist item and attachment metadata entry through Microsoft Graph
/// and returns them as a lossless archive. The <see cref="HttpClient"/> must already add authorization;
/// this class never sees a token. It only issues GET requests.
/// </summary>
public sealed class GraphTodoReader
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0/";
    public const int MaxRetries = 5;

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private bool _expandFallback;
    private bool _perTaskChecklists;

    /// <param name="http">An HttpClient already configured with authentication.</param>
    /// <param name="delay">Delay function used for retries. Tests inject one that does not sleep.</param>
    /// <param name="clock">Clock for the archive's exportedAt value.</param>
    public GraphTodoReader(
        HttpClient http,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _delay = delay ?? Task.Delay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True once the reader has switched to per-task checklist requests because $expand was rejected.</summary>
    public bool UsedChecklistFallback => _expandFallback;

    /// <summary>
    /// True when steps were read with one request per task, either because $expand was rejected
    /// or because Graph returned tasks without a checklistItems array.
    /// </summary>
    public bool UsedPerTaskChecklistRequests => _perTaskChecklists;

    public async Task<TodoArchiveDocument> ReadAsync(
        IProgress<ReadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var lists = await GetAllPagesAsync(BaseUrl + "me/todo/lists", cancellationToken).ConfigureAwait(false);
        var result = new List<ArchivedList>(lists.Count);
        var tasksRead = 0;

        progress?.Report(new ReadProgress(0, lists.Count, 0, null));

        for (var i = 0; i < lists.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = lists[i];
            var listId = GetString(list, "id") ?? throw new GraphRequestException("Graph returned a list without an id.");
            var listName = GetString(list, "displayName");
            progress?.Report(new ReadProgress(i, lists.Count, tasksRead, listName));

            var tasks = await ReadTasksAsync(listId, cancellationToken).ConfigureAwait(false);
            foreach (var task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var taskId = GetString(task, "id");
                if (taskId is null)
                {
                    EnsureArray(task, "checklistItems");
                    EnsureArray(task, "attachments");
                    continue;
                }

                var taskPath = $"{BaseUrl}me/todo/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}";

                // Any task without a checklistItems array gets its steps fetched directly. This covers the
                // 400 fallback and the case where Graph answers 200 but silently ignores $expand.
                if (task["checklistItems"] is not JsonArray)
                {
                    _perTaskChecklists = true;
                    var items = await GetAllPagesAsync(taskPath + "/checklistItems", cancellationToken).ConfigureAwait(false);
                    task["checklistItems"] = ToArray(items);
                }

                if (task["hasAttachments"] is JsonValue hv && hv.TryGetValue<bool>(out var hasAttachments) && hasAttachments)
                {
                    var attachments = await GetAllPagesAsync(taskPath + "/attachments", cancellationToken).ConfigureAwait(false);
                    task["attachments"] = ToArray(attachments.Select(StripAttachment));
                }
                else
                {
                    EnsureArray(task, "attachments");
                }

                tasksRead++;
                if (tasksRead % 25 == 0)
                {
                    progress?.Report(new ReadProgress(i, lists.Count, tasksRead, listName));
                }
            }

            result.Add(new ArchivedList(list, tasks));
            progress?.Report(new ReadProgress(i + 1, lists.Count, tasksRead, listName));
        }

        return TodoArchiveDocument.Create(_clock(), result);
    }

    private async Task<List<JsonObject>> ReadTasksAsync(string listId, CancellationToken cancellationToken)
    {
        var tasksUrl = $"{BaseUrl}me/todo/lists/{Uri.EscapeDataString(listId)}/tasks?$top=100";
        if (!_expandFallback)
        {
            try
            {
                return await GetAllPagesAsync(tasksUrl + "&$expand=checklistItems", cancellationToken).ConfigureAwait(false);
            }
            catch (GraphRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                // $expand=checklistItems is not documented as supported. Fall back for the rest of the run.
                _expandFallback = true;
            }
        }

        return await GetAllPagesAsync(tasksUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Attachment metadata only. contentBytes is never requested, but drop it defensively if present.</summary>
    private static JsonObject StripAttachment(JsonObject attachment)
    {
        attachment.Remove("contentBytes");
        return attachment;
    }

    private async Task<List<JsonObject>> GetAllPagesAsync(string url, CancellationToken cancellationToken)
    {
        var items = new List<JsonObject>();
        string? next = url;
        while (next is not null)
        {
            var page = await GetJsonAsync(next, cancellationToken).ConfigureAwait(false);
            if (page["value"] is JsonArray values)
            {
                // Detach nodes from the page so they can be re-parented into the archive.
                var detached = values.ToList();
                values.Clear();
                foreach (var v in detached)
                {
                    if (v is JsonObject o)
                    {
                        items.Add(o);
                    }
                }
            }

            next = GetString(page, "@odata.nextLink");
            if (next is not null)
            {
                EnsureTrustedNextLink(next);
            }
        }

        return items;
    }

    private async Task<JsonObject> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return JsonNode.Parse(body) as JsonObject
                        ?? throw new GraphRequestException($"Graph returned a non-object response for {DescribeUrl(url)}.", response.StatusCode);
                }
                catch (JsonException ex)
                {
                    throw new GraphRequestException($"Graph returned invalid JSON for {DescribeUrl(url)}: {ex.Message}", response.StatusCode);
                }
            }

            var status = response.StatusCode;
            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt >= MaxRetries)
                {
                    throw new GraphRequestException(
                        $"Microsoft Graph kept throttling the request ({(int)status} {status}) after {MaxRetries} retries: {DescribeUrl(url)}. Try again later.",
                        status);
                }

                await _delay(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var errorBody = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
            var (code, message) = ParseGraphError(errorBody);
            var text = $"Microsoft Graph request failed with {(int)status} {status} for {DescribeUrl(url)}";
            if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(message))
            {
                text += $": {code}{(code is not null && message is not null ? " - " : string.Empty)}{message}";
            }

            throw new GraphRequestException(text, status, code);
        }
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = null;
        if (retryAfter?.Delta is { } delta)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        // No usable header: back off 2, 4, 8, ... seconds.
        var value = delay ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value > MaxRetryDelay ? MaxRetryDelay : value;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
    }

    internal static (string? Code, string? Message) ParseGraphError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                return (code, message);
            }
        }
        catch (JsonException)
        {
            // Not JSON; ignore the body.
        }

        return (null, null);
    }

    /// <summary>
    /// The HttpClient may carry a bearer token as a default header, so a next link is only followed
    /// when it points at the same https host as <see cref="BaseUrl"/>.
    /// </summary>
    internal static void EnsureTrustedNextLink(string nextLink)
    {
        var expected = new Uri(BaseUrl);
        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort)
        {
            throw new GraphRequestException(
                $"Refusing to follow @odata.nextLink outside {expected.Scheme}://{expected.Host}: {DescribeUrl(nextLink)}");
        }
    }

    /// <summary>Path without query string, for error messages.</summary>
    private static string DescribeUrl(string url)
    {
        var q = url.IndexOf('?');
        return q < 0 ? url : url[..q];
    }

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static void EnsureArray(JsonObject obj, string name)
    {
        if (obj[name] is not JsonArray)
        {
            obj[name] = new JsonArray();
        }
    }

    private static JsonArray ToArray(IEnumerable<JsonObject> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add((JsonNode)item);
        }

        return array;
    }
}

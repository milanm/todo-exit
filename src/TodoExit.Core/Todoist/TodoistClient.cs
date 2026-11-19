using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TodoExit.Core.Todoist;

/// <summary>One Sync API command. <paramref name="TempId"/> is set for commands that create an object.</summary>
public sealed record TodoistCommand(string Type, string Uuid, string? TempId, JsonObject Args);

/// <summary>Result of one command: "ok", or Todoist's error object.</summary>
public sealed record TodoistCommandResult(bool Ok, string? ErrorTag = null, int? ErrorCode = null, string? Error = null, int? HttpCode = null)
{
    public static TodoistCommandResult Success { get; } = new(true);
}

public sealed record TodoistSyncResult(
    IReadOnlyDictionary<string, TodoistCommandResult> Status,
    IReadOnlyDictionary<string, string> TempIdMapping);

public sealed record TodoistProject(string Id, string Name, bool IsInbox, bool IsArchived);

/// <summary>An active task as the full sync returns it. Used to recover ids after a lost response.</summary>
public sealed record TodoistItem(string Id, string ProjectId, string? ParentId, string Content, long ChildOrder);

/// <summary>What the transfer needs to know about the account. Read with one read-only sync request.</summary>
public sealed record TodoistAccount(
    string? Email,
    bool IsPremium,
    string? PlanName,
    string? TimeZone,
    int? MaxProjects,
    int? MaxTasks,
    int? MaxLabels,
    bool Reminders,
    IReadOnlyList<TodoistProject> Projects)
{
    /// <summary>Projects that count against max_projects: everything except the Inbox and archived projects.</summary>
    public int CountedProjects => Projects.Count(p => !p.IsInbox && !p.IsArchived);
}

public enum TodoistFailure
{
    /// <summary>HTTP 401 or 403: the token was rejected.</summary>
    TokenRejected,

    /// <summary>HTTP 400 or another 4xx for the whole request. Retrying would not help.</summary>
    BadRequest,

    /// <summary>429, 5xx, network failures or timeouts, still failing after the last attempt.</summary>
    Unavailable,

    /// <summary>A redirect or a response that is not Sync API JSON.</summary>
    UnexpectedResponse,
}

public sealed class TodoistApiException(TodoistFailure failure, string message, int? statusCode = null, string? errorTag = null)
    : Exception(message)
{
    public TodoistFailure Failure { get; } = failure;

    public int? StatusCode { get; } = statusCode;

    public string? ErrorTag { get; } = errorTag;
}

/// <summary>
/// Minimal client for POST https://api.todoist.com/api/v1/sync. The HttpClient is injected (CLI and browser).
/// Sends only the Authorization and Content-Type headers, so the browser's CORS preflight passes. Requests that
/// fail with 429, 5xx, a network error or a timeout are sent again with the same body (same command uuids,
/// which Todoist treats as already done), up to <see cref="MaxAttempts"/> times.
/// </summary>
public sealed class TodoistClient
{
    public const string SyncUrl = "https://api.todoist.com/api/v1/sync";
    public const int MaxCommandsPerRequest = 100;

    private static readonly Uri SyncUri = new(SyncUrl);

    private readonly HttpClient _http;
    private readonly TodoistToken _token;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Delay used between attempts. Tests inject one that does not sleep.</param>
    public TodoistClient(HttpClient http, TodoistToken token, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Attempts per request, the first one included.</summary>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>Longest wait honoured from retry_after.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Called before each wait with its length and a short reason.</summary>
    public Action<TimeSpan, string>? Waiting { get; set; }

    /// <summary>The token goes only to https://api.todoist.com on the default port.</summary>
    public static bool IsTodoist(Uri? uri) =>
        uri is not null
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "api.todoist.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort;

    public override string ToString() => "TodoistClient";

    /// <summary>Reads user, plan limits and projects. Read-only.</summary>
    public async Task<TodoistAccount> ReadAccountAsync(CancellationToken cancellationToken)
    {
        var root = await ReadAsync(["user", "user_plan_limits", "projects"], cancellationToken).ConfigureAwait(false);
        var user = root["user"] as JsonObject;
        var current = (root["user_plan_limits"] as JsonObject)?["current"] as JsonObject;
        return new TodoistAccount(
            Email: Str(user, "email"),
            IsPremium: Bool(user, "is_premium") ?? false,
            PlanName: Str(current, "plan_name"),
            TimeZone: Str((user?["tz_info"] as JsonObject), "timezone"),
            MaxProjects: Int(current, "max_projects"),
            MaxTasks: Int(current, "max_tasks"),
            MaxLabels: Int(current, "max_labels"),
            Reminders: Bool(current, "reminders") ?? false,
            Projects: ParseProjects(root["projects"]));
    }

    /// <summary>Reads all active projects and tasks. Read-only. Used to find ids a lost response did not deliver.</summary>
    public async Task<(IReadOnlyList<TodoistProject> Projects, IReadOnlyList<TodoistItem> Items)> ReadProjectsAndItemsAsync(
        CancellationToken cancellationToken)
    {
        var root = await ReadAsync(["projects", "items"], cancellationToken).ConfigureAwait(false);
        var items = new List<TodoistItem>();
        if (root["items"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                if (Bool(item, "is_deleted") == true || Bool(item, "checked") == true || Id(item, "id") is not { } id
                    || Id(item, "project_id") is not { } projectId)
                {
                    continue;
                }

                items.Add(new TodoistItem(id, projectId, Id(item, "parent_id"), Str(item, "content") ?? string.Empty,
                    Long(item, "child_order") ?? 0));
            }
        }

        return (ParseProjects(root["projects"]), items);
    }

    public async Task<JsonObject> ReadAsync(IReadOnlyList<string> resourceTypes, CancellationToken cancellationToken)
    {
        var types = new JsonArray(resourceTypes.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        return await PostAsync(
            [new("sync_token", "*"), new("resource_types", types.ToJsonString())], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends at most <see cref="MaxCommandsPerRequest"/> commands in one request.</summary>
    public async Task<TodoistSyncResult> ExecuteAsync(IReadOnlyList<TodoistCommand> commands, CancellationToken cancellationToken)
    {
        if (commands.Count == 0 || commands.Count > MaxCommandsPerRequest)
        {
            throw new ArgumentOutOfRangeException(nameof(commands), $"Send between 1 and {MaxCommandsPerRequest} commands per request.");
        }

        var root = await PostAsync([new("commands", SerializeCommands(commands))], cancellationToken).ConfigureAwait(false);
        var status = new Dictionary<string, TodoistCommandResult>(StringComparer.Ordinal);
        if (root["sync_status"] is JsonObject syncStatus)
        {
            foreach (var (uuid, value) in syncStatus)
            {
                status[uuid] = value is JsonValue v && v.TryGetValue<string>(out var s) && s == "ok"
                    ? TodoistCommandResult.Success
                    : value is JsonObject e
                        ? new TodoistCommandResult(false, Str(e, "error_tag"), Int(e, "error_code"), Str(e, "error"), Int(e, "http_code"))
                        : new TodoistCommandResult(false, "UNKNOWN", null, value?.ToJsonString());
            }
        }

        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root["temp_id_mapping"] is JsonObject map)
        {
            foreach (var (tempId, value) in map)
            {
                if (value is JsonValue v && Scalar(v) is { } id)
                {
                    mapping[tempId] = id;
                }
            }
        }

        return new TodoistSyncResult(status, mapping);
    }

    internal static string SerializeCommands(IReadOnlyList<TodoistCommand> commands)
    {
        var array = new JsonArray();
        foreach (var c in commands)
        {
            var o = new JsonObject { ["type"] = c.Type, ["uuid"] = c.Uuid };
            if (c.TempId is not null)
            {
                o["temp_id"] = c.TempId;
            }

            o["args"] = c.Args.DeepClone();
            array.Add((JsonNode)o);
        }

        return array.ToJsonString();
    }

    private async Task<JsonObject> PostAsync(IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        byte[] body;
        using (var content = new FormUrlEncodedContent(form))
        {
            body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string reason;
            TimeSpan? retryAfter = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, SyncUri) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                if (IsTodoist(request.RequestUri))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Value);
                }

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    if (TryParseObject(text) is { } root)
                    {
                        return root;
                    }

                    reason = "Todoist sent a response that is not JSON";
                }
                else if (code is 401 or 403)
                {
                    throw new TodoistApiException(TodoistFailure.TokenRejected,
                        $"Todoist rejected the token (HTTP {code}). Check the API token in Todoist Settings > Integrations > Developer.", code);
                }
                else if (code is >= 300 and < 400)
                {
                    throw new TodoistApiException(TodoistFailure.UnexpectedResponse,
                        $"Todoist answered with a redirect (HTTP {code}); it was not followed.", code);
                }
                else if (code == 429 || code >= 500)
                {
                    var (tag, message, after) = ErrorOf(text);
                    retryAfter = after ?? HeaderRetryAfter(response);
                    reason = $"HTTP {code}{(tag is null ? string.Empty : " " + tag)}{(message is null ? string.Empty : ": " + message)}";
                }
                else
                {
                    var (tag, message, _) = ErrorOf(text);
                    throw new TodoistApiException(TodoistFailure.BadRequest,
                        $"Todoist refused the request (HTTP {code}{(tag is null ? string.Empty : " " + tag)}){(message is null ? "." : ": " + message)}",
                        code, tag);
                }
            }
            catch (HttpRequestException ex)
            {
                reason = "network error: " + ex.Message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                reason = "the request timed out";
            }

            if (attempt >= MaxAttempts)
            {
                throw new TodoistApiException(TodoistFailure.Unavailable, $"Todoist did not answer after {attempt} attempts ({reason}).");
            }

            var wait = retryAfter ?? Backoff(attempt);
            if (wait > MaxWait)
            {
                wait = MaxWait;
            }

            Waiting?.Invoke(wait, reason);
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>2, 4, 8, 16, 32, 60, 60 ... seconds.</summary>
    internal static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));

    private static (string? Tag, string? Message, TimeSpan? RetryAfter) ErrorOf(string text)
    {
        if (TryParseObject(text) is not { } root)
        {
            return (null, null, null);
        }

        TimeSpan? after = null;
        if (root["error_extra"] is JsonObject extra && Long(extra, "retry_after") is { } seconds && seconds >= 0)
        {
            after = TimeSpan.FromSeconds(seconds);
        }

        return (Str(root, "error_tag"), Str(root, "error"), after);
    }

    private static TimeSpan? HeaderRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date && date - DateTimeOffset.UtcNow is { } d && d > TimeSpan.Zero ? d : null;
    }

    private static JsonObject? TryParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<TodoistProject> ParseProjects(JsonNode? node)
    {
        var projects = new List<TodoistProject>();
        if (node is not JsonArray array)
        {
            return projects;
        }

        foreach (var p in array.OfType<JsonObject>())
        {
            if (Bool(p, "is_deleted") == true || Id(p, "id") is not { } id)
            {
                continue;
            }

            projects.Add(new TodoistProject(id, Str(p, "name") ?? string.Empty, Bool(p, "inbox_project") ?? false, Bool(p, "is_archived") ?? false));
        }

        return projects;
    }

    private static string? Scalar(JsonValue v) =>
        v.TryGetValue<string>(out var s) ? s : v.TryGetValue<long>(out var l) ? l.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>Ids are strings in API v1; numbers are accepted too.</summary>
    private static string? Id(JsonObject? o, string name) => o?[name] is JsonValue v ? Scalar(v) : null;

    private static string? Str(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static long? Long(JsonObject? o, string name)
    {
        if (o?[name] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (v.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            return (long)d;
        }

        return v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static int? Int(JsonObject? o, string name) => Long(o, name) is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
}

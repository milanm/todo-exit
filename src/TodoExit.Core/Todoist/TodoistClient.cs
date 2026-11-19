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

    /// <summary>error_extra.retry_after of the command's error, when Todoist sent one.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// The command failed for a reason that may go away: http_code 408, 429 or 5xx. Every other error is taken as a
    /// refusal that a retry would not change.
    /// </summary>
    public bool IsTemporary => !Ok && HttpCode is 408 or 429 or >= 500;
}

public sealed record TodoistSyncResult(
    IReadOnlyDictionary<string, TodoistCommandResult> Status,
    IReadOnlyDictionary<string, string> TempIdMapping);

public sealed record TodoistProject(string Id, string Name, bool IsInbox, bool IsArchived)
{
    /// <summary>
    /// workspace_id of a team project. Null for a personal project: on a personal account the key is not sent at all.
    /// </summary>
    public string? WorkspaceId { get; init; }

    public bool IsPersonal => WorkspaceId is null;
}

/// <summary>A task as a sync read returns it. Used to recover ids after a lost response.</summary>
public sealed record TodoistItem(string Id, string ProjectId, string? ParentId, string Content, long ChildOrder)
{
    /// <summary>Completed. A full sync lists only open tasks; an incremental sync can return completed ones.</summary>
    public bool Checked { get; init; }
}

/// <summary>A reminder as a sync read returns it.</summary>
public sealed record TodoistReminder(string Id, string ItemId, string? Type, string? DueDate);

/// <summary>
/// What one sync read returned: everything (full sync) or only what changed since a sync_token (incremental sync).
/// Deleted objects are left out.
/// </summary>
public sealed record TodoistChanges(
    bool FullSync,
    string? SyncToken,
    string? FullSyncDateUtc,
    IReadOnlyList<TodoistProject> Projects,
    IReadOnlyList<TodoistItem> Items,
    IReadOnlyList<TodoistReminder> Reminders)
{
    /// <summary>Ids that this read reported as deleted.</summary>
    public IReadOnlySet<string> DeletedIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

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
    /// <summary>Todoist's id of the user (user.id, a string). A transfer is bound to it.</summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Projects that count against max_projects: personal projects except the Inbox and archived ones. Team
    /// (workspace) projects have limits of their own and are not counted.
    /// </summary>
    public int CountedProjects => Projects.Count(p => !p.IsInbox && !p.IsArchived && p.IsPersonal);

    /// <summary>Active team (workspace) projects. Not counted against max_projects.</summary>
    public int TeamProjects => Projects.Count(p => !p.IsArchived && !p.IsPersonal);
}

/// <summary>Who a token belongs to: user.id and email, and the projects when they were asked for.</summary>
public sealed record TodoistIdentity(string? UserId, string? Email, IReadOnlyList<TodoistProject> Projects);

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
            Projects: ParseProjects(root["projects"]))
        {
            UserId = Id(user, "id"),
        };
    }

    /// <summary>Reads who the token belongs to, and the projects when <paramref name="withProjects"/>. Read-only, one request.</summary>
    public async Task<TodoistIdentity> ReadIdentityAsync(bool withProjects, CancellationToken cancellationToken)
    {
        var root = await ReadAsync(withProjects ? ["user", "projects"] : ["user"], cancellationToken).ConfigureAwait(false);
        var user = root["user"] as JsonObject;
        return new TodoistIdentity(Id(user, "id"), Str(user, "email"), withProjects ? ParseProjects(root["projects"]) : []);
    }

    /// <summary>
    /// Takes a sync_token before a transfer writes anything (sync_token "*", projects and tasks). Read-only. Later reads
    /// with this token return what changed since, the transfer's own objects included.
    /// </summary>
    public Task<TodoistChanges> ReadSyncTokenAsync(CancellationToken cancellationToken) =>
        ReadChangesAsync("*", reminders: false, cancellationToken);

    /// <summary>
    /// Reads projects and tasks, and reminders when asked: all of them for <paramref name="syncToken"/> "*", otherwise
    /// what changed since that token. Read-only.
    /// </summary>
    public async Task<TodoistChanges> ReadChangesAsync(string syncToken, bool reminders, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> types = reminders ? ["projects", "items", "reminders"] : ["projects", "items"];
        var root = await ReadAsync(types, syncToken, cancellationToken).ConfigureAwait(false);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<TodoistItem>();
        if (root["items"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                if (Id(item, "id") is not { } id)
                {
                    continue;
                }

                if (Bool(item, "is_deleted") == true || Id(item, "project_id") is not { } projectId)
                {
                    deleted.Add(id);
                    continue;
                }

                items.Add(new TodoistItem(id, projectId, Id(item, "parent_id"), Str(item, "content") ?? string.Empty,
                    Long(item, "child_order") ?? 0) { Checked = Bool(item, "checked") == true });
            }
        }

        var found = new List<TodoistReminder>();
        if (root["reminders"] is JsonArray reminderArray)
        {
            foreach (var r in reminderArray.OfType<JsonObject>())
            {
                if (Id(r, "id") is not { } id)
                {
                    continue;
                }

                if (Bool(r, "is_deleted") == true || Id(r, "item_id") is not { } itemId)
                {
                    deleted.Add(id);
                    continue;
                }

                found.Add(new TodoistReminder(id, itemId, Str(r, "type"), Str(r["due"] as JsonObject, "date")));
            }
        }

        if (root["projects"] is JsonArray projectArray)
        {
            foreach (var p in projectArray.OfType<JsonObject>())
            {
                if (Bool(p, "is_deleted") == true && Id(p, "id") is { } id)
                {
                    deleted.Add(id);
                }
            }
        }

        return new TodoistChanges(Bool(root, "full_sync") ?? syncToken == "*", Str(root, "sync_token"), Str(root, "full_sync_date_utc"),
            ParseProjects(root["projects"]), items, found)
        {
            DeletedIds = deleted,
        };
    }

    /// <summary>Waits before a retry, through the delay given to the constructor, and tells <see cref="Waiting"/>.</summary>
    public async Task WaitAsync(TimeSpan wait, string reason, CancellationToken cancellationToken)
    {
        if (wait > MaxWait)
        {
            wait = MaxWait;
        }

        Waiting?.Invoke(wait, reason);
        await _delay(wait, cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonObject> ReadAsync(IReadOnlyList<string> resourceTypes, CancellationToken cancellationToken) =>
        ReadAsync(resourceTypes, "*", cancellationToken);

    private async Task<JsonObject> ReadAsync(IReadOnlyList<string> resourceTypes, string syncToken, CancellationToken cancellationToken)
    {
        var types = new JsonArray(resourceTypes.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        return await PostAsync(
            [new("sync_token", syncToken), new("resource_types", types.ToJsonString())], cancellationToken).ConfigureAwait(false);
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
                        {
                            RetryAfter = (e["error_extra"] as JsonObject) is { } extra && Long(extra, "retry_after") is { } seconds && seconds >= 0
                                ? TimeSpan.FromSeconds(seconds)
                                : null,
                        }
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

            projects.Add(new TodoistProject(id, Str(p, "name") ?? string.Empty, Bool(p, "inbox_project") ?? false, Bool(p, "is_archived") ?? false)
            {
                WorkspaceId = Id(p, "workspace_id") is { Length: > 0 } workspace ? workspace : null,
            });
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

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TodoExit.Core.Tests;

/// <summary>
/// In-memory Todoist Sync API (POST https://api.todoist.com/api/v1/sync) with the behaviour measured on a real
/// free account: form-encoded body, temp_ids resolved inside one request, real ids across requests, at most 100
/// commands (HTTP 400 LIMITS_REACHED_COMMANDS, nothing executed), a repeated uuid answers "ok" with no
/// temp_id_mapping and creates nothing, reminders are PREMIUM_ONLY when the plan has none, an optional active-task
/// limit per project gives per-command errors, item_complete with date_completed, 429 with error_extra.retry_after,
/// and 401 for a wrong token. Can execute a request and then fail instead of answering.
/// Reads: sync_token "*" is a full sync (open tasks only) that returns a sync_token; a later read with that token
/// returns only what changed since (completed tasks included), as measured. A full sync can be made to lag behind
/// (<see cref="FullSyncFrozenAt"/>), which Todoist documents for large accounts. Personal projects have no
/// workspace_id key, as measured; team projects have one. Not measured, modelled as the docs describe: a command can
/// fail on its own with an http_code of 503 (<see cref="Transient"/>), or run and still answer 503
/// (<see cref="AppliedButFailed"/>). Every answer, errors included, is remembered for its uuid.
/// </summary>
internal sealed class FakeTodoist : HttpMessageHandler
{
    public const string Token = "secret-token-1234567890";

    private readonly object _lock = new();
    private readonly Dictionary<string, JsonNode> _seen = new(StringComparer.Ordinal);
    private int _nextId = 1000;
    private long _seq;

    public sealed class Project
    {
        public required string Id { get; init; }

        public required string Name { get; set; }

        public bool Inbox { get; init; }

        public bool CreatedByTransfer { get; init; }

        /// <summary>Set for a team (workspace) project.</summary>
        public string? WorkspaceId { get; init; }

        /// <summary>Send "workspace_id": null instead of leaving the key out.</summary>
        public bool NullWorkspaceKey { get; init; }

        /// <summary>Change sequence numbers; -1 until the fake assigns one.</summary>
        public long Created { get; set; } = -1;

        public long Changed { get; set; } = -1;
    }

    public sealed class Item
    {
        public required string Id { get; init; }

        public required string ProjectId { get; init; }

        public string? ParentId { get; init; }

        public required string Content { get; set; }

        public string? Description { get; init; }

        public int Priority { get; init; }

        public List<string> Labels { get; init; } = new();

        public JsonObject? Due { get; init; }

        public bool Checked { get; set; }

        public string? CompletedAt { get; set; }

        public long ChildOrder { get; init; }

        public long Created { get; set; } = -1;

        public long Changed { get; set; } = -1;
    }

    public sealed record Reminder(string ItemId, JsonObject Args)
    {
        public string Id { get; init; } = string.Empty;

        public long Created { get; set; } = -1;
    }

    public sealed record Request(string Url, string? Authorization, IReadOnlyDictionary<string, string> Form)
    {
        public JsonArray? Commands => Form.TryGetValue("commands", out var c) ? JsonNode.Parse(c)!.AsArray() : null;
    }

    public List<Project> Projects { get; } = [new Project { Id = "inbox-1", Name = "Inbox", Inbox = true }];

    public List<Item> Items { get; } = new();

    public List<Reminder> Reminders { get; } = new();

    public List<Request> Requests { get; } = new();

    /// <summary>The token this account accepts. Several fakes behind <see cref="TodoistAccounts"/> model several accounts.</summary>
    public string AcceptedToken { get; init; } = Token;

    /// <summary>user.id as the live API returns it: a string (8 characters on the test account).</summary>
    public string UserId { get; set; } = "51234567";

    public string Email { get; set; } = "tester@example.com";

    public string PlanName { get; set; } = "Beginner";

    public int MaxProjects { get; set; } = 5;

    public int MaxTasks { get; set; } = 300;

    public bool RemindersAllowed { get; set; }

    /// <summary>When set, item_add into a project with this many active tasks fails per command.</summary>
    public int? ActiveLimit { get; set; }

    /// <summary>item_add with this content fails per command with this error tag.</summary>
    public Dictionary<string, string> FailContent { get; } = new(StringComparer.Ordinal);

    /// <summary>The next N write requests answer 429 with retry_after, without executing.</summary>
    public int Throttle { get; set; }

    public int RetryAfterSeconds { get; set; } = 7;

    /// <summary>The next N write requests answer 503 without executing.</summary>
    public int ServerErrors { get; set; }

    /// <summary>Write request numbers (1-based) that are executed but then time out instead of answering.</summary>
    public HashSet<int> LoseResponse { get; } = new();

    /// <summary>Write request numbers (1-based) that time out before anything is executed.</summary>
    public HashSet<int> DropRequest { get; } = new();

    /// <summary>
    /// When set, a read of the tasks first rewrites each bare address in their content into a titled Markdown link,
    /// for good, as Todoist does some time after a task is created: "https://www.learndash.com/" becomes
    /// "[Page title of www.learndash.com](https://www.learndash.com/)".
    /// </summary>
    public bool RewriteBareUrls { get; set; }

    /// <summary>
    /// "type:label" (label: the project name for project_add, the task content otherwise) to how many more times the
    /// command fails on its own with http_code 503, without running.
    /// </summary>
    public Dictionary<string, int> Transient { get; } = new(StringComparer.Ordinal);

    /// <summary>"type:label" to how many more times the command runs but answers http_code 503 and no temp_id_mapping.</summary>
    public Dictionary<string, int> AppliedButFailed { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, a full sync shows the account as it was at this change number, with the sync_token of that point.</summary>
    public long? FullSyncFrozenAt { get; set; }

    /// <summary>Tasks with this content are left out of every read, as if Todoist had not indexed them yet.</summary>
    public HashSet<string> HiddenFromReads { get; } = new(StringComparer.Ordinal);

    /// <summary>The current change number.</summary>
    public long Sequence => _seq;

    /// <summary>Freezes full syncs at the current state of the account.</summary>
    public void FreezeFullSync()
    {
        lock (_lock)
        {
            Stamp();
            FullSyncFrozenAt = _seq;
        }
    }

    /// <summary>When set, every request answers with a redirect to this address.</summary>
    public string? RedirectTo { get; set; }

    public int WriteRequests { get; private set; }

    public IEnumerable<Request> Writes => Requests.Where(r => r.Form.ContainsKey("commands"));

    public IEnumerable<Item> Active => Items.Where(i => !i.Checked);

    public Project AddProject(string name)
    {
        var p = new Project { Id = NewId("p"), Name = name };
        Projects.Add(p);
        return p;
    }

    public Project AddTeamProject(string name, string workspaceId = "ws-1")
    {
        var p = new Project { Id = NewId("p"), Name = name, WorkspaceId = workspaceId };
        Projects.Add(p);
        return p;
    }

    public Project ProjectNamed(string name) => Projects.Single(p => p.Name == name);

    public List<Item> ItemsIn(string projectName) => Items.Where(i => i.ProjectId == ProjectNamed(projectName).Id).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var form = ParseForm(body);
        var auth = request.Headers.Authorization is { } a ? $"{a.Scheme} {a.Parameter}" : null;
        lock (_lock)
        {
            Requests.Add(new Request(request.RequestUri!.ToString(), auth, form));
        }

        if (RedirectTo is not null)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(RedirectTo);
            return redirect;
        }

        if (request.RequestUri!.ToString() != "https://api.todoist.com/api/v1/sync" || request.Method != HttpMethod.Post)
        {
            return Json("""{"error":"not found"}""", HttpStatusCode.NotFound);
        }

        if (auth != "Bearer " + AcceptedToken)
        {
            return Json("""{"error":"Unauthorized","error_tag":"AUTH_INVALID_TOKEN","http_code":401}""", HttpStatusCode.Unauthorized);
        }

        if (!form.TryGetValue("commands", out var commandsText))
        {
            lock (_lock)
            {
                return Json(Read(form).ToJsonString());
            }
        }

        if (ServerErrors > 0)
        {
            ServerErrors--;
            return Json("""{"error":"Service unavailable"}""", HttpStatusCode.ServiceUnavailable);
        }

        var commands = JsonNode.Parse(commandsText)!.AsArray();
        if (commands.Count > 100)
        {
            return Json("""{"error":"Too many commands","error_code":36,"error_extra":{"retry_after":3},"error_tag":"LIMITS_REACHED_COMMANDS","http_code":400}""",
                HttpStatusCode.BadRequest);
        }

        if (Throttle > 0)
        {
            Throttle--;
            return Json($$"""{"error":"Too many requests","error_code":35,"error_extra":{"retry_after":{{RetryAfterSeconds}}},"error_tag":"LIMITS_REACHED","http_code":429}""",
                (HttpStatusCode)429);
        }

        int number;
        lock (_lock)
        {
            number = ++WriteRequests;
        }

        if (DropRequest.Remove(number))
        {
            throw new TaskCanceledException("timeout", new TimeoutException());
        }

        JsonObject response;
        lock (_lock)
        {
            Stamp();
            response = Execute(commands);
        }

        if (LoseResponse.Remove(number))
        {
            throw new TaskCanceledException("timeout", new TimeoutException());
        }

        return Json(response.ToJsonString());
    }

    private JsonObject Execute(JsonArray commands)
    {
        var status = new JsonObject();
        var mapping = new JsonObject();
        var temp = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in commands.Select(n => n!.AsObject()))
        {
            var uuid = (string)c["uuid"]!;
            if (_seen.TryGetValue(uuid, out var earlier))
            {
                status[uuid] = earlier.DeepClone();
                continue;
            }

            var result = Run(c, temp, mapping);
            _seen[uuid] = result.DeepClone();
            status[uuid] = result;
        }

        return new JsonObject { ["sync_status"] = status, ["temp_id_mapping"] = mapping, ["full_sync"] = false };
    }

    private JsonNode Run(JsonObject c, Dictionary<string, string> temp, JsonObject mapping)
    {
        var type = (string)c["type"]!;
        var args = c["args"]!.AsObject();
        var tempId = (string?)c["temp_id"];
        string? Resolve(string? id) => id is null ? null : temp.TryGetValue(id, out var real) ? real : id;
        void Map(string id)
        {
            if (tempId is not null)
            {
                temp[tempId] = id;
                mapping[tempId] = id;
            }
        }

        var label = type switch
        {
            "project_add" => (string?)args["name"],
            "item_add" => (string?)args["content"],
            "item_complete" => Items.FirstOrDefault(i => i.Id == Resolve((string?)args["id"]))?.Content,
            "reminder_add" => Items.FirstOrDefault(i => i.Id == Resolve((string?)args["item_id"]))?.Content,
            _ => null,
        };
        var failure = $"{type}:{label}";
        if (Transient.TryGetValue(failure, out var left) && left > 0)
        {
            Transient[failure] = left - 1;
            return Error("SERVICE_UNAVAILABLE", 0, "Service unavailable", 503);
        }

        var result = RunCommand(type, args, tempId, temp, mapping, Resolve, Map);
        if (AppliedButFailed.TryGetValue(failure, out var applied) && applied > 0 && result is JsonValue)
        {
            AppliedButFailed[failure] = applied - 1;
            if (tempId is not null)
            {
                mapping.Remove(tempId);
            }

            return Error("SERVICE_UNAVAILABLE", 0, "Service unavailable", 503);
        }

        return result;
    }

    private JsonNode RunCommand(
        string type, JsonObject args, string? tempId, Dictionary<string, string> temp, JsonObject mapping,
        Func<string?, string?> Resolve, Action<string> Map)
    {
        switch (type)
        {
            case "project_add":
            {
                var p = new Project { Id = NewId("p"), Name = (string)args["name"]!, CreatedByTransfer = true, Created = ++_seq, Changed = _seq };
                Projects.Add(p);
                Map(p.Id);
                return "ok";
            }

            case "item_add":
            {
                var projectId = Resolve((string?)args["project_id"]);
                var parentId = Resolve((string?)args["parent_id"]);
                if (Projects.All(p => p.Id != projectId) || (parentId is not null && Items.All(i => i.Id != parentId)))
                {
                    return Error("INVALID_TEMPID", 15, "Invalid temporary id", 400);
                }

                var content = (string)args["content"]!;
                if (FailContent.TryGetValue(content, out var tag))
                {
                    return Error(tag, 20, "Refused by the test", 400);
                }

                if (ActiveLimit is { } limit && Active.Count(i => i.ProjectId == projectId) >= limit)
                {
                    return Error("MAX_ITEMS_LIMIT_REACHED", 48, "Maximum number of items per user project limit reached", 403);
                }

                var item = new Item
                {
                    Id = NewId("i"),
                    ProjectId = projectId!,
                    ParentId = parentId,
                    Content = content,
                    Description = (string?)args["description"],
                    Priority = (int?)args["priority"] ?? 1,
                    Labels = args["labels"]?.AsArray().Select(l => (string)l!).ToList() ?? new(),
                    Due = args["due"]?.DeepClone().AsObject(),
                    ChildOrder = Items.Count(i => i.ProjectId == projectId && i.ParentId == parentId) + 1,
                    Created = ++_seq,
                    Changed = _seq,
                };
                Items.Add(item);
                Map(item.Id);
                return "ok";
            }

            case "item_complete":
            {
                var item = Items.FirstOrDefault(i => i.Id == Resolve((string?)args["id"]));
                if (item is null)
                {
                    return Error("ITEM_NOT_FOUND", 22, "Item not found", 404);
                }

                item.Checked = true;
                item.CompletedAt = (string?)args["date_completed"] ?? "now";
                item.Changed = ++_seq;
                return "ok";
            }

            case "reminder_add":
            {
                if (!RemindersAllowed)
                {
                    return Error("PREMIUM_ONLY", 32, "Premium only feature", 403);
                }

                var itemId = Resolve((string?)args["item_id"]);
                if (Items.All(i => i.Id != itemId))
                {
                    return Error("INVALID_TEMPID", 15, "Invalid temporary id", 400);
                }

                var reminder = new Reminder(itemId!, args.DeepClone().AsObject()) { Id = NewId("r"), Created = ++_seq };
                Reminders.Add(reminder);
                Map(reminder.Id);
                return "ok";
            }

            default:
                return Error("INVALID_COMMAND", 1, "Unknown command", 400);
        }
    }

    /// <summary>Gives a change number to objects that tests added directly.</summary>
    private void Stamp()
    {
        foreach (var p in Projects.Where(p => p.Created < 0))
        {
            p.Created = p.Changed = ++_seq;
        }

        foreach (var i in Items.Where(i => i.Created < 0))
        {
            i.Created = i.Changed = ++_seq;
        }
    }

    private JsonObject Read(IReadOnlyDictionary<string, string> form)
    {
        Stamp();
        var types = JsonNode.Parse(form["resource_types"])!.AsArray().Select(t => (string)t!).ToHashSet();
        var token = form.GetValueOrDefault("sync_token", "*");
        long since = 0;
        var full = !(token.StartsWith("token-", StringComparison.Ordinal)
            && long.TryParse(token["token-".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out since)
            && since <= _seq);
        if (types.Contains("items") && RewriteBareUrls)
        {
            foreach (var item in Items)
            {
                var rewritten = RewriteUrls(item.Content);
                if (rewritten != item.Content)
                {
                    item.Content = rewritten;
                    item.Changed = ++_seq;
                }
            }
        }

        var at = full ? FullSyncFrozenAt ?? _seq : _seq;
        bool Show(long created, long changed) => full ? created <= at : changed > since;
        var root = new JsonObject { ["full_sync"] = full, ["sync_token"] = "token-" + at };
        if (full)
        {
            root["full_sync_date_utc"] = "2026-11-19T21:17:48Z";
        }

        if (types.Contains("user"))
        {
            root["user"] = new JsonObject
            {
                ["id"] = UserId,
                ["email"] = Email,
                ["is_premium"] = RemindersAllowed,
                ["tz_info"] = new JsonObject { ["timezone"] = "Europe/Belgrade" },
            };
        }

        if (types.Contains("user_plan_limits"))
        {
            root["user_plan_limits"] = new JsonObject
            {
                ["current"] = new JsonObject
                {
                    ["plan_name"] = PlanName,
                    ["max_projects"] = MaxProjects,
                    ["max_tasks"] = MaxTasks,
                    ["max_sections"] = 20,
                    ["max_labels"] = 500,
                    ["reminders"] = RemindersAllowed,
                },
                ["next"] = null,
            };
        }

        if (types.Contains("projects"))
        {
            root["projects"] = new JsonArray(Projects.Where(p => Show(p.Created, p.Changed)).Select(p =>
            {
                var o = new JsonObject
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["inbox_project"] = p.Inbox,
                    ["is_archived"] = false,
                    ["is_deleted"] = false,
                };
                if (p.WorkspaceId is not null || p.NullWorkspaceKey)
                {
                    o["workspace_id"] = p.WorkspaceId;
                }

                return (JsonNode)o;
            }).ToArray());
        }

        if (types.Contains("items"))
        {
            root["items"] = new JsonArray(Items
                .Where(i => Show(i.Created, i.Changed) && (!full || !i.Checked) && !HiddenFromReads.Contains(i.Content))
                .Select(i => (JsonNode)new JsonObject
                {
                    ["id"] = i.Id,
                    ["project_id"] = i.ProjectId,
                    ["parent_id"] = i.ParentId,
                    ["content"] = i.Content,
                    ["child_order"] = i.ChildOrder,
                    ["checked"] = i.Checked,
                    ["is_deleted"] = false,
                }).ToArray());
        }

        if (types.Contains("reminders"))
        {
            root["reminders"] = new JsonArray(Reminders.Where(r => Show(r.Created, r.Created)).Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id,
                ["item_id"] = r.ItemId,
                ["type"] = (string?)r.Args["type"],
                ["due"] = r.Args["due"]?.DeepClone(),
                ["is_deleted"] = false,
            }).ToArray());
        }

        return root;
    }

    private static readonly System.Text.RegularExpressions.Regex BareUrl =
        new(@"(?<![\(\[])\bhttps?://[^\s\)\]]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Bare addresses become "[Page title of host](address)"; addresses already in a link stay as they are.</summary>
    internal static string RewriteUrls(string content) =>
        BareUrl.Replace(content, m => $"[Page title of {new Uri(m.Value).Host} | Sell [things]](" + m.Value + ")");

    private string NewId(string prefix) => prefix + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject Error(string tag, int code, string message, int http) => new()
    {
        ["error"] = message,
        ["error_code"] = code,
        ["error_extra"] = new JsonObject(),
        ["error_tag"] = tag,
        ["http_code"] = http,
    };

    private static Dictionary<string, string> ParseForm(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
            form[Decode(eq < 0 ? pair : pair[..eq])] = eq < 0 ? string.Empty : Decode(pair[(eq + 1)..]);
        }

        return form;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>Several Todoist accounts behind one handler: each request goes to the account whose token it carries.</summary>
internal sealed class TodoistAccounts(params FakeTodoist[] accounts) : HttpMessageHandler
{
    /// <summary>Called before a request is passed on; may wait, for tests that change things while a request is in flight.</summary>
    public Func<HttpRequestMessage, Task>? Before { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Before is { } before)
        {
            await before(request);
        }

        var auth = request.Headers.Authorization?.Parameter;
        var account = accounts.FirstOrDefault(a => a.AcceptedToken == auth) ?? accounts[0];
        using var invoker = new HttpMessageInvoker(account, disposeHandler: false);
        return await invoker.SendAsync(request, cancellationToken);
    }
}

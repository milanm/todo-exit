using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TodoExit.Core.Export;

namespace TodoExit.Core.Todoist;

public sealed record TransferProgress(int CommandsDone, int CommandsTotal, string? CurrentList)
{
    /// <summary>Set while waiting before a retry.</summary>
    public TimeSpan? Waiting { get; init; }

    public string? WaitingReason { get; init; }
}

public enum TransferStatus
{
    Completed,

    /// <summary>Stopped by Ctrl+C or because Todoist kept failing; the state is saved and the run can be resumed.</summary>
    Interrupted,

    /// <summary>Stopped by an error that a retry would not fix (token rejected, request refused).</summary>
    Failed,

    /// <summary>The token belongs to another Todoist account than the plan or the state. Nothing was written.</summary>
    WrongAccount,
}

public sealed record TodoistTransferResult(TransferStatus Status, string? Message);

/// <summary>Totals of a transfer, from the plan and the state.</summary>
public sealed record TransferCounts(int Projects, int Tasks, int SubTasks, int Completed, int Reminders, int Errors, int Skipped)
{
    public static TransferCounts From(TodoistPlan plan, TodoistTransferState state)
    {
        bool Created(string key) => state.Ids.ContainsKey(key) && !state.Failed.ContainsKey(key);
        var items = plan.Projects.SelectMany(p => p.Items).ToList();
        return new TransferCounts(
            plan.Projects.Count(p => Created(p.Key)),
            items.Count(i => !i.IsSubTask && Created(i.Key)),
            items.Count(i => i.IsSubTask && Created(i.Key)),
            items.Count(i => state.Completed.Contains(i.Key)),
            state.Reminders.Count,
            state.Errors.Count(e => e.LossKind == LossKinds.TodoistError),
            state.Errors.Count(e => e.LossKind == LossKinds.ParentNotCreated));
    }
}

/// <summary>
/// Writes a plan into Todoist. Commands go out in plan order, at most 100 per request: for each project
/// project_add, then for each task item_add (with reminder_add right after it when allowed), its sub-tasks, then
/// item_complete for the completed sub-tasks and the task. References inside one request use temp_ids;
/// references to earlier requests use the real ids from the state.
/// </summary>
/// <remarks>
/// Before a request is sent, its uuids are saved as Pending. A response that never arrives leaves them there, and
/// the next attempt (or a resumed run) sends exactly those commands again with the same uuids and temp_ids. Todoist
/// answers "ok" without new objects for commands it already ran, and without their temp_id_mapping; the missing ids
/// are then found by reading the account's projects and active tasks.
/// </remarks>
public static class TodoistTransfer
{
    public const string ProjectAdd = "project_add";
    public const string ItemAdd = "item_add";
    public const string ItemComplete = "item_complete";
    public const string ReminderAdd = "reminder_add";

    /// <summary>A command for an item that never got a status is sent again at most this many times.</summary>
    private const int MaxMissingStatus = 3;

    internal sealed record PlannedCommand(string Type, string Key, string Uuid, PlannedProject Project, PlannedItem? Item)
    {
        /// <summary>Keys this command refers to: project and parent for item_add, the task for the others.</summary>
        public IEnumerable<string> Dependencies => Type switch
        {
            ItemAdd when Item!.ParentKey is { } parent => [Project.Key, parent],
            ItemAdd => [Project.Key],
            ItemComplete or ReminderAdd => [Item!.Key],
            _ => [],
        };

        public bool Creates => Type is ProjectAdd or ItemAdd;
    }

    /// <summary>Creates the state for a new transfer of <paramref name="plan"/>.</summary>
    public static TodoistTransferState NewState(TodoistPlan plan, string archiveFingerprint, DateTimeOffset now) => new()
    {
        TransferId = Guid.NewGuid().ToString("D"),
        ArchiveFingerprint = archiveFingerprint,
        StartedAt = now,
        Settings = plan.Settings,
        PreexistingProjectIds = (plan.Account?.Projects.Select(p => p.Id) ?? []).ToHashSet(StringComparer.Ordinal),
        TodoistUserId = plan.Account?.UserId,
        TodoistEmail = plan.Account?.Email,
    };

    /// <summary>How many of the plan's commands the state marks as done.</summary>
    public static (int Done, int Total) CommandProgress(TodoistPlan plan, TodoistTransferState state)
    {
        var commands = Commands(plan, state);
        return (commands.Count(c => state.Done.Contains(c.Uuid)), commands.Count);
    }

    /// <summary>All commands of the plan in the order they are sent.</summary>
    internal static List<PlannedCommand> Commands(TodoistPlan plan, TodoistTransferState state)
    {
        var commands = new List<PlannedCommand>();
        foreach (var project in plan.Projects)
        {
            commands.Add(new(ProjectAdd, project.Key, state.Uuid(ProjectAdd, project.Key), project, null));
            var completes = new List<PlannedItem>();
            PlannedItem? task = null;
            foreach (var item in project.Items)
            {
                if (!item.IsSubTask)
                {
                    Flush();
                    task = item;
                }

                commands.Add(new(ItemAdd, item.Key, state.Uuid(ItemAdd, item.Key), project, item));
                if (item.ReminderUtc is not null)
                {
                    commands.Add(new(ReminderAdd, item.Key, state.Uuid(ReminderAdd, item.Key), project, item));
                }

                if (item.IsSubTask && item.Complete)
                {
                    completes.Add(item);
                }
            }

            Flush();

            void Flush()
            {
                // Sub-tasks first, then the task, so a parent is completed after its children.
                if (task is { Complete: true })
                {
                    completes.Add(task);
                }

                foreach (var c in completes)
                {
                    commands.Add(new(ItemComplete, c.Key, state.Uuid(ItemComplete, c.Key), project, c));
                }

                completes.Clear();
                task = null;
            }
        }

        return commands;
    }

    /// <param name="save">Persists the state. Called before each request (with its uuids as Pending) and after it.</param>
    public static async Task<TodoistTransferResult> RunAsync(
        TodoistPlan plan,
        TodoistTransferState state,
        TodoistClient client,
        Func<TodoistTransferState, CancellationToken, Task> save,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(save);
        if (!plan.CanStart)
        {
            throw new InvalidOperationException("The plan has problems; the transfer cannot start.");
        }

        var commands = Commands(plan, state);
        var missing = new Dictionary<string, int>(StringComparer.Ordinal);
        string? currentList = null;
        void Report(TimeSpan? waiting = null, string? reason = null) => progress?.Report(
            new TransferProgress(commands.Count(c => state.Done.Contains(c.Uuid)), commands.Count, currentList)
            {
                Waiting = waiting,
                WaitingReason = reason,
            });

        var previousWaiting = client.Waiting;
        client.Waiting = (wait, reason) => Report(wait, reason);
        try
        {
            // Before anything else, and before every resume or Continue: is this the account the transfer belongs to?
            var bindingBefore = state.TodoistUserId;
            if (await CheckAccountAsync(plan, state, client, cancellationToken).ConfigureAwait(false) is { } wrong)
            {
                return new TodoistTransferResult(TransferStatus.WrongAccount, wrong);
            }

            if (bindingBefore != state.TodoistUserId)
            {
                await save(state, CancellationToken.None).ConfigureAwait(false);
            }

            if (state.Unresolved.Count > 0)
            {
                await RecoverAsync(plan, state, client, cancellationToken).ConfigureAwait(false);
                await save(state, CancellationToken.None).ConfigureAwait(false);
            }

            Report();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = NextBatch(commands, state);
                if (batch.Count == 0)
                {
                    break;
                }

                currentList = batch[^1].Project.ListName;
                var wire = Wire(batch, state);
                if (state.Pending.Count == 0)
                {
                    state.Pending.AddRange(batch.Select(c => c.Uuid));
                }

                state.Sent.UnionWith(batch.Select(c => c.Uuid));
                await save(state, CancellationToken.None).ConfigureAwait(false);

                var result = await client.ExecuteAsync(wire, cancellationToken).ConfigureAwait(false);
                Apply(batch, result, state, missing);
                state.Pending.Clear();
                if (state.Unresolved.Count > 0)
                {
                    await save(state, CancellationToken.None).ConfigureAwait(false);
                    await RecoverAsync(plan, state, client, cancellationToken).ConfigureAwait(false);
                }

                await save(state, CancellationToken.None).ConfigureAwait(false);
                Report();
            }

            state.Finished = true;
            await save(state, CancellationToken.None).ConfigureAwait(false);
            return new TodoistTransferResult(TransferStatus.Completed, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await save(state, CancellationToken.None).ConfigureAwait(false);
            return new TodoistTransferResult(TransferStatus.Interrupted, "Cancelled.");
        }
        catch (TodoistApiException ex)
        {
            await save(state, CancellationToken.None).ConfigureAwait(false);
            return new TodoistTransferResult(ex.Failure == TodoistFailure.Unavailable ? TransferStatus.Interrupted : TransferStatus.Failed, ex.Message);
        }
        finally
        {
            client.Waiting = previousWaiting;
        }
    }

    /// <summary>
    /// Reads the account behind the token (one read request) and compares it with the account the transfer belongs to.
    /// Returns a message when it is another account; nothing may be written then. Binds a state that has no account yet.
    /// </summary>
    /// <remarks>
    /// The expected account is the state's <see cref="TodoistTransferState.TodoistUserId"/>, or the plan's account
    /// when the state has none. A state saved by an earlier version has no user id: if it never sent anything it is
    /// bound to the token's account (nothing exists to collide with); otherwise it is bound only when the token's
    /// account holds a project this transfer created, or its Inbox is one of the projects recorded before the transfer
    /// started. If neither is found, the run is refused.
    /// </remarks>
    internal static async Task<string?> CheckAccountAsync(
        TodoistPlan plan, TodoistTransferState state, TodoistClient client, CancellationToken cancellationToken)
    {
        var legacy = state.TodoistUserId is null && state.Sent.Count > 0;
        var actual = await client.ReadIdentityAsync(withProjects: legacy, cancellationToken).ConfigureAwait(false);
        if (actual.UserId is null)
        {
            return "Todoist did not say which account this token belongs to, so nothing was written. Try again.";
        }

        if (state.TodoistUserId is { } bound)
        {
            return bound == actual.UserId ? null : WrongAccountMessage(state.TodoistEmail ?? plan.Account?.Email, actual.Email);
        }

        if (plan.Account?.UserId is { } planned && planned != actual.UserId)
        {
            return WrongAccountMessage(plan.Account.Email, actual.Email);
        }

        if (legacy)
        {
            var ours = plan.Projects
                .Select(p => state.Ids.TryGetValue(p.Key, out var id) ? id : null)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.Ordinal);
            var found = actual.Projects.Any(p => ours.Contains(p.Id) || (p.IsInbox && state.PreexistingProjectIds.Contains(p.Id)));
            if (!found)
            {
                return "This transfer state was saved by an earlier version of todo-exit and does not record its Todoist account, " +
                    $"and none of its projects were found in the account of this token ({actual.Email ?? "unknown email"}). Nothing was written. " +
                    "Use the token of the account the transfer started in, or start a new transfer.";
            }
        }

        state.TodoistUserId = actual.UserId;
        state.TodoistEmail = actual.Email;
        return null;
    }

    private static string WrongAccountMessage(string? expected, string? actual) =>
        $"This token belongs to a different Todoist account ({actual ?? "unknown email"}) than the one this transfer is for " +
        $"({expected ?? "unknown email"}). Nothing was written. Use the token of {expected ?? "that account"}, or make a new plan.";

    /// <summary>
    /// The next request: the pending commands when a request is in doubt, otherwise up to 100 commands in order.
    /// Commands whose project or parent failed are marked done and reported here.
    /// </summary>
    internal static List<PlannedCommand> NextBatch(List<PlannedCommand> commands, TodoistTransferState state)
    {
        if (state.Pending.Count > 0)
        {
            var pending = state.Pending.ToHashSet(StringComparer.Ordinal);
            var again = commands.Where(c => pending.Contains(c.Uuid) && !state.Done.Contains(c.Uuid)).ToList();
            if (again.Count > 0)
            {
                return again;
            }

            state.Pending.Clear();
        }

        var batch = new List<PlannedCommand>();
        var inBatch = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in commands)
        {
            if (batch.Count >= TodoistClient.MaxCommandsPerRequest)
            {
                break;
            }

            if (state.Done.Contains(c.Uuid))
            {
                continue;
            }

            var blocked = c.Dependencies.FirstOrDefault(d => !inBatch.Contains(d) && !HasId(state, d));
            if (blocked is not null)
            {
                Skip(c, blocked, state);
                continue;
            }

            if (c.Type == ReminderAdd && state.RemindersRefused)
            {
                state.Done.Add(c.Uuid);
                state.Errors.Add(Error(c, LossKinds.ReminderNotCarried, "PREMIUM_ONLY",
                    $"Reminder at {Fmt.DateTime(c.Item!.ReminderUtc!.Value.UtcDateTime)} UTC was not added: your Todoist plan does not include reminders."));
                continue;
            }

            batch.Add(c);
            if (c.Creates)
            {
                inBatch.Add(c.Key);
            }
        }

        return batch;
    }

    private static bool HasId(TodoistTransferState state, string key) =>
        state.Ids.TryGetValue(key, out var id) && id.Length > 0 && !state.Failed.ContainsKey(key);

    private static void Skip(PlannedCommand c, string blockedBy, TodoistTransferState state)
    {
        state.Done.Add(c.Uuid);
        if (c.Type != ItemAdd)
        {
            return; // the task itself is already reported
        }

        var reason = state.Failed.TryGetValue(blockedBy, out var r) ? r : "its id is not known";
        state.Failed[c.Key] = "parent not created";
        var what = blockedBy == c.Project.Key ? $"project \"{c.Project.Name}\"" : "parent task";
        state.Errors.Add(Error(c, LossKinds.ParentNotCreated, null, $"Not created because its {what} could not be created ({reason})."));
    }

    /// <summary>Turns planned commands into wire commands. Deterministic for a given plan and state.</summary>
    internal static List<TodoistCommand> Wire(List<PlannedCommand> batch, TodoistTransferState state)
    {
        var created = batch.Where(c => c.Creates).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        string Ref(string key) => HasId(state, key) ? state.Ids[key] : created.Contains(key)
            ? state.TempId(key)
            : throw new InvalidOperationException($"No id for {key}.");

        var wire = new List<TodoistCommand>();
        foreach (var c in batch)
        {
            switch (c.Type)
            {
                case ProjectAdd:
                    wire.Add(new TodoistCommand(ProjectAdd, c.Uuid, state.TempId(c.Key), new JsonObject { ["name"] = c.Project.Name }));
                    break;
                case ItemAdd:
                {
                    var item = c.Item!;
                    var args = new JsonObject { ["content"] = item.Content };
                    if (item.Description.Length > 0)
                    {
                        args["description"] = item.Description;
                    }

                    args["project_id"] = Ref(c.Project.Key);
                    if (item.ParentKey is { } parent)
                    {
                        args["parent_id"] = Ref(parent);
                    }

                    args["priority"] = item.Priority;
                    if (item.Labels.Count > 0)
                    {
                        args["labels"] = new JsonArray(item.Labels.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
                    }

                    if (item.DueString is { } phrase)
                    {
                        args["due"] = new JsonObject { ["string"] = phrase, ["lang"] = "en" };
                    }
                    else if (item.DueDate is { } date)
                    {
                        args["due"] = new JsonObject { ["date"] = date };
                    }

                    wire.Add(new TodoistCommand(ItemAdd, c.Uuid, state.TempId(c.Key), args));
                    break;
                }

                case ItemComplete:
                {
                    var args = new JsonObject { ["id"] = Ref(c.Key) };
                    if (c.Item!.CompletedAtUtc is { } at)
                    {
                        args["date_completed"] = Utc(at);
                    }

                    wire.Add(new TodoistCommand(ItemComplete, c.Uuid, null, args));
                    break;
                }

                case ReminderAdd:
                    wire.Add(new TodoistCommand(ReminderAdd, c.Uuid, state.TempId("reminder:" + c.Key), new JsonObject
                    {
                        ["item_id"] = Ref(c.Key),
                        ["type"] = "absolute",
                        ["due"] = new JsonObject { ["date"] = Utc(c.Item!.ReminderUtc!.Value) },
                    }));
                    break;
            }
        }

        return wire;
    }

    /// <summary>"2024-03-05T10:00:00.000000Z", the format Todoist accepted for date_completed.</summary>
    internal static string Utc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'.000000Z'", CultureInfo.InvariantCulture);

    private static void Apply(List<PlannedCommand> batch, TodoistSyncResult result, TodoistTransferState state, Dictionary<string, int> missing)
    {
        foreach (var c in batch)
        {
            if (!result.Status.TryGetValue(c.Uuid, out var status))
            {
                // No status: send it again (same uuid, so it cannot run twice), but not forever.
                missing[c.Uuid] = missing.GetValueOrDefault(c.Uuid) + 1;
                if (missing[c.Uuid] < MaxMissingStatus)
                {
                    continue;
                }

                status = new TodoistCommandResult(false, "NO_STATUS", null, "Todoist did not report a result for this command.");
            }

            state.Done.Add(c.Uuid);
            if (status.Ok)
            {
                switch (c.Type)
                {
                    case ProjectAdd or ItemAdd:
                        if (result.TempIdMapping.TryGetValue(state.TempId(c.Key), out var id) && id.Length > 0)
                        {
                            state.Ids[c.Key] = id;
                        }
                        else
                        {
                            state.Unresolved.Add(c.Key);
                        }

                        break;
                    case ItemComplete:
                        state.Completed.Add(c.Key);
                        break;
                    case ReminderAdd:
                        state.Reminders.Add(c.Key);
                        break;
                }

                continue;
            }

            var tag = status.ErrorTag ?? "UNKNOWN";
            var message = status.Error ?? "no message";
            switch (c.Type)
            {
                case ReminderAdd when tag == "PREMIUM_ONLY":
                    state.RemindersRefused = true;
                    state.Errors.Add(Error(c, LossKinds.ReminderNotCarried, tag,
                        $"Reminder at {Fmt.DateTime(c.Item!.ReminderUtc!.Value.UtcDateTime)} UTC was not added: your Todoist plan does not include reminders."));
                    break;
                case ProjectAdd or ItemAdd:
                {
                    var failedParent = c.Dependencies.FirstOrDefault(d => state.Failed.ContainsKey(d));
                    state.Failed[c.Key] = $"{tag}: {message}";
                    if (failedParent is not null)
                    {
                        var what = failedParent == c.Project.Key ? $"project \"{c.Project.Name}\"" : "parent task";
                        state.Errors.Add(Error(c, LossKinds.ParentNotCreated, tag,
                            $"Not created because its {what} could not be created ({state.Failed[failedParent]})."));
                    }
                    else
                    {
                        state.Errors.Add(Error(c, LossKinds.TodoistError, tag, c.Type == ProjectAdd
                            ? $"Todoist refused to create the project \"{c.Project.Name}\": {message} ({tag}). Its tasks were not created."
                            : $"Todoist refused to create {(c.Item!.IsSubTask ? $"the sub-task \"{c.Item.Content}\"" : "the task")}: {message} ({tag})."));
                    }

                    break;
                }

                case ItemComplete:
                    state.Errors.Add(Error(c, LossKinds.TodoistError, tag,
                        $"Todoist refused to complete {(c.Item!.IsSubTask ? $"the sub-task \"{c.Item.Content}\"" : "the task")}; it stays open: {message} ({tag})."));
                    break;
                default:
                    state.Errors.Add(Error(c, LossKinds.TodoistError, tag, $"Todoist refused to add the reminder: {message} ({tag})."));
                    break;
            }
        }
    }

    /// <summary>
    /// Finds the ids of objects Todoist acknowledged without a temp_id_mapping entry (a request sent again after
    /// a lost response). Reads all projects and active tasks; matches projects by name among those that existed
    /// neither before the transfer nor in the state, and tasks by project, parent and content in creation order.
    /// Completed tasks are not listed by Todoist; their ids are not needed any more.
    /// </summary>
    internal static async Task RecoverAsync(TodoistPlan plan, TodoistTransferState state, TodoistClient client, CancellationToken cancellationToken)
    {
        var (projects, items) = await client.ReadProjectsAndItemsAsync(cancellationToken).ConfigureAwait(false);
        var claimed = state.Ids.Values.Where(v => v.Length > 0).ToHashSet(StringComparer.Ordinal);

        foreach (var project in plan.Projects.Where(p => state.Unresolved.Contains(p.Key)))
        {
            var candidates = projects
                .Where(p => p.Name == project.Name && !p.IsInbox && !state.PreexistingProjectIds.Contains(p.Id) && !claimed.Contains(p.Id))
                .ToList();
            if (candidates.Count == 1)
            {
                state.Ids[project.Key] = candidates[0].Id;
                claimed.Add(candidates[0].Id);
            }
            else
            {
                var reason = candidates.Count == 0 ? "created, but not found afterwards" : "several new projects have this name";
                state.Failed[project.Key] = reason;
                state.Errors.Add(new TransferError(LossKinds.TodoistError, ProjectAdd, project.Key, project.ListName, null, null,
                    $"Cannot tell which Todoist project is \"{project.Name}\" ({reason}). Its remaining tasks were not created; check the account."));
            }

            state.Unresolved.Remove(project.Key);
        }

        foreach (var (project, item) in plan.Projects.SelectMany(p => p.Items.Select(i => (p, i))).Where(x => state.Unresolved.Contains(x.i.Key)))
        {
            state.Unresolved.Remove(item.Key);
            if (state.Completed.Contains(item.Key))
            {
                state.Ids[item.Key] = string.Empty; // completed: not listed, and nothing refers to it any more
                continue;
            }

            var projectId = HasId(state, project.Key) ? state.Ids[project.Key] : null;
            var parentId = item.ParentKey is null ? null : HasId(state, item.ParentKey) ? state.Ids[item.ParentKey] : null;

            // Only tasks in a project this transfer created, under the expected parent, not yet claimed, with the same
            // content once Markdown links are reduced to their address. Among equal candidates the earliest one wins:
            // the plan is walked in creation order, so the n-th such sibling in the plan gets the n-th one in Todoist.
            var key = MatchKey(item.Content);
            var match = projectId is null || (item.ParentKey is not null && parentId is null)
                ? null
                : items
                    .Where(i => i.ProjectId == projectId && i.ParentId == parentId && !claimed.Contains(i.Id) && MatchKey(i.Content) == key)
                    .OrderBy(i => i.ChildOrder)
                    .FirstOrDefault();
            if (match is not null)
            {
                state.Ids[item.Key] = match.Id;
                claimed.Add(match.Id);
            }
            else
            {
                state.Failed[item.Key] = "created, but not found afterwards";
                state.Errors.Add(new TransferError(LossKinds.TodoistError, ItemAdd, item.Key, item.ListName, item.TaskTitle, null,
                    "Todoist confirmed this task, but it could not be found afterwards; its sub-tasks, reminder and completion were skipped. Check the project."));
            }
        }

        state.Unresolved.Clear();
    }

    /// <summary>
    /// A Markdown link "[text](address)" or "[text](address "title")": the text may hold one level of brackets and
    /// escaped characters, the address one level of parentheses.
    /// </summary>
    private static readonly Regex MarkdownLink = new(
        @"\[(?:\\.|[^\[\]\\]|\[[^\[\]]*\])*\]\((?<url>(?:[^\s()]|\([^\s()]*\))+)(?:\s+""[^""]*"")?\)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The form task content is compared in when ids are recovered. Todoist rewrites a bare address in the content,
    /// some time after the task is created, into a Markdown link titled with the page title, so
    /// "https://example.com/" may come back as "[Example Domain](https://example.com/)". Each link counts as its address.
    /// </summary>
    internal static string MatchKey(string content) =>
        MarkdownLink.Replace(content, m => m.Groups["url"].Value).Trim();

    private static TransferError Error(PlannedCommand c, string kind, string? tag, string message) =>
        new(kind, c.Type, c.Key, c.Project.ListName, c.Item?.TaskTitle, tag, message);
}

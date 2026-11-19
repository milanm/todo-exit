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
/// Before the first write, a sync_token is taken. Before a request is sent, its uuids are saved as Pending. A response
/// that never arrives leaves them there, and the next attempt (or a resumed run) sends exactly those commands again
/// with the same uuids and temp_ids. Todoist answers "ok" without new objects for commands it already ran, and without
/// their temp_id_mapping; the missing ids are then found in what changed since the sync_token. An id that is not found
/// stays unknown: the run ends interrupted, and what depends on it waits for a resumed run.
/// A command that Todoist answers with a temporary error (http_code 408, 429 or 5xx) is sent again after a wait, with a
/// new uuid, a few times per run; before that, the account is read in case Todoist ran it anyway. If it still fails, the
/// run ends interrupted with the command, and everything that depends on it, left for a resumed run.
/// </remarks>
public static class TodoistTransfer
{
    public const string ProjectAdd = "project_add";
    public const string ItemAdd = "item_add";
    public const string ItemComplete = "item_complete";
    public const string ReminderAdd = "reminder_add";

    /// <summary>A command for an item that never got a status is sent again at most this many times.</summary>
    private const int MaxMissingStatus = 3;

    /// <summary>A command that failed with a temporary error is sent again at most this many times per run.</summary>
    internal const int MaxTemporaryRetries = 4;

    /// <summary>When created objects are not found, the account is read again at most this many times, with waits.</summary>
    internal const int MaxRecoveryRereads = 5;

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

            if (state.SyncToken is null && state.Sent.Count == 0 && commands.Any(c => !state.Done.Contains(c.Uuid)))
            {
                // Before the first write: id recovery later reads what changed since this point.
                var start = await client.ReadSyncTokenAsync(cancellationToken).ConfigureAwait(false);
                state.SyncToken = start.SyncToken;
                state.SyncTokenDate = start.FullSyncDateUtc;
                await save(state, CancellationToken.None).ConfigureAwait(false);
            }

            // In this run only: temporary failures per command, commands that used up their retries, and keys that
            // were already searched for with patience (each new unknown id gets one patient search per run).
            var tries = new Dictionary<string, int>(StringComparer.Ordinal);
            var held = new HashSet<string>(StringComparer.Ordinal);
            var searched = new HashSet<string>(StringComparer.Ordinal);
            if (state.Unresolved.Count > 0)
            {
                await ResolveAsync(plan, state, client, [], MaxRecoveryRereads, cancellationToken).ConfigureAwait(false);
                searched.UnionWith(state.Unresolved);
                await save(state, CancellationToken.None).ConfigureAwait(false);
            }

            string? lastTemporary = null;
            Report();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = NextBatch(commands, state, held);
                if (batch.Count == 0)
                {
                    if (state.Unresolved.Count > 0)
                    {
                        // Time has passed since the last search: look once more, and go on if anything was found.
                        var before = state.Unresolved.Count;
                        await ResolveAsync(plan, state, client, [], 0, cancellationToken).ConfigureAwait(false);
                        await save(state, CancellationToken.None).ConfigureAwait(false);
                        if (state.Unresolved.Count < before)
                        {
                            continue;
                        }
                    }

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
                var outcome = Apply(batch, result, state, missing);
                state.Pending.Clear();
                await save(state, CancellationToken.None).ConfigureAwait(false);

                var retry = new List<(PlannedCommand Command, TodoistCommandResult Status)>();
                foreach (var (c, status) in outcome.Temporary)
                {
                    lastTemporary = Describe(status);
                    tries[c.Uuid] = tries.GetValueOrDefault(c.Uuid) + 1;
                    if (tries[c.Uuid] > MaxTemporaryRetries)
                    {
                        held.Add(c.Uuid);
                    }
                    else
                    {
                        retry.Add((c, status));
                    }
                }

                if (retry.Count > 0)
                {
                    var wait = retry.Select(r => r.Status.RetryAfter ?? TimeSpan.Zero)
                        .Append(TodoistClient.Backoff(retry.Max(r => tries[r.Command.Uuid])))
                        .Max();
                    await client.WaitAsync(wait,
                        $"{TodoistPlanText.Count(retry.Count, "command", "commands")} failed for now: {Describe(retry[0].Status)}",
                        cancellationToken).ConfigureAwait(false);
                }

                // Commands sent again with a new uuid: Todoist may have run them despite the error, so look first.
                var again = outcome.Temporary.Select(t => t.Command).Concat(outcome.Blocked).ToHashSet();
                var check = batch.Where(again.Contains).ToList();
                var unsearched = state.Unresolved.Any(k => !searched.Contains(k));
                if (unsearched || check.Count > 0)
                {
                    await ResolveAsync(plan, state, client, check, unsearched ? MaxRecoveryRereads : 0, cancellationToken).ConfigureAwait(false);
                    searched.UnionWith(state.Unresolved);
                    await save(state, CancellationToken.None).ConfigureAwait(false);
                }

                Report();
            }

            if (commands.Any(c => !state.Done.Contains(c.Uuid)) || state.Unresolved.Count > 0)
            {
                await save(state, CancellationToken.None).ConfigureAwait(false);
                return new TodoistTransferResult(TransferStatus.Interrupted, NotFinishedMessage(commands, state, held, lastTemporary));
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
    /// Commands whose project or parent failed are marked done and reported here. Commands whose project or parent
    /// has no id yet (it failed for now, or Todoist confirmed it but its id is not found yet) wait, and so do the
    /// commands in <paramref name="held"/>.
    /// </summary>
    internal static List<PlannedCommand> NextBatch(List<PlannedCommand> commands, TodoistTransferState state, IReadOnlySet<string>? held = null)
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

            if (state.Done.Contains(c.Uuid) || held?.Contains(c.Uuid) == true)
            {
                continue;
            }

            var missingDependency = c.Dependencies.FirstOrDefault(d => !inBatch.Contains(d) && !HasId(state, d));
            if (missingDependency is not null)
            {
                if (state.Failed.ContainsKey(missingDependency) || state.Ids.ContainsKey(missingDependency))
                {
                    // Failed for good, or known to have no usable id (completed before its id was found).
                    Skip(c, missingDependency, state);
                }

                continue; // otherwise wait until the dependency has an id
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
        var created = batch.Where(c => c.Creates).ToDictionary(c => c.Key, StringComparer.Ordinal);
        string Ref(string key) => HasId(state, key) ? state.Ids[key] : created.TryGetValue(key, out var creator)
            ? TempIdOf(creator, state)
            : throw new InvalidOperationException($"No id for {key}.");

        var wire = new List<TodoistCommand>();
        foreach (var c in batch)
        {
            switch (c.Type)
            {
                case ProjectAdd:
                    wire.Add(new TodoistCommand(ProjectAdd, WireUuid(c, state), TempIdOf(c, state), new JsonObject { ["name"] = c.Project.Name }));
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

                    wire.Add(new TodoistCommand(ItemAdd, WireUuid(c, state), TempIdOf(c, state), args));
                    break;
                }

                case ItemComplete:
                {
                    var args = new JsonObject { ["id"] = Ref(c.Key) };
                    if (c.Item!.CompletedAtUtc is { } at)
                    {
                        args["date_completed"] = Utc(at);
                    }

                    wire.Add(new TodoistCommand(ItemComplete, WireUuid(c, state), null, args));
                    break;
                }

                case ReminderAdd:
                    wire.Add(new TodoistCommand(ReminderAdd, WireUuid(c, state), TempIdOf(c, state), new JsonObject
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

    /// <summary>The uuid a command is sent with now. The first attempt uses its base uuid.</summary>
    internal static string WireUuid(PlannedCommand c, TodoistTransferState state) => state.WireUuid(c.Type, c.Key);

    /// <summary>The temp_id a creating command (or a reminder) is sent with now.</summary>
    internal static string TempIdOf(PlannedCommand c, TodoistTransferState state) =>
        state.WireTempId(c.Uuid, c.Type == ReminderAdd ? "reminder:" + c.Key : c.Key);

    /// <summary>"2024-03-05T10:00:00.000000Z", the format Todoist accepted for date_completed.</summary>
    internal static string Utc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'.000000Z'", CultureInfo.InvariantCulture);

    /// <summary>What one answer left to do: commands that failed for now, and commands whose parent was not created.</summary>
    internal sealed record ApplyOutcome(
        List<(PlannedCommand Command, TodoistCommandResult Status)> Temporary,
        List<PlannedCommand> Blocked);

    /// <remarks>
    /// Three outcomes per command: "ok"; refused for good (reported, children skipped); not done yet. Not done yet is
    /// a temporary error (http_code 408, 429 or 5xx), or any error of a command whose project or parent has no id
    /// after this answer: that parent failed for now in the same request, so the child could not run. Such commands
    /// are not marked done and get a new uuid for their next attempt.
    /// </remarks>
    private static ApplyOutcome Apply(List<PlannedCommand> batch, TodoistSyncResult result, TodoistTransferState state, Dictionary<string, int> missing)
    {
        var outcome = new ApplyOutcome([], []);
        foreach (var c in batch)
        {
            if (!result.Status.TryGetValue(WireUuid(c, state), out var status))
            {
                // No status: send it again (same uuid, so it cannot run twice), but not forever.
                missing[c.Uuid] = missing.GetValueOrDefault(c.Uuid) + 1;
                if (missing[c.Uuid] < MaxMissingStatus)
                {
                    continue;
                }

                status = new TodoistCommandResult(false, "NO_STATUS", null, "Todoist did not report a result for this command.");
            }

            if (status.Ok)
            {
                state.Done.Add(c.Uuid);
                switch (c.Type)
                {
                    case ProjectAdd or ItemAdd:
                        if (result.TempIdMapping.TryGetValue(TempIdOf(c, state), out var id) && id.Length > 0)
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

            if (c.Dependencies.Any(d => !HasId(state, d) && !state.Failed.ContainsKey(d) && !state.Ids.ContainsKey(d)))
            {
                outcome.Blocked.Add(c);
                state.Attempts[c.Uuid] = state.Attempt(c.Uuid) + 1;
                continue;
            }

            if (status.IsTemporary)
            {
                outcome.Temporary.Add((c, status));
                state.Attempts[c.Uuid] = state.Attempt(c.Uuid) + 1;
                continue;
            }

            state.Done.Add(c.Uuid);
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

        return outcome;
    }

    /// <summary>
    /// Finds ids Todoist did not deliver, and commands it ran although it answered them with an error.
    /// </summary>
    /// <remarks>
    /// Reads what changed since the transfer's sync_token, so the transfer's own objects are seen even when a full sync
    /// of the account lags behind. A state without a token (saved by an earlier version) reads everything and then
    /// what changed since that read. A created object that is not found stays unresolved: the account is read again up
    /// to <paramref name="rereads"/> times with waits, and if it is still missing it stays unresolved for a resumed run. It is never marked
    /// failed, and nothing that depends on it is sent with a guessed id.
    /// <para>
    /// Projects match by name among personal projects that existed neither before the transfer nor in the state; two
    /// such candidates make the project fail, as a new read would not tell them apart. Tasks match by project, parent
    /// and content (Markdown links reduced to their address), earliest sibling first. Ids found for objects Todoist
    /// confirmed are matched first, then the commands in <paramref name="check"/>, in plan order.
    /// </para>
    /// </remarks>
    /// <param name="check">Commands that got an error and would be sent again with a new uuid. One found in the account is done.</param>
    /// <param name="rereads">How many more reads, with waits, while created objects are still not found.</param>
    internal static async Task ResolveAsync(
        TodoistPlan plan, TodoistTransferState state, TodoistClient client, IReadOnlyList<PlannedCommand> check, int rereads,
        CancellationToken cancellationToken)
    {
        var reminders = check.Any(c => c.Type == ReminderAdd);
        var view = await ReadViewAsync(state, client, null, reminders, cancellationToken).ConfigureAwait(false);
        for (var reread = 1; ; reread++)
        {
            MatchUnresolved(plan, state, view);
            if (state.Unresolved.Count == 0 || reread > rereads)
            {
                break;
            }

            await client.WaitAsync(TodoistClient.Backoff(reread),
                $"looking for {TodoistPlanText.Count(state.Unresolved.Count, "created item", "created items")} in Todoist", cancellationToken).ConfigureAwait(false);
            view = await ReadViewAsync(state, client, view, reminders, cancellationToken).ConfigureAwait(false);
        }

        MatchApplied(state, check, view);
    }

    /// <summary>The account's projects, tasks and reminders as far as recovery needs them, merged over several reads.</summary>
    internal sealed class AccountView
    {
        public Dictionary<string, TodoistProject> Projects { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TodoistItem> Items { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TodoistReminder> Reminders { get; } = new(StringComparer.Ordinal);

        public string? SyncToken { get; private set; }

        public void Merge(TodoistChanges changes)
        {
            foreach (var id in changes.DeletedIds)
            {
                Projects.Remove(id);
                Items.Remove(id);
                Reminders.Remove(id);
            }

            foreach (var p in changes.Projects)
            {
                Projects[p.Id] = p;
            }

            foreach (var i in changes.Items)
            {
                Items[i.Id] = i;
            }

            foreach (var r in changes.Reminders)
            {
                Reminders[r.Id] = r;
            }

            SyncToken = changes.SyncToken ?? SyncToken;
        }
    }

    private static async Task<AccountView> ReadViewAsync(
        TodoistTransferState state, TodoistClient client, AccountView? previous, bool reminders, CancellationToken cancellationToken)
    {
        var view = previous ?? new AccountView();
        var token = previous is null ? state.SyncToken ?? "*" : previous.SyncToken ?? "*";
        var changes = await client.ReadChangesAsync(token, reminders, cancellationToken).ConfigureAwait(false);
        view.Merge(changes);
        if (changes.FullSync && changes.SyncToken is { } after)
        {
            // A full sync can lag behind on large accounts; what changed after it comes from an incremental sync.
            view.Merge(await client.ReadChangesAsync(after, reminders, cancellationToken).ConfigureAwait(false));
        }

        return view;
    }

    private static bool IsNewProject(TodoistProject p, PlannedProject planned, TodoistTransferState state, HashSet<string> claimed) =>
        p.Name == planned.Name && !p.IsInbox && p.IsPersonal && !state.PreexistingProjectIds.Contains(p.Id) && !claimed.Contains(p.Id);

    /// <summary>The task in Todoist that <paramref name="item"/> became, or null when its project or parent has no id yet.</summary>
    private static TodoistItem? FindItem(PlannedProject project, PlannedItem item, TodoistTransferState state, AccountView view, HashSet<string> claimed)
    {
        var projectId = HasId(state, project.Key) ? state.Ids[project.Key] : null;
        var parentId = item.ParentKey is null ? null : HasId(state, item.ParentKey) ? state.Ids[item.ParentKey] : null;
        if (projectId is null || (item.ParentKey is not null && parentId is null))
        {
            return null;
        }

        // Only tasks in a project this transfer created, under the expected parent, not yet claimed, with the same
        // content once Markdown links are reduced to their address. Among equal candidates the earliest one wins:
        // the plan is walked in creation order, so the n-th such sibling in the plan gets the n-th one in Todoist.
        var key = MatchKey(item.Content);
        return view.Items.Values
            .Where(i => i.ProjectId == projectId && i.ParentId == parentId && !claimed.Contains(i.Id) && MatchKey(i.Content) == key)
            .OrderBy(i => i.ChildOrder)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static void MatchUnresolved(TodoistPlan plan, TodoistTransferState state, AccountView view)
    {
        var claimed = state.Ids.Values.Where(v => v.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var project in plan.Projects.Where(p => state.Unresolved.Contains(p.Key)))
        {
            var candidates = view.Projects.Values.Where(p => IsNewProject(p, project, state, claimed)).ToList();
            if (candidates.Count == 1)
            {
                state.Ids[project.Key] = candidates[0].Id;
                claimed.Add(candidates[0].Id);
                state.Unresolved.Remove(project.Key);
            }
            else if (candidates.Count > 1)
            {
                Ambiguous(project, state);
                state.Unresolved.Remove(project.Key);
            }
        }

        foreach (var (project, item) in plan.Projects.SelectMany(p => p.Items.Select(i => (p, i))).Where(x => state.Unresolved.Contains(x.i.Key)))
        {
            if (state.Completed.Contains(item.Key))
            {
                state.Ids[item.Key] = string.Empty; // completed: not listed by a full sync, and nothing refers to it any more
                state.Unresolved.Remove(item.Key);
                continue;
            }

            if (FindItem(project, item, state, view, claimed) is { } match)
            {
                state.Ids[item.Key] = match.Id;
                claimed.Add(match.Id);
                state.Unresolved.Remove(item.Key);
            }
        }
    }

    private static void MatchApplied(TodoistTransferState state, IReadOnlyList<PlannedCommand> check, AccountView view)
    {
        var claimed = state.Ids.Values.Where(v => v.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var c in check.Where(c => !state.Done.Contains(c.Uuid)))
        {
            switch (c.Type)
            {
                case ProjectAdd:
                {
                    var candidates = view.Projects.Values.Where(p => IsNewProject(p, c.Project, state, claimed)).ToList();
                    if (candidates.Count == 1)
                    {
                        state.Ids[c.Key] = candidates[0].Id;
                        claimed.Add(candidates[0].Id);
                        state.Done.Add(c.Uuid);
                    }
                    else if (candidates.Count > 1)
                    {
                        Ambiguous(c.Project, state);
                        state.Done.Add(c.Uuid);
                    }

                    break;
                }

                case ItemAdd when FindItem(c.Project, c.Item!, state, view, claimed) is { } match:
                    state.Ids[c.Key] = match.Id;
                    claimed.Add(match.Id);
                    state.Done.Add(c.Uuid);
                    break;
                case ItemComplete when HasId(state, c.Key) && view.Items.TryGetValue(state.Ids[c.Key], out var item) && item.Checked:
                    state.Completed.Add(c.Key);
                    state.Done.Add(c.Uuid);
                    break;
                case ReminderAdd when HasId(state, c.Key) && view.Reminders.Values.Any(r => r.ItemId == state.Ids[c.Key] && SameTime(r.DueDate, c.Item!.ReminderUtc!.Value)):
                    state.Reminders.Add(c.Key);
                    state.Done.Add(c.Uuid);
                    break;
            }
        }
    }

    private static bool SameTime(string? text, DateTimeOffset expected) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at.UtcDateTime == expected.UtcDateTime;

    private static void Ambiguous(PlannedProject project, TodoistTransferState state)
    {
        const string Reason = "several new projects have this name";
        state.Failed[project.Key] = Reason;
        state.Errors.Add(new TransferError(LossKinds.TodoistError, ProjectAdd, project.Key, project.ListName, null, null,
            $"Cannot tell which Todoist project is \"{project.Name}\" ({Reason}). Its remaining tasks were not created; check the account."));
    }

    private static string Describe(TodoistCommandResult status) =>
        $"HTTP {status.HttpCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}{(status.ErrorTag is { } tag ? " " + tag : string.Empty)}{(status.Error is { } e ? ": " + e : string.Empty)}";

    private static string NotFinishedMessage(List<PlannedCommand> commands, TodoistTransferState state, IReadOnlySet<string> held, string? lastTemporary)
    {
        var parts = new List<string>();
        if (held.Count > 0)
        {
            parts.Add($"Todoist failed {TodoistPlanText.Count(held.Count, "command", "commands")} {MaxTemporaryRetries + 1} times in a row ({lastTemporary}).");
        }

        if (state.Unresolved.Count > 0)
        {
            var n = state.Unresolved.Count;
            parts.Add($"Todoist confirmed {TodoistPlanText.Count(n, "item", "items")}, but {(n == 1 ? "it was" : "they were")} not found in the account yet.");
        }

        var waiting = commands.Count(c => !state.Done.Contains(c.Uuid));
        parts.Add(waiting == 0
            ? "Continue the transfer later to look again."
            : $"{TodoistPlanText.Count(waiting, "command is", "commands are")} not done yet, with everything that depends on {(held.Count + state.Unresolved.Count == 1 ? "it" : "them")}. Continue the transfer later to try again.");
        return string.Join(" ", parts);
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

using System.Globalization;
using System.Text.Json.Nodes;
using TodoExit.Core.Export;
using TodoExit.Core.Model;

namespace TodoExit.Core.Todoist;

/// <summary>
/// The settings a plan is built from. Stored in the transfer state so a resumed run builds the same plan
/// (same keys, same projects, same commands) whatever the command line says.
/// </summary>
public sealed record TodoistPlanSettings
{
    public const int DefaultMaxTasksPerProject = 300;

    public bool IncludeCompleted { get; init; }

    /// <summary>List names chosen with --lists (exact match), or null for all lists.</summary>
    public IReadOnlyList<string>? Lists { get; init; }

    /// <summary>Create projects even when the account already has a project with the same name.</summary>
    public bool AllowDuplicateNames { get; init; }

    /// <summary>Zone the archive dates are read in; also used to turn reminder and completion times into UTC.</summary>
    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>Most active tasks (tasks plus sub-tasks) per project: the account's max_tasks, or 300 offline.</summary>
    public int MaxTasksPerProject { get; init; } = DefaultMaxTasksPerProject;

    /// <summary>Whether the plan allows reminders. Null when unknown (offline plan).</summary>
    public bool? Reminders { get; init; }

    /// <summary>Reminders at or before this time are not added.</summary>
    public DateTimeOffset PlannedAt { get; init; }

    public JsonObject ToJson() => new()
    {
        ["includeCompleted"] = IncludeCompleted,
        ["lists"] = Lists is null ? null : new JsonArray(Lists.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
        ["allowDuplicateNames"] = AllowDuplicateNames,
        ["timeZone"] = TimeZoneId,
        ["maxTasksPerProject"] = MaxTasksPerProject,
        ["reminders"] = Reminders,
        ["plannedAt"] = PlannedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    };

    public static TodoistPlanSettings FromJson(JsonObject? o)
    {
        if (o is null)
        {
            return new TodoistPlanSettings();
        }

        static bool? B(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        return new TodoistPlanSettings
        {
            IncludeCompleted = B(o["includeCompleted"]) ?? false,
            Lists = o["lists"] is JsonArray a
                ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
                : null,
            AllowDuplicateNames = B(o["allowDuplicateNames"]) ?? false,
            TimeZoneId = TodoistTransferState.Str(o, "timeZone") ?? "UTC",
            MaxTasksPerProject = o["maxTasksPerProject"] is JsonValue m && m.TryGetValue<int>(out var max) && max > 0 ? max : DefaultMaxTasksPerProject,
            Reminders = B(o["reminders"]),
            PlannedAt = DateTimeOffset.TryParse(TodoistTransferState.Str(o, "plannedAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue,
        };
    }
}

/// <summary>One Todoist task to create: a To Do task or one of its steps.</summary>
public sealed record PlannedItem
{
    /// <summary>Stable key from archive ids: "t:&lt;task id&gt;" or "s:&lt;task id&gt;/&lt;step index&gt;".</summary>
    public required string Key { get; init; }

    public required string ProjectKey { get; init; }

    /// <summary>Key of the parent task for a sub-task.</summary>
    public string? ParentKey { get; init; }

    public required string Content { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>API priority: 4 is the highest (p1 in the apps), 1 is normal.</summary>
    public int Priority { get; init; } = 1;

    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Recurring due as an English phrase, e.g. "every mon starting 2026-11-24".</summary>
    public string? DueString { get; init; }

    /// <summary>Plain due date, yyyy-MM-dd.</summary>
    public string? DueDate { get; init; }

    /// <summary>Add, then complete.</summary>
    public bool Complete { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public DateTimeOffset? ReminderUtc { get; init; }

    public required string ListName { get; init; }

    /// <summary>The To Do task title (the parent's title for a sub-task), for the report.</summary>
    public required string TaskTitle { get; init; }

    public bool IsSubTask => ParentKey is not null;
}

/// <summary>One Todoist project to create. Items are in order: each task followed by its sub-tasks.</summary>
public sealed record PlannedProject(string Key, string Name, string ListName, int Part, IReadOnlyList<PlannedItem> Items)
{
    public int OpenTasks => Items.Count(i => !i.IsSubTask && !i.Complete);

    public int OpenSubTasks => Items.Count(i => i.IsSubTask && !i.Complete);

    public int CompletedTasks => Items.Count(i => !i.IsSubTask && i.Complete);

    public int CompletedSubTasks => Items.Count(i => i.IsSubTask && i.Complete);

    public int Reminders => Items.Count(i => i.ReminderUtc is not null);
}

/// <summary>What happens to one To Do list.</summary>
/// <param name="Skipped">Why nothing is created for it, or null.</param>
public sealed record ListPlan(string ListName, IReadOnlyList<PlannedProject> Projects, string? Skipped);

public sealed class TodoistPlan
{
    public required TodoistPlanSettings Settings { get; init; }

    /// <summary>Null for an offline plan.</summary>
    public TodoistAccount? Account { get; init; }

    public required IReadOnlyList<ListPlan> Lists { get; init; }

    public IReadOnlyList<PlannedProject> Projects => Lists.SelectMany(l => l.Projects).ToList();

    /// <summary>What cannot be carried, before aggregation.</summary>
    public required IReadOnlyList<Loss> Losses { get; init; }

    /// <summary>Reasons the transfer cannot start. Empty when it can.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Projects this plan still has to create.</summary>
    public int ProjectsNeeded { get; init; }

    /// <summary>Projects the account can still take, or null when unknown.</summary>
    public int? ProjectsAvailable { get; init; }

    /// <summary>The plan needs more new projects than the account can take.</summary>
    public bool TooManyProjects => ProjectsAvailable is { } free && ProjectsNeeded > free;

    /// <summary>Names of new projects that already exist in the account, when duplicates are not allowed.</summary>
    public IReadOnlyList<string> NameClashes { get; init; } = [];

    public bool CanStart => Problems.Count == 0;

    public override string ToString() => $"TodoistPlan({Projects.Count} projects)";
}

/// <summary>Builds the transfer plan from an archive. Pure: no network, no file system.</summary>
public static class TodoistPlanner
{
    private const string Format = ExportFormatNames.TodoistApi;

    /// <summary>Names in <paramref name="lists"/> that match no list exactly.</summary>
    public static IReadOnlyList<string> UnknownLists(TodoArchive archive, IEnumerable<string> lists)
    {
        var names = archive.Lists.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        return lists.Where(l => !names.Contains(l)).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <param name="account">Null for an offline plan: no limits or name checks.</param>
    /// <param name="state">The transfer state when resuming, so finished work is not counted again.</param>
    public static TodoistPlan Build(TodoArchive archive, TodoistPlanSettings settings, TodoistAccount? account, TodoistTransferState? state = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(settings);
        var zone = TimeZones.ResolveOrUtc(settings.TimeZoneId);
        var limit = Math.Max(1, settings.MaxTasksPerProject);
        var losses = new List<Loss>();
        var selected = settings.Lists?.ToHashSet(StringComparer.Ordinal);
        var listKeys = UniqueKeys(archive.Lists.Select(l => l.Id), "list");
        var usedTaskKeys = new HashSet<string>(StringComparer.Ordinal);

        // First the parts of every list, then the names: project names are unique over the final names, after
        // splitting, so a list named "Work - part 1" cannot get the same name as a part of a split list "Work".
        var listParts = new List<List<List<PlannedItem>>?>(archive.Lists.Count);
        for (var i = 0; i < archive.Lists.Count; i++)
        {
            var list = archive.Lists[i];
            if (selected is not null && !selected.Contains(list.Name))
            {
                if (list.Tasks.Count > 0)
                {
                    losses.Add(new Loss(Format, list.Name, null, LossKinds.ListNotSelected,
                        "This list was not chosen for the transfer.", list.Tasks.Count));
                }

                listParts.Add(null);
                continue;
            }

            if (list.IsShared)
            {
                losses.Add(new Loss(Format, list.Name, null, LossKinds.SharedListMembersNotCarried,
                    "This list is shared in To Do. Members and sharing are not carried; share the Todoist project again."));
            }

            var blocks = new List<List<PlannedItem>>();
            var listKey = listKeys[i];
            for (var position = 0; position < list.Tasks.Count; position++)
            {
                var task = list.Tasks[position];
                if (task.IsCompleted && !settings.IncludeCompleted)
                {
                    losses.Add(TodoistMapping.Loss(Format, list, task, LossKinds.CompletedSkipped,
                        "Completed task left out. Completed tasks are only transferred when you ask for them."));
                    continue;
                }

                var taskKey = UniqueKey(usedTaskKeys, string.IsNullOrEmpty(task.Id) ? $"{listKey}#{position}" : task.Id);
                blocks.Add(Block(list, task, taskKey, settings, zone, losses));
            }

            var parts = Pack(blocks, limit, list, losses);
            listParts.Add(parts);
            if (parts.Count > 1)
            {
                losses.Add(new Loss(Format, list.Name, null, LossKinds.ListSplit,
                    $"This list has more than {limit} active tasks for one Todoist project and was split into {parts.Count} projects.",
                    parts.Count));
            }
        }

        // Same rule as the CSV file names: duplicates (ignoring case) get " (2)", " (3)" ... Empty names become
        // "Untitled list". Every list keeps its name reserved, chosen or not.
        var projectNames = FileNames.UniqueStems(
            archive.Lists.Select(l => TodoistMapping.OneLine(l.Name).Trim() is { Length: > 0 } n ? n : "Untitled list").ToList(),
            listParts.Select(p => p?.Count ?? 0).ToList(),
            ProjectName);

        var lists = new List<ListPlan>();
        for (var i = 0; i < archive.Lists.Count; i++)
        {
            var list = archive.Lists[i];
            if (listParts[i] is not { } parts)
            {
                lists.Add(new ListPlan(list.Name, [], "not chosen"));
                continue;
            }

            var projects = new List<PlannedProject>();
            for (var p = 0; p < parts.Count; p++)
            {
                var key = $"p:{listKeys[i]}:{p + 1}";
                projects.Add(new PlannedProject(key, ProjectName(projectNames[i], p + 1, parts.Count), list.Name, p + 1,
                    parts[p].Select(item => item with { ProjectKey = key }).ToList()));
            }

            lists.Add(new ListPlan(list.Name, projects, projects.Count == 0 ? "nothing to transfer" : null));
        }

        if (FileNames.SameFileGroups(lists.SelectMany(l => l.Projects).Select(p => p.Name)).Count > 0)
        {
            // Id recovery after a lost response matches projects by name, so two planned projects may never share one.
            throw new InvalidOperationException("Two planned Todoist projects have the same name.");
        }

        var problems = new List<string>();
        var warnings = new List<string>();
        if (archive.ReadNotes.Interrupted)
        {
            warnings.Add($"The archive is incomplete (the read stopped: {archive.ReadNotes.InterruptedReason ?? "unknown reason"}). Lists that were not read are missing.");
        }

        if (archive.Lists.SelectMany(l => l.Tasks).Any(t => t.IsPartial))
        {
            warnings.Add("Some tasks were only partly readable from Microsoft Graph; they are transferred with what the archive has.");
        }

        var all = lists.SelectMany(l => l.Projects).ToList();
        var notSent = all.Where(p => state is null || !state.Sent.Contains(state.Uuid(TodoistTransfer.ProjectAdd, p.Key))).ToList();
        int? available = null;
        var clashes = new List<string>();
        if (account is not null)
        {
            if (account.MaxProjects is { } maxProjects)
            {
                available = Math.Max(0, maxProjects - account.CountedProjects);
            }

            // The transfer creates personal projects. A team project with the same name does not stop it: Todoist shows
            // it under its team, and id recovery only considers personal projects that did not exist before.
            var ours = state?.Ids.Values.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            var existing = account.Projects.Where(p => !ours.Contains(p.Id) && p.IsPersonal).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var team = account.Projects.Where(p => !ours.Contains(p.Id) && !p.IsPersonal).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sameName = notSent.Where(p => existing.Contains(p.Name)).Select(p => p.Name).ToList();
            var sameAsTeam = notSent.Where(p => team.Contains(p.Name) && !existing.Contains(p.Name)).Select(p => p.Name).ToList();
            if (sameAsTeam.Count > 0)
            {
                warnings.Add($"New personal projects will have the same name as team projects: {string.Join(", ", sameAsTeam.Select(n => $"\"{n}\""))}.");
            }

            if (sameName.Count > 0 && !settings.AllowDuplicateNames)
            {
                clashes = sameName;
            }
            else if (sameName.Count > 0)
            {
                warnings.Add($"New projects will have the same name as existing ones: {string.Join(", ", sameName.Select(n => $"\"{n}\""))}.");
            }

            var labels = all.SelectMany(p => p.Items).SelectMany(i => i.Labels).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (account.MaxLabels is { } maxLabels && labels > maxLabels)
            {
                warnings.Add($"The tasks use {labels} different labels; your plan allows {maxLabels}. Todoist may refuse some of them.");
            }

            problems.AddRange(TodoistPlanText.Problems(notSent.Count, account.MaxProjects, account.CountedProjects, available, clashes));
        }

        return new TodoistPlan
        {
            Settings = settings,
            Account = account,
            Lists = lists,
            Losses = losses,
            Problems = problems,
            Warnings = warnings,
            ProjectsNeeded = notSent.Count,
            ProjectsAvailable = available,
            NameClashes = clashes,
        };
    }

    private static List<PlannedItem> Block(
        TaskList list, TodoItem task, string taskKey, TodoistPlanSettings settings, TimeZoneInfo zone, List<Loss> losses)
    {
        if (task.IsPartial)
        {
            losses.Add(TodoistMapping.Loss(Format, list, task, LossKinds.PartialTask,
                "This task was only partly readable from Microsoft Graph; it was transferred with what the archive has."));
        }

        DateTimeOffset? reminderUtc = null;
        var fields = TodoistMapping.Map(list, task, Format, losses, withRecurrence: !task.IsCompleted, reminder =>
        {
            var at = Fmt.DateTime(reminder);
            if (task.IsCompleted)
            {
                return $"Reminder at {at} is not added because the task is completed.";
            }

            if (settings.Reminders is null)
            {
                return $"Reminder at {at} is added only if your Todoist plan includes reminders.";
            }

            if (settings.Reminders == false)
            {
                return $"Reminder at {at} is not carried: your Todoist plan does not include reminders.";
            }

            var utc = ToUtc(reminder, zone);
            if (utc <= settings.PlannedAt)
            {
                return $"Reminder at {at} is in the past and was not added.";
            }

            reminderUtc = utc;
            return null;
        });

        var completedAt = task.CompletedAt is { } c ? ToUtc(c, zone) : (DateTimeOffset?)null;
        var key = "t:" + taskKey;
        var block = new List<PlannedItem>
        {
            new()
            {
                Key = key,
                ProjectKey = string.Empty,
                Content = NonEmpty(fields.Content),
                Description = fields.Description,
                Priority = fields.HighPriority ? 4 : 1,
                Labels = fields.Labels,
                DueString = fields.DateIsRecurrence ? fields.Date : null,
                DueDate = !fields.DateIsRecurrence && fields.Date.Length > 0 ? fields.Date : null,
                Complete = task.IsCompleted,
                CompletedAtUtc = completedAt,
                ReminderUtc = reminderUtc,
                ListName = list.Name,
                TaskTitle = task.Title,
            },
        };

        for (var s = 0; s < task.Steps.Count; s++)
        {
            var step = task.Steps[s];
            if (step.IsChecked && !settings.IncludeCompleted)
            {
                losses.Add(TodoistMapping.Loss(Format, list, task, LossKinds.CheckedStepSkipped, $"Checked step skipped: {step.Title}"));
                continue;
            }

            var complete = task.IsCompleted || step.IsChecked;
            block.Add(new PlannedItem
            {
                Key = $"s:{taskKey}/{s}",
                ProjectKey = string.Empty,
                ParentKey = key,
                Content = NonEmpty(TodoistMapping.OneLine(step.Title)),
                Complete = complete,
                CompletedAtUtc = !complete ? null : step.CheckedAt is { } checkedAt ? ToUtc(checkedAt, zone) : completedAt,
                ListName = list.Name,
                TaskTitle = task.Title,
            });
        }

        return block;
    }

    /// <summary>
    /// Packs blocks (a task and its sub-tasks) into projects without splitting a block. Items that are completed
    /// right after they are added count only while their block is being written, so a project fits when the open
    /// items before a block plus the whole block stay within the limit. With no completed items this is the
    /// CSV exporter's 300-row split.
    /// </summary>
    private static List<List<PlannedItem>> Pack(List<List<PlannedItem>> blocks, int limit, TaskList list, List<Loss> losses)
    {
        var parts = new List<List<PlannedItem>>();
        var current = new List<PlannedItem>();
        var open = 0;
        foreach (var original in blocks)
        {
            var block = original;
            if (block.Count > limit)
            {
                losses.Add(new Loss(Format, list.Name, block[0].TaskTitle, LossKinds.StepOverflow,
                    $"This task has more steps than one Todoist project allows. {block.Count - limit} steps were not transferred."));
                block = block.Take(limit).ToList();
            }

            if (current.Count > 0 && open + block.Count > limit)
            {
                parts.Add(current);
                current = new List<PlannedItem>();
                open = 0;
            }

            current.AddRange(block);
            open += block.Count(i => !i.Complete);
        }

        if (current.Count > 0)
        {
            parts.Add(current);
        }

        return parts;
    }

    internal static DateTimeOffset ToUtc(DateTime wallClock, TimeZoneInfo zone) =>
        new(DateTime.SpecifyKind(TimeZones.Convert(wallClock, zone, TimeZoneInfo.Utc), DateTimeKind.Unspecified), TimeSpan.Zero);

    /// <summary>Todoist needs content; an empty To Do title becomes "(no title)".</summary>
    private static string NonEmpty(string content) => string.IsNullOrWhiteSpace(content) ? "(no title)" : content;

    private static string UniqueKey(HashSet<string> used, string candidate)
    {
        var key = candidate;
        for (var n = 2; !used.Add(key); n++)
        {
            key = $"{candidate}#{n}";
        }

        return key;
    }

    private static List<string> UniqueKeys(IEnumerable<string> ids, string fallback)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        return ids.Select((id, i) => UniqueKey(used, string.IsNullOrEmpty(id) ? $"{fallback}{i}" : id)).ToList();
    }

    private static string ProjectName(string stem, int part, int count) => count == 1 ? stem : $"{stem} - part {part}";
}

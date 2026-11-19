using System.Globalization;
using TodoExit.Core.Export;

namespace TodoExit.Core.Todoist;

/// <summary>Who reads the plan text: the command-line tool names its options, the web page names its controls.</summary>
public enum PlanAudience
{
    CommandLine,
    Web,
}

/// <summary>Plan text for people, shared by the command-line tool and the web page.</summary>
public static class TodoistPlanText
{
    internal static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"1 project", "2 projects".</summary>
    public static string Count(int n, string one, string many) => $"{N(n)} {(n == 1 ? one : many)}";

    /// <summary>
    /// One readable line per kind of loss, most frequent first, without the kind codes. Used for the whole plan
    /// and, on the web page, per list.
    /// </summary>
    public static IReadOnlyList<string> LossLines(IEnumerable<Loss> losses)
    {
        var aggregated = LossCatalog.Aggregate(losses);
        return aggregated
            .GroupBy(l => l.Kind)
            .Select(g => (Kind: g.Key, Count: g.Key == LossKinds.ListSplit ? g.Count() : g.Sum(l => l.Count)))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Kind, StringComparer.Ordinal)
            .Select(x => LossLine(x.Kind, x.Count))
            .ToList();
    }

    public static string LossLine(string kind, int n) => kind switch
    {
        LossKinds.CompletedSkipped =>
            $"{Count(n, "completed task is", "completed tasks are")} left out. Completed tasks are transferred only if you choose to include them.",
        LossKinds.CheckedStepSkipped =>
            $"{Count(n, "checked step is", "checked steps are")} left out. Checked steps are transferred only if you choose to include completed tasks.",
        LossKinds.ReminderNotCarried =>
            $"{Count(n, "reminder is", "reminders are")} not added: your Todoist plan has no reminders, the time has passed, or the task is completed. Set them again in Todoist if you need them.",
        LossKinds.AttachmentNotCarried =>
            $"{Count(n, "task has", "tasks have")} attachments. The files are not transferred; save them from To Do if you need them.",
        LossKinds.RecurrenceApproximated =>
            $"{Count(n, "repeat rule is", "repeat rules are")} written as the closest Todoist phrase. Check {(n == 1 ? "this task" : "these tasks")} in Todoist.",
        LossKinds.SharedListMembersNotCarried =>
            $"{Count(n, "list is", "lists are")} shared in To Do. Members and sharing are not carried; share the Todoist project again.",
        LossKinds.StartDateNotCarried =>
            $"{Count(n, "start date is", "start dates are")} not carried. Todoist has no start date.",
        LossKinds.StepOverflow =>
            $"{Count(n, "task has", "tasks have")} more steps than one Todoist project allows. The extra steps are not transferred.",
        LossKinds.ListSplit =>
            $"{Count(n, "list has", "lists have")} more tasks than one Todoist project allows, so {(n == 1 ? "it is" : "each is")} split into several projects.",
        LossKinds.PartialTask =>
            $"{Count(n, "task was", "tasks were")} only partly readable from Microsoft Graph and {(n == 1 ? "is" : "are")} transferred with what the archive has.",
        LossKinds.ListNotSelected =>
            $"{Count(n, "task is", "tasks are")} in lists that were not chosen for this transfer.",
        LossKinds.TodoistError =>
            $"{Count(n, "item was", "items were")} refused by Todoist. The errors are listed with the tasks.",
        LossKinds.ParentNotCreated =>
            $"{Count(n, "item was", "items were")} not created because {(n == 1 ? "its" : "their")} project or parent task could not be created.",
        _ => $"{N(n)} x {kind}",
    };

    /// <summary>The problems that stop the transfer, as facts. The same for every audience.</summary>
    internal static IReadOnlyList<string> Problems(int needed, int? maxProjects, int counted, int? available, IReadOnlyList<string> clashes)
    {
        var problems = new List<string>();
        if (available is { } free && maxProjects is { } max && needed > free)
        {
            problems.Add(
                $"This transfer needs {Count(needed, "new Todoist project", "new Todoist projects")}, but your plan allows {Count(max, "project", "projects")} " +
                $"and you already have {N(counted)}, so {(free == 0 ? "no more fit" : $"only {N(free)} more fit")}.");
        }

        if (clashes.Count > 0)
        {
            problems.Add(
                $"Your Todoist account already has {(clashes.Count == 1 ? "a project" : "projects")} named {Names(clashes)}. " +
                "todo-exit never adds tasks to an existing project.");
        }

        return problems;
    }

    /// <summary>What has to change before the transfer can start. Empty when the plan has no problems.</summary>
    public static IReadOnlyList<string> Fixes(TodoistPlan plan, PlanAudience audience)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var cli = audience == PlanAudience.CommandLine;
        var fixes = new List<string>();
        if (plan.TooManyProjects && plan.ProjectsAvailable is { } free)
        {
            var room = plan.ProjectsNeeded - free;
            var fewer = cli ? "choose fewer lists with --lists" : "choose fewer lists";
            fixes.Add(free == 0
                ? $"Make room for {Count(room, "project", "projects")} in Todoist (archive or delete {(room == 1 ? "one" : "some")}), or {fewer}."
                : $"Make room for {Count(room, "more project", "more projects")} in Todoist (archive or delete {(room == 1 ? "one" : "some")}), " +
                  $"or {fewer} so that at most {Count(free, "project is", "projects are")} needed.");
        }

        if (plan.NameClashes.Count > 0)
        {
            var one = plan.NameClashes.Count == 1;
            var what = $"the Todoist {(one ? "project" : "projects")} named {Names(plan.NameClashes)}";
            fixes.Add(cli
                ? $"Rename or remove {what}, leave {(one ? "that list" : "those lists")} out with --lists, " +
                  "or add --allow-duplicate-names to create a new project with the same name."
                : $"Rename or remove {what}, or leave {(one ? "that list" : "those lists")} out.");
        }

        return fixes;
    }

    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"\"{n}\""));
}

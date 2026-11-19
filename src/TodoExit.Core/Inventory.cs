using TodoExit.Core.Model;

namespace TodoExit.Core;

public sealed record InventoryCounts(
    int Lists,
    int Tasks,
    int Open,
    int Completed,
    int Recurring,
    int WithSteps,
    int WithNotes,
    int WithReminders,
    int WithAttachments,
    int WithLinkedResources,
    int SharedLists,
    int Unreadable = 0,
    int Partial = 0)
{
    public static InventoryCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public InventoryCounts Add(InventoryCounts other) => new(
        Lists + other.Lists,
        Tasks + other.Tasks,
        Open + other.Open,
        Completed + other.Completed,
        Recurring + other.Recurring,
        WithSteps + other.WithSteps,
        WithNotes + other.WithNotes,
        WithReminders + other.WithReminders,
        WithAttachments + other.WithAttachments,
        WithLinkedResources + other.WithLinkedResources,
        SharedLists + other.SharedLists,
        Unreadable + other.Unreadable,
        Partial + other.Partial);

    /// <summary>Row labels and values in display order.</summary>
    public IReadOnlyList<(string Label, int Value)> Rows() =>
    [
        ("Lists", Lists),
        ("Tasks", Tasks),
        ("Open", Open),
        ("Completed", Completed),
        ("Recurring", Recurring),
        ("With steps", WithSteps),
        ("With notes", WithNotes),
        ("With reminders", WithReminders),
        ("With attachments", WithAttachments),
        ("With linked resources", WithLinkedResources),
        ("Shared lists", SharedLists),
        ("Unreadable (skipped)", Unreadable),
        ("Partial", Partial),
    ];
}

public sealed record ListInventory(string ListName, InventoryCounts Counts);

public sealed record Inventory(InventoryCounts Totals, IReadOnlyList<ListInventory> PerList)
{
    public static Inventory From(TodoArchive archive)
    {
        var unreadable = archive.ReadNotes.Errors.Where(e => e.Kind == ReadErrorKinds.TaskUnreadable).ToList();
        var perList = archive.Lists
            .Select(l => new ListInventory(l.Name, Count(l, unreadable.Count(e => e.ListId == l.Id))))
            .ToList();
        var totals = perList.Aggregate(InventoryCounts.Empty, (acc, l) => acc.Add(l.Counts));
        return new Inventory(totals, perList);
    }

    private static InventoryCounts Count(TaskList list, int unreadable)
    {
        var tasks = list.Tasks;
        return new InventoryCounts(
            Lists: 1,
            Tasks: tasks.Count,
            Open: tasks.Count(t => !t.IsCompleted),
            Completed: tasks.Count(t => t.IsCompleted),
            Recurring: tasks.Count(t => t.Recurrence is not null),
            WithSteps: tasks.Count(t => t.Steps.Count > 0),
            WithNotes: tasks.Count(t => t.Notes is not null),
            WithReminders: tasks.Count(t => t.Reminder is not null),
            WithAttachments: tasks.Count(t => t.Attachments.Count > 0),
            WithLinkedResources: tasks.Count(t => t.LinkedResources.Count > 0),
            SharedLists: list.IsShared ? 1 : 0,
            Unreadable: unreadable,
            Partial: tasks.Count(t => t.IsPartial));
    }
}

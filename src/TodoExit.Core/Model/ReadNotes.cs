using System.Text.Json.Nodes;

namespace TodoExit.Core.Model;

public static class ReadErrorKinds
{
    /// <summary>A task could not be read at all and is missing from the archive.</summary>
    public const string TaskUnreadable = "task-unreadable";

    /// <summary>Only some properties of a task could be read; the archive holds a partial object.</summary>
    public const string TaskPartial = "task-partial";

    public const string StepsUnreadable = "steps-unreadable";

    public const string AttachmentsUnreadable = "attachments-unreadable";

    /// <summary>Graph refused to list attachments with the read-only permission. Recorded once per archive.</summary>
    public const string AttachmentsDenied = "attachments-denied";
}

/// <summary>Something the reader could not get from Microsoft Graph.</summary>
/// <param name="Position">Zero-based offset of the task in its list, for task-level errors.</param>
public sealed record ReadError(
    string Kind,
    string? ListId,
    string? ListName,
    string? TaskId,
    string? TaskTitle,
    int? Position,
    string Message);

/// <summary>How far an interrupted read got in one list. Stored in readNotes.progress of a partial archive.</summary>
/// <param name="NextSkip">The $skip of the next task page to read when <paramref name="TasksComplete"/> is false.</param>
public sealed record ListReadProgress(string ListId, bool TasksComplete, int NextSkip, bool StepsComplete, bool AttachmentsComplete)
{
    public bool IsComplete => TasksComplete && StepsComplete && AttachmentsComplete;
}

/// <summary>
/// What the read could not carry into the archive. Stored as the optional top-level "readNotes"
/// property; it is omitted when there is nothing to note.
/// </summary>
/// <remarks>
/// Older archives may contain "stepsForCompletedTasksSkipped". It is ignored here and, like any unknown property,
/// kept when such an archive is loaded and saved again.
/// </remarks>
public sealed record ReadNotes(IReadOnlyList<ReadError> Errors)
{
    public const string PropertyName = "readNotes";

    /// <summary>Marks a task object in the archive that was salvaged from a broken Graph response and is incomplete.</summary>
    public const string PartialMarker = "todoExitPartial";

    public static ReadNotes Empty { get; } = new([]);

    /// <summary>
    /// With --verify-steps: how many tasks without inline steps turned out to have steps. Non-zero means the
    /// "steps come inline" assumption does not hold for this account.
    /// </summary>
    public int StepsFoundByVerification { get; init; }

    /// <summary>
    /// Graph refused to list attachments (observed with Tasks.Read on personal accounts). Attachment arrays are empty;
    /// tasks keep hasAttachments.
    /// </summary>
    public bool AttachmentsDenied { get; init; }

    /// <summary>True for a partial archive: the read stopped before it finished.</summary>
    public bool Interrupted { get; init; }

    /// <summary>Short text saying why the read stopped. Only for partial archives.</summary>
    public string? InterruptedReason { get; init; }

    /// <summary>One entry per list the interrupted read had started. Only for partial archives.</summary>
    public IReadOnlyList<ListReadProgress> Progress { get; init; } = [];

    /// <summary>The raw list collection as Graph returned it, so a resume does not depend on list order.</summary>
    public JsonArray? PendingLists { get; init; }

    public bool IsEmpty => Errors.Count == 0 && !Interrupted && !AttachmentsDenied && StepsFoundByVerification == 0;

    /// <summary>The first list in <see cref="Progress"/> that is not complete, if any.</summary>
    public ListReadProgress? ListInProgress => Progress.FirstOrDefault(p => !p.IsComplete);

    /// <summary>Display name of a list from <see cref="PendingLists"/>, if known.</summary>
    public string? ListName(string listId) =>
        PendingLists?.OfType<JsonObject>().FirstOrDefault(l => Str(l, "id") == listId) is { } list ? Str(list, "displayName") : null;

    public JsonObject ToJson()
    {
        var errors = new JsonArray();
        foreach (var e in Errors)
        {
            errors.Add((JsonNode)new JsonObject
            {
                ["kind"] = e.Kind,
                ["listId"] = e.ListId,
                ["listName"] = e.ListName,
                ["taskId"] = e.TaskId,
                ["taskTitle"] = e.TaskTitle,
                ["position"] = e.Position,
                ["message"] = e.Message,
            });
        }

        var result = new JsonObject
        {
            ["errors"] = errors,
        };

        if (StepsFoundByVerification > 0)
        {
            result["stepsFoundByVerification"] = StepsFoundByVerification;
        }

        if (AttachmentsDenied)
        {
            result["attachmentsDenied"] = true;
        }

        if (Interrupted)
        {
            var progress = new JsonArray();
            foreach (var p in Progress)
            {
                progress.Add((JsonNode)new JsonObject
                {
                    ["listId"] = p.ListId,
                    ["tasksComplete"] = p.TasksComplete,
                    ["nextSkip"] = p.NextSkip,
                    ["stepsComplete"] = p.StepsComplete,
                    ["attachmentsComplete"] = p.AttachmentsComplete,
                });
            }

            result["interrupted"] = true;
            result["interruptedReason"] = InterruptedReason;
            result["progress"] = progress;
            result["pendingLists"] = PendingLists?.DeepClone() ?? new JsonArray();
        }

        return result;
    }

    /// <summary>Reads the property tolerantly. Missing or malformed input gives <see cref="Empty"/>.</summary>
    public static ReadNotes FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return Empty;
        }

        var errors = new List<ReadError>();
        if (obj["errors"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                errors.Add(new ReadError(
                    Str(item, "kind") ?? "unknown",
                    Str(item, "listId"),
                    Str(item, "listName"),
                    Str(item, "taskId"),
                    Str(item, "taskTitle"),
                    item["position"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? p : null,
                    Str(item, "message") ?? string.Empty));
            }
        }

        var interrupted = obj["interrupted"] is JsonValue iv && iv.TryGetValue<bool>(out var i) && i;
        var progress = new List<ListReadProgress>();
        if (obj["progress"] is JsonArray progressArray)
        {
            foreach (var item in progressArray.OfType<JsonObject>())
            {
                if (Str(item, "listId") is not { } listId)
                {
                    continue;
                }

                progress.Add(new ListReadProgress(
                    listId,
                    Bool(item, "tasksComplete"),
                    item["nextSkip"] is JsonValue nv && nv.TryGetValue<int>(out var n) ? n : 0,
                    Bool(item, "stepsComplete"),
                    Bool(item, "attachmentsComplete")));
            }
        }

        var attachmentsDenied = obj["attachmentsDenied"] is JsonValue av && av.TryGetValue<bool>(out var a) && a;
        var found = obj["stepsFoundByVerification"] is JsonValue fv && fv.TryGetValue<int>(out var f) ? f : 0;
        if (errors.Count == 0 && !interrupted && !attachmentsDenied && found == 0)
        {
            return Empty;
        }

        return new ReadNotes(errors)
        {
            StepsFoundByVerification = found,
            AttachmentsDenied = attachmentsDenied,
            Interrupted = interrupted,
            InterruptedReason = interrupted ? Str(obj, "interruptedReason") : null,
            Progress = interrupted ? progress : [],
            PendingLists = interrupted && obj["pendingLists"] is JsonArray pending ? pending.DeepClone().AsArray() : null,
        };
    }

    private static bool Bool(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

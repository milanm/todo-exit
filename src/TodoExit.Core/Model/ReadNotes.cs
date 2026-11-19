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

/// <summary>
/// What the read could not carry into the archive. Stored as the optional top-level "readNotes"
/// property; it is omitted when there is nothing to note.
/// </summary>
public sealed record ReadNotes(bool StepsForCompletedTasksSkipped, IReadOnlyList<ReadError> Errors)
{
    public const string PropertyName = "readNotes";

    /// <summary>Marks a task object in the archive that was salvaged with $select and is incomplete.</summary>
    public const string PartialMarker = "todoExitPartial";

    public static ReadNotes Empty { get; } = new(false, []);

    public bool IsEmpty => !StepsForCompletedTasksSkipped && Errors.Count == 0;

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

        return new JsonObject
        {
            ["stepsForCompletedTasksSkipped"] = StepsForCompletedTasksSkipped,
            ["errors"] = errors,
        };
    }

    /// <summary>Reads the property tolerantly. Missing or malformed input gives <see cref="Empty"/>.</summary>
    public static ReadNotes FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return Empty;
        }

        var skipped = obj["stepsForCompletedTasksSkipped"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
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

        return skipped || errors.Count > 0 ? new ReadNotes(skipped, errors) : Empty;
    }

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

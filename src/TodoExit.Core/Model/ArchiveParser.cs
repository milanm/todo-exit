using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TodoExit.Core.Archive;

namespace TodoExit.Core.Model;

/// <summary>
/// Builds the typed model from the raw archive. Tolerates missing or malformed properties everywhere:
/// anything it cannot read becomes a default value instead of an error.
/// </summary>
public static partial class ArchiveParser
{
    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneDesignator();

    public static TodoArchive Parse(TodoArchiveDocument document, TimeZoneInfo? targetZone = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var zone = targetZone ?? TimeZoneInfo.Local;
        var root = document.Root;

        DateTimeOffset? exportedAt = null;
        if (Str(root, "exportedAt") is { } exported
            && DateTimeOffset.TryParse(exported, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e))
        {
            exportedAt = e;
        }

        var lists = new List<TaskList>();
        if (root["lists"] is JsonArray listArray)
        {
            foreach (var entry in listArray.OfType<JsonObject>())
            {
                lists.Add(ParseList(entry, zone));
            }
        }

        return new TodoArchive(exportedAt, lists) { ReadNotes = document.ReadNotes };
    }

    private static TaskList ParseList(JsonObject entry, TimeZoneInfo zone)
    {
        var list = entry["list"] as JsonObject ?? new JsonObject();
        var wellknown = Str(list, "wellknownListName");
        if (string.Equals(wellknown, "none", StringComparison.OrdinalIgnoreCase))
        {
            wellknown = null;
        }

        var tasks = new List<TodoItem>();
        if (entry["tasks"] is JsonArray taskArray)
        {
            foreach (var task in taskArray.OfType<JsonObject>())
            {
                tasks.Add(ParseTask(task, zone));
            }
        }

        var name = Str(list, "displayName");
        return new TaskList(
            Id: Str(list, "id") ?? string.Empty,
            Name: string.IsNullOrWhiteSpace(name) ? "Untitled list" : name,
            IsOwner: Bool(list, "isOwner") ?? true,
            IsShared: Bool(list, "isShared") ?? false,
            WellknownName: wellknown,
            Tasks: tasks);
    }

    public static TodoItem ParseTask(JsonObject task, TimeZoneInfo zone)
    {
        var reminderOn = Bool(task, "isReminderOn") ?? false;

        return new TodoItem
        {
            Id = Str(task, "id") ?? string.Empty,
            Title = (Str(task, "title") ?? string.Empty).Trim(),
            IsPartial = Bool(task, ReadNotes.PartialMarker) ?? false,
            Notes = ParseBody(task["body"] as JsonObject),
            IsCompleted = string.Equals(Str(task, "status"), "completed", StringComparison.OrdinalIgnoreCase),
            Importance = ParseImportance(Str(task, "importance")),
            DueDate = DateOf(ParseDateTimeTimeZone(task["dueDateTime"], zone)),
            StartDate = DateOf(ParseDateTimeTimeZone(task["startDateTime"], zone)),
            Reminder = reminderOn ? ParseDateTimeTimeZone(task["reminderDateTime"], zone) : null,
            CompletedAt = ParseDateTimeTimeZone(task["completedDateTime"], zone),
            CreatedAt = ParseInstant(Str(task, "createdDateTime"), zone),
            ModifiedAt = ParseInstant(Str(task, "lastModifiedDateTime"), zone),
            Categories = StringArray(task["categories"]),
            Steps = ParseSteps(task["checklistItems"]),
            Recurrence = ParseRecurrence(task["recurrence"] as JsonObject),
            LinkedResources = ParseLinkedResources(task["linkedResources"]),
            Attachments = ParseAttachments(task["attachments"]),
            HasAttachments = Bool(task, "hasAttachments") ?? false,
        };
    }

    private static Importance ParseImportance(string? value) => value?.ToLowerInvariant() switch
    {
        "low" => Importance.Low,
        "high" => Importance.High,
        _ => Importance.Normal,
    };

    private static string? ParseBody(JsonObject? body)
    {
        if (body is null)
        {
            return null;
        }

        var content = Str(body, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        if (string.Equals(Str(body, "contentType"), "html", StringComparison.OrdinalIgnoreCase))
        {
            return HtmlText.ToPlainText(content);
        }

        var text = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return text.Length == 0 ? null : text;
    }

    private static DateOnly? DateOf(DateTime? value) => value is { } v ? DateOnly.FromDateTime(v) : null;

    /// <summary>Reads a Graph dateTimeTimeZone object and converts it to the target zone.</summary>
    internal static DateTime? ParseDateTimeTimeZone(JsonNode? node, TimeZoneInfo target)
    {
        if (node is not JsonObject obj || Str(obj, "dateTime") is not { } text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        var timePart = text.IndexOf('T') is var t and >= 0 ? text[t..] : string.Empty;
        if (timePart.Length > 0 && ZoneDesignator().IsMatch(timePart))
        {
            // The string carries its own offset; that wins over the timeZone property.
            return ParseInstant(text, target);
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            return null;
        }

        var source = TimeZones.ResolveOrUtc(Str(obj, "timeZone"));
        return TimeZones.Convert(wall, source, target);
    }

    /// <summary>Reads an ISO 8601 instant (e.g. createdDateTime) and converts it to the target zone.</summary>
    private static DateTime? ParseInstant(string? text, TimeZoneInfo target)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            return null;
        }

        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, target).DateTime, DateTimeKind.Unspecified);
    }

    private static IReadOnlyList<Step> ParseSteps(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var steps = new List<Step>();
        foreach (var item in array.OfType<JsonObject>())
        {
            var title = (Str(item, "displayName") ?? string.Empty).Trim();
            steps.Add(new Step(title, Bool(item, "isChecked") ?? false));
        }

        return steps;
    }

    private static IReadOnlyList<LinkedResource> ParseLinkedResources(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array.OfType<JsonObject>()
            .Select(o => new LinkedResource(Str(o, "displayName"), Str(o, "applicationName"), Str(o, "webUrl")))
            .Where(l => !string.IsNullOrWhiteSpace(l.WebUrl) || !string.IsNullOrWhiteSpace(l.DisplayName))
            .ToList();
    }

    private static IReadOnlyList<AttachmentInfo> ParseAttachments(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array.OfType<JsonObject>()
            .Select(o => new AttachmentInfo(
                string.IsNullOrWhiteSpace(Str(o, "name")) ? "attachment" : Str(o, "name")!,
                Long(o, "size"),
                Str(o, "contentType")))
            .ToList();
    }

    internal static Recurrence? ParseRecurrence(JsonObject? recurrence)
    {
        if (recurrence?["pattern"] is not JsonObject pattern)
        {
            return null;
        }

        var type = Str(pattern, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var range = recurrence["range"] as JsonObject;
        var days = new List<DayOfWeek>();
        foreach (var d in StringArray(pattern["daysOfWeek"]))
        {
            if (ParseDay(d) is { } day && !days.Contains(day))
            {
                days.Add(day);
            }
        }

        var interval = Int(pattern, "interval") ?? 1;
        return new Recurrence
        {
            PatternType = type,
            Interval = interval < 1 ? 1 : interval,
            DaysOfWeek = days,
            DayOfMonth = Int(pattern, "dayOfMonth") ?? 0,
            Month = Int(pattern, "month") ?? 0,
            Index = Str(pattern, "index"),
            RangeType = Str(range, "type") ?? "noEnd",
            RangeStartDate = ParseDate(Str(range, "startDate")),
            RangeEndDate = ParseDate(Str(range, "endDate")),
            NumberOfOccurrences = Int(range, "numberOfOccurrences") ?? 0,
        };
    }

    private static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            || DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
        {
            // Graph uses 0001-01-01 for "no date".
            return d.Year <= 1 ? null : d;
        }

        return null;
    }

    private static DayOfWeek? ParseDay(string value) => value.Trim().ToLowerInvariant() switch
    {
        "sunday" => DayOfWeek.Sunday,
        "monday" => DayOfWeek.Monday,
        "tuesday" => DayOfWeek.Tuesday,
        "wednesday" => DayOfWeek.Wednesday,
        "thursday" => DayOfWeek.Thursday,
        "friday" => DayOfWeek.Friday,
        "saturday" => DayOfWeek.Saturday,
        _ => null,
    };

    // ---- tolerant JSON helpers ----
    internal static string? Str(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<string>(out var s))
        {
            return s;
        }

        return v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? v.ToJsonString()
            : null;
    }

    private static bool? Bool(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<bool>(out var b))
        {
            return b;
        }

        return v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) ? parsed : null;
    }

    private static int? Int(JsonObject? obj, string name)
    {
        var l = Long(obj, name);
        return l is { } x && x is >= int.MinValue and <= int.MaxValue ? (int)x : null;
    }

    private static long? Long(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue v)
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

    private static IReadOnlyList<string> StringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                result.Add(s.Trim());
            }
        }

        return result;
    }
}

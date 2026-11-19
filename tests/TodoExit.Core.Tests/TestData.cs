using System.Text.Json.Nodes;
using TodoExit.Core.Archive;
using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

internal static class TestData
{
    public static readonly TimeZoneInfo Belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");

    public static JsonObject Task(string title, string? json = null)
    {
        var task = json is null ? new JsonObject() : (JsonObject)JsonNode.Parse(json)!;
        task["title"] = title;
        task["id"] ??= "id-" + title.GetHashCode().ToString("x");
        return task;
    }

    public static JsonObject List(string name, bool shared = false, string? id = null) => new()
    {
        ["id"] = id ?? "list-" + name,
        ["displayName"] = name,
        ["isOwner"] = true,
        ["isShared"] = shared,
        ["wellknownListName"] = "none",
    };

    public static TodoArchiveDocument Document(params (JsonObject List, JsonObject[] Tasks)[] lists) =>
        TodoArchiveDocument.Create(
            new DateTimeOffset(2026, 11, 19, 13, 30, 0, TimeSpan.Zero),
            lists.Select(l => new ArchivedList(l.List, l.Tasks)));

    public static TodoArchive Archive(params (JsonObject List, JsonObject[] Tasks)[] lists) =>
        ArchiveParser.Parse(Document(lists), Belgrade);

    public static TodoItem ParseTask(string json) =>
        ArchiveParser.ParseTask((JsonObject)JsonNode.Parse(json)!, Belgrade);
}

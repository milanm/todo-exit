using TodoExit.Core.Graph;

namespace TodoExit.Core.Tests;

public class BrokenTaskParserTests
{
    private const string RealShape =
        "{\"@odata.context\":\"https://graph.microsoft.com/v1.0/$metadata#tasks\",\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/todo/lists/L/tasks?$top=1&$skip=9270\","
        + "\"value\":[{\"@odata.etag\":\"W/\\\"abc\\\"\",\"importance\":\"normal\",\"isReminderOn\":false,\"status\":\"notStarted\",\"title\":\"Pay \\\"rent\\\", ćevapi\","
        + "\"createdDateTime\":\"2026-01-01T10:00:00.0000000Z\",\"lastModifiedDateTime\":\"2026-02-01T10:00:00.0000000Z\",\"hasAttachments\":false,\"categories\":[],\"id\":\"AAMk=\","
        + "\"body\":{\"content\":\"x\",\"contentType\":\"text\"},\"completedDateTime\":{\"dateTime\":\"2026-03-01T00:00:00.0000000\",\"timeZone\":\"UTC\"},"
        + "\"dueDateTime\":{\"dateTime\":\"2026-03-01T00:00:00.0000000\",\"timeZone\":\"UTC\"},\"reminderDateTime\":{\"dateTime\":\"2026-03-01T08:00:00.0000000\",\"timeZone\":\"UTC\"},"
        + "\"linkedResources@odata.context\":\"https://graph.microsoft.com/v1.0/$metadata#linkedResources\",\"linkedResources\":["
        + "{\"error\":{\"code\":\"InternalServerError\",\"message\":\"An internal server error occurred.\",\"innerError\":{\"date\":\"2026-11-20T10:00:00\",\"request-id\":\"r\",\"client-request-id\":\"c\"}}}";

    [Fact]
    public void Real_shaped_body_keeps_every_complete_property()
    {
        var result = BrokenTaskParser.Parse(RealShape);

        Assert.Equal("linkedResources", result.FailedProperty);
        Assert.Equal("InternalServerError", result.ErrorCode);
        var task = result.Task!;
        Assert.Equal(
            ["@odata.etag", "importance", "isReminderOn", "status", "title", "createdDateTime", "lastModifiedDateTime",
             "hasAttachments", "categories", "id", "body", "completedDateTime", "dueDateTime", "reminderDateTime"],
            task.Select(p => p.Key));
        Assert.Equal("Pay \"rent\", ćevapi", (string)task["title"]!);
        Assert.Equal("AAMk=", (string)task["id"]!);
        Assert.False((bool)task["isReminderOn"]!);
        Assert.Equal("UTC", (string)task["reminderDateTime"]!["timeZone"]!);
    }

    [Fact]
    public void Error_text_inside_a_string_is_not_mistaken_for_the_appended_error()
    {
        var body = "{\"value\":[{\"id\":\"1\",\"title\":\"{\\\"error\\\":{ see log\",\"body\":{\"content\":\"a";
        var result = BrokenTaskParser.Parse(body);
        Assert.Null(result.ErrorCode);
        Assert.Equal("body", result.FailedProperty);
        Assert.Equal("1", (string)result.Task!["id"]!);
        Assert.Equal("{\"error\":{ see log", (string)result.Task["title"]!);
    }

    [Fact]
    public void Body_without_value_gives_nothing()
    {
        var result = BrokenTaskParser.Parse("{\"@odata.context\":\"x\"," + "{\"error\":{\"code\":\"E\"}}");
        Assert.True(result.Task is null || result.Task.Count == 0);
        Assert.Equal("E", result.ErrorCode);
    }

    [Fact]
    public void Truncated_number_is_not_kept()
    {
        var result = BrokenTaskParser.Parse("{\"value\":[{\"id\":\"1\",\"size\":12");
        Assert.Equal("size", result.FailedProperty);
        Assert.False(result.Task!.ContainsKey("size"));
    }
}

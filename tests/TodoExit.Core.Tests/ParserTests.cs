using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

public class ParserTests
{
    [Fact]
    public void Due_date_set_in_Belgrade_comes_back_as_previous_day_utc_and_converts_back()
    {
        var task = TestData.ParseTask("""
            { "title": "t", "dueDateTime": { "dateTime": "2026-11-24T23:00:00.0000000", "timeZone": "UTC" } }
            """);

        Assert.Equal(new DateOnly(2026, 11, 25), task.DueDate);
    }

    [Fact]
    public void Due_date_in_utc_target_stays_on_utc_day()
    {
        var doc = TestData.Document((TestData.List("L"), [TestData.Task("t", """
            { "dueDateTime": { "dateTime": "2026-11-24T23:00:00.0000000", "timeZone": "UTC" } }
            """)]));

        var archive = ArchiveParser.Parse(doc, TimeZoneInfo.Utc);

        Assert.Equal(new DateOnly(2026, 11, 24), archive.Lists[0].Tasks[0].DueDate);
    }

    [Fact]
    public void Summer_time_due_date_converts_with_daylight_offset()
    {
        var task = TestData.ParseTask("""
            { "title": "t", "dueDateTime": { "dateTime": "2027-06-29T22:00:00.0000000", "timeZone": "UTC" } }
            """);

        Assert.Equal(new DateOnly(2027, 6, 30), task.DueDate);
    }

    [Theory]
    [InlineData("Central Europe Standard Time")]
    [InlineData("Europe/Belgrade")]
    public void Source_time_zone_may_be_windows_or_iana_id(string zone)
    {
        var task = TestData.ParseTask($$"""
            { "title": "t", "isReminderOn": true,
              "reminderDateTime": { "dateTime": "2026-11-25T09:00:00.0000000", "timeZone": "{{zone}}" } }
            """);

        Assert.Equal(new DateTime(2026, 11, 25, 9, 0, 0), task.Reminder);
    }

    [Fact]
    public void Unknown_source_time_zone_falls_back_to_utc()
    {
        var task = TestData.ParseTask("""
            { "title": "t", "isReminderOn": true,
              "reminderDateTime": { "dateTime": "2026-11-25T08:00:00.0000000", "timeZone": "Mars/Olympus" } }
            """);

        Assert.Equal(new DateTime(2026, 11, 25, 9, 0, 0), task.Reminder);
    }

    [Fact]
    public void Reminder_is_ignored_when_reminder_is_off()
    {
        var task = TestData.ParseTask("""
            { "title": "t", "isReminderOn": false,
              "reminderDateTime": { "dateTime": "2026-11-25T08:00:00.0000000", "timeZone": "UTC" } }
            """);

        Assert.Null(task.Reminder);
    }

    [Fact]
    public void Html_body_becomes_plain_text()
    {
        var task = TestData.ParseTask("""
            { "title": "t", "body": { "contentType": "html",
              "content": "<html><head><style>p{}</style></head><body><div>First&nbsp;line<br>Second &amp; third</div><div><br></div><div>After blank</div><p></p><p></p><ul><li>One</li><li>Two</li></ul></body></html>" } }
            """);

        Assert.Equal("First line\nSecond & third\n\nAfter blank\n\n- One\n- Two", task.Notes);
    }

    [Fact]
    public void Paragraphs_are_separated_by_one_blank_line()
    {
        Assert.Equal("A\n\nB", HtmlText.ToPlainText("<p>A</p>\r\n\r\n<p></p><p></p><p>B</p>"));
    }

    [Theory]
    [InlineData("""{ "title": "t", "body": { "content": "", "contentType": "text" } }""")]
    [InlineData("""{ "title": "t", "body": { "content": "<html><body><div></div></body></html>", "contentType": "html" } }""")]
    [InlineData("""{ "title": "t", "body": { "content": "   \n  ", "contentType": "text" } }""")]
    [InlineData("""{ "title": "t" }""")]
    public void Empty_body_becomes_null(string json)
    {
        Assert.Null(TestData.ParseTask(json).Notes);
    }

    [Fact]
    public void Text_body_is_kept_and_trimmed()
    {
        var task = TestData.ParseTask("""{ "title": "t", "body": { "content": "  line 1\r\nline 2  ", "contentType": "text" } }""");
        Assert.Equal("line 1\nline 2", task.Notes);
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("notStarted", false)]
    [InlineData("inProgress", false)]
    [InlineData("waitingOnOthers", false)]
    [InlineData("deferred", false)]
    public void Status_maps_to_completed(string status, bool completed)
    {
        var task = TestData.ParseTask($$"""{ "title": "t", "status": "{{status}}" }""");
        Assert.Equal(completed, task.IsCompleted);
    }

    [Theory]
    [InlineData("low", Importance.Low)]
    [InlineData("normal", Importance.Normal)]
    [InlineData("high", Importance.High)]
    [InlineData("weird", Importance.Normal)]
    public void Importance_maps(string value, Importance expected)
    {
        var task = TestData.ParseTask($$"""{ "title": "t", "importance": "{{value}}" }""");
        Assert.Equal(expected, task.Importance);
    }

    [Fact]
    public void Missing_properties_are_tolerated_everywhere()
    {
        var task = TestData.ParseTask("""
            { "recurrence": { "range": {} }, "checklistItems": [ {} ], "linkedResources": [ { "webUrl": "https://x" } ],
              "attachments": [ {} ], "dueDateTime": {}, "body": null, "categories": [ null, 3, "A" ] }
            """);

        Assert.Equal(string.Empty, task.Title);
        Assert.Null(task.Recurrence);
        Assert.Null(task.DueDate);
        Assert.Single(task.Steps);
        Assert.Single(task.LinkedResources);
        Assert.Equal("attachment", task.Attachments[0].Name);
        Assert.Equal(["A"], task.Categories);
    }

    [Fact]
    public void Steps_categories_links_attachments_and_recurrence_are_parsed()
    {
        var task = TestData.ParseTask("""
            {
              "title": "Full",
              "categories": ["Blue category", "Work"],
              "checklistItems": [ { "displayName": "a", "isChecked": true }, { "displayName": "b", "isChecked": false } ],
              "linkedResources": [ { "webUrl": "https://example.com/x", "applicationName": "Outlook", "displayName": "Mail" } ],
              "attachments": [ { "name": "f.pdf", "size": 2048, "contentType": "application/pdf" } ],
              "createdDateTime": "2026-10-01T08:00:00.0000000Z",
              "completedDateTime": { "dateTime": "2026-11-01T00:00:00.0000000", "timeZone": "UTC" },
              "startDateTime": { "dateTime": "2026-11-09T23:00:00.0000000", "timeZone": "UTC" },
              "recurrence": {
                "pattern": { "type": "weekly", "interval": 1, "daysOfWeek": ["friday", "monday"] },
                "range": { "type": "endDate", "startDate": "2026-11-01", "endDate": "2027-06-30" }
              }
            }
            """);

        Assert.Equal(["Blue category", "Work"], task.Categories);
        Assert.Equal([new Step("a", true), new Step("b", false)], task.Steps);
        Assert.Equal(new LinkedResource("Mail", "Outlook", "https://example.com/x"), task.LinkedResources[0]);
        Assert.Equal(new AttachmentInfo("f.pdf", 2048, "application/pdf"), task.Attachments[0]);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), task.CreatedAt);
        Assert.Equal(new DateTime(2026, 11, 1, 1, 0, 0), task.CompletedAt);
        Assert.Equal(new DateOnly(2026, 11, 10), task.StartDate);
        Assert.NotNull(task.Recurrence);
        Assert.Equal("weekly", task.Recurrence!.PatternType);
        Assert.Equal([DayOfWeek.Friday, DayOfWeek.Monday], task.Recurrence.DaysOfWeek);
        Assert.Equal("endDate", task.Recurrence.RangeType);
        Assert.Equal(new DateOnly(2027, 6, 30), task.Recurrence.RangeEndDate);
    }

    [Fact]
    public void List_properties_are_parsed()
    {
        var archive = TestData.Archive((TestData.List("Groceries", shared: true), []));
        var list = archive.Lists[0];

        Assert.Equal("Groceries", list.Name);
        Assert.True(list.IsShared);
        Assert.True(list.IsOwner);
        Assert.Null(list.WellknownName);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 13, 30, 0, TimeSpan.Zero), archive.ExportedAt);
    }

    [Fact]
    public void Inventory_counts_totals_and_per_list()
    {
        var archive = TestData.Archive(
            (TestData.List("A", shared: true), [
                TestData.Task("open with steps", """{ "checklistItems": [ { "displayName": "s" } ], "body": { "content": "n", "contentType": "text" } }"""),
                TestData.Task("done", """{ "status": "completed" }"""),
            ]),
            (TestData.List("B"), [
                TestData.Task("recurring", """{ "recurrence": { "pattern": { "type": "daily", "interval": 1 } }, "isReminderOn": true, "reminderDateTime": { "dateTime": "2026-11-25T08:00:00", "timeZone": "UTC" } }"""),
                TestData.Task("files", """{ "attachments": [ { "name": "a" } ], "linkedResources": [ { "webUrl": "https://x" } ] }"""),
            ]));

        var inventory = Inventory.From(archive);
        Assert.Equal(new InventoryCounts(2, 4, 3, 1, 1, 1, 1, 1, 1, 1, 1), inventory.Totals);
        Assert.Equal(2, inventory.PerList.Count);
        Assert.Equal(new InventoryCounts(1, 2, 1, 1, 0, 1, 1, 0, 0, 0, 1), inventory.PerList[0].Counts);
    }
}

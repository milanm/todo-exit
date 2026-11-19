using System.Net;
using System.Text.Json.Nodes;
using TodoExit.Core.Archive;
using TodoExit.Core.Graph;
using TodoExit.Core.Model;
using Cli = TodoExit.Cli;

namespace TodoExit.Core.Tests;

/// <summary>
/// A list can change between an interrupted read and its resume, or while it is being read. $skip positions then move.
/// A resume reads the unfinished list again from its start, so its result is what a fresh read of that list gives;
/// finished lists are kept. A task id that comes back twice in one read is kept once, with a note.
/// </summary>
public class GraphListChangeTests
{
    private const string Base = FakeGraph.Base;
    private const string Page2 = Base + "me/todo/lists/L1/tasks?$top=100&$skip=100";

    private static GraphTodoReader Reader(FakeGraph graph) =>
        new(new HttpClient(graph), (_, _) => Task.CompletedTask, () => new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));

    private static JsonObject BigTask(int i) => new()
    {
        ["id"] = $"t{i}",
        ["title"] = $"Task {i}",
        ["status"] = i % 11 == 0 ? "completed" : "notStarted",
        ["hasAttachments"] = i % 40 == 0,
    };

    /// <summary>"Big" with 230 tasks (three pages) and "Small" with 5, which is finished before "Big" is started.</summary>
    private static FakeGraph Account(int? poisonedAt = null)
    {
        var graph = new FakeGraph();
        graph.AddList("L0", "Small", 5, i => new JsonObject { ["id"] = $"s{i}", ["title"] = $"Small {i}" });
        var big = graph.AddList("L1", "Big", 230, BigTask);
        for (var i = 0; i < 230; i += 7)
        {
            graph.Steps[$"t{i}"] = new JsonArray((JsonNode)new JsonObject { ["displayName"] = $"step {i}", ["isChecked"] = false });
        }

        foreach (var id in new[] { "t0", "t40", "t80", "t120", "t160", "t200" })
        {
            graph.Attachments[id] = new JsonArray((JsonNode)new JsonObject { ["id"] = $"a-{id}", ["name"] = $"{id}.pdf", ["size"] = 100 });
        }

        if (poisonedAt is { } p)
        {
            big.Poisoned[p] = "early";
        }

        return graph;
    }

    private static FakeGraph.FakeList Big(FakeGraph graph) => graph.AddedLists.Single(l => l.Id == "L1");

    private static JsonObject Normalized(TodoArchiveDocument doc)
    {
        var root = doc.Root.DeepClone().AsObject();
        root.Remove("exportedAt");
        return root;
    }

    private static List<string> Ids(TodoArchiveDocument doc, string listId) =>
        doc.Lists.OfType<JsonObject>().Single(l => (string)l["list"]!["id"]! == listId)["tasks"]!.AsArray()
            .Select(t => (string)t!["id"]!).ToList();

    private static async Task<TodoArchiveDocument> InterruptAsync(FakeGraph graph, string url)
    {
        var fired = false;
        graph.Intercept = (r, _) =>
        {
            if (!fired && r.Url == url)
            {
                fired = true;
                return FakeGraph.Json("""{ "error": { "code": "InvalidAuthenticationToken", "message": "expired" } }""", HttpStatusCode.Unauthorized);
            }

            return null;
        };

        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync());
        graph.Intercept = null;
        return TodoArchiveDocument.Parse(ex.PartialArchive.ToJsonString());
    }

    /// <summary>A fresh read of an account built the same way and changed the same way.</summary>
    private static async Task<TodoArchiveDocument> FreshAsync(Action<FakeGraph> change, int? poisonedAt = null)
    {
        var graph = Account(poisonedAt);
        change(graph);
        return await Reader(graph).ReadAsync();
    }

    [Fact]
    public async Task Resume_after_the_first_task_was_deleted_gives_the_fresh_read()
    {
        // The reviewer's case: the read stops after the first 100 tasks, then task 0 is deleted in To Do. Resuming at
        // $skip=100 would miss t100 (now at position 99) and keep t0; the totals would still match the source.
        var graph = Account();
        var partial = await InterruptAsync(graph, Page2);
        Assert.Equal(100, Ids(partial, "L1").Count);
        Assert.Contains("t0", Ids(partial, "L1"));

        static void Delete(FakeGraph g) => Big(g).Tasks.RemoveAt(0);
        Delete(graph);
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);

        var ids = Ids(resumed, "L1");
        Assert.Equal(229, ids.Count);
        Assert.DoesNotContain("t0", ids);
        Assert.Contains("t100", ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.False(resumed.ReadNotes.Interrupted);
        Assert.Empty(resumed.ReadNotes.Errors);
        Assert.True(JsonNode.DeepEquals(Normalized(await FreshAsync(Delete)), Normalized(resumed)));
    }

    [Fact]
    public async Task Resume_after_a_task_was_added_before_the_checkpoint_gives_the_fresh_read()
    {
        var graph = Account();
        var partial = await InterruptAsync(graph, Page2);

        static void Insert(FakeGraph g) => Big(g).Tasks.Insert(10, new JsonObject { ["id"] = "new", ["title"] = "Added later" });
        Insert(graph);
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);

        var ids = Ids(resumed, "L1");
        Assert.Equal(231, ids.Count);
        Assert.Contains("new", ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Empty(resumed.ReadNotes.Errors);
        Assert.True(JsonNode.DeepEquals(Normalized(await FreshAsync(Insert)), Normalized(resumed)));
    }

    [Fact]
    public async Task Resume_keeps_finished_lists_and_reads_the_unfinished_one_from_its_start()
    {
        var graph = Account();
        var partial = await InterruptAsync(graph, Page2);
        Assert.True(partial.ReadNotes.Progress.Single(p => p.ListId == "L0").IsComplete);

        // A finished list is not read again, even if it changed: the earlier copy is kept as it is.
        graph.AddedLists.Single(l => l.Id == "L0").Tasks.RemoveAt(0);
        var mark = graph.Requests.Count;
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);
        var after = graph.Requests.Skip(mark).ToList();

        Assert.Equal(["s0", "s1", "s2", "s3", "s4"], Ids(resumed, "L0"));
        Assert.DoesNotContain(after, r => r.Url.Contains("/lists/L0/", StringComparison.Ordinal));
        Assert.Equal(Base + "me/todo/lists/L1/tasks?$top=100", after.First(r => r.Url.Contains("/lists/L1/tasks?", StringComparison.Ordinal)).Url);
        Assert.Equal(Enumerable.Range(0, 230).Select(i => $"t{i}"), Ids(resumed, "L1"));
    }

    [Fact]
    public async Task Read_errors_of_the_unfinished_list_are_replaced_by_those_of_the_new_read()
    {
        // The unreadable task is at 120 when its error is recorded, then task 0 is deleted: a fresh read finds it at 119.
        var graph = Account(poisonedAt: 120);
        var partial = await InterruptAsync(graph, Base + "me/todo/lists/L1/tasks?$top=100&$skip=200");
        var before = Assert.Single(partial.ReadNotes.Errors);
        Assert.Equal(120, before.Position);

        static void Delete(FakeGraph g)
        {
            Big(g).Tasks.RemoveAt(0);
            Big(g).Poisoned.Clear();
            Big(g).Poisoned[119] = "early";
        }

        Delete(graph);
        var resumed = await Reader(graph).ReadAsync(resumeFrom: partial);

        var error = Assert.Single(resumed.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.TaskUnreadable, "L1", 119), (error.Kind, error.ListId, error.Position!.Value));
        Assert.Equal(228, Ids(resumed, "L1").Count);
        Assert.True(JsonNode.DeepEquals(Normalized(await FreshAsync(Delete, poisonedAt: 120)), Normalized(resumed)));
    }

    [Fact]
    public async Task Outage_during_the_new_read_interrupts_again_with_a_correct_partial_archive()
    {
        var graph = Account();
        var partial = await InterruptAsync(graph, Page2);
        static void Delete(FakeGraph g) => Big(g).Tasks.RemoveAt(0);
        Delete(graph);

        // The new read stops at its second page: the partial archive holds the 100 tasks it read, not the old ones.
        graph.Intercept = (r, _) => r.Url == Page2 ? throw new HttpRequestException("connection reset") : null;
        var ex = await Assert.ThrowsAsync<GraphReadInterruptedException>(() => Reader(graph).ReadAsync(resumeFrom: partial));
        graph.Intercept = null;
        var second = TodoArchiveDocument.Parse(ex.PartialArchive.ToJsonString());
        Assert.Equal(Enumerable.Range(1, 100).Select(i => $"t{i}"), Ids(second, "L1"));
        Assert.Equal(new ListReadProgress("L1", false, 100, false, false), second.ReadNotes.Progress.Single(p => p.ListId == "L1"));
        Assert.Empty(second.ReadNotes.Errors);

        var resumed = await Reader(graph).ReadAsync(resumeFrom: second);
        Assert.True(JsonNode.DeepEquals(Normalized(await FreshAsync(Delete)), Normalized(resumed)));
    }

    [Fact]
    public async Task Task_returned_twice_in_one_read_is_kept_once_with_a_note()
    {
        // While the read is between pages, a task is added at the top: the next page starts one task earlier and
        // returns t99 again. The added task itself is not seen, which the API gives no way to detect.
        var graph = Account();
        var added = false;
        graph.Intercept = (r, _) =>
        {
            if (!added && r.Url == Page2)
            {
                added = true;
                Big(graph).Tasks.Insert(0, new JsonObject { ["id"] = "top", ["title"] = "Added during the read" });
            }

            return null;
        };

        var doc = await Reader(graph).ReadAsync();

        var ids = Ids(doc, "L1");
        Assert.Equal(230, ids.Count);
        Assert.Equal(Enumerable.Range(0, 230).Select(i => $"t{i}"), ids);
        var note = Assert.Single(doc.ReadNotes.Errors);
        Assert.Equal((ReadErrorKinds.ListChanged, "L1", "Big"), (note.Kind, note.ListId, note.ListName));
        Assert.Contains("Read your tasks again", note.Message);
        Assert.False(doc.ReadNotes.Interrupted);

        var report = Export.ExportService.Export(doc, ArchiveParser.Parse(doc, TimeZoneInfo.Utc), new Export.ExportOptions());
        var md = report.Files.Single(f => f.Path == "report.md").GetText();
        Assert.Contains(ReadErrorKinds.ListChanged, md);
    }

    [Fact]
    public async Task Cli_says_on_resume_that_the_unfinished_list_is_read_again_from_the_start()
    {
        var graph = Account();
        var partial = await InterruptAsync(graph, Page2);

        Assert.Equal(
            "Continuing the read. Lists that were finished are kept. The list \"Big\" was not finished, so it is read again from the start.",
            Cli.Program.ResumeText(partial.ReadNotes));
    }
}

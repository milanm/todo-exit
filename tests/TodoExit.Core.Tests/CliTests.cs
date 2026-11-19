using System.Net;
using System.Text.Json.Nodes;
using TodoExit.Cli;

namespace TodoExit.Core.Tests;

/// <summary>Runs the CLI in-process against FakeGraph. Files go under the test output folder.</summary>
public class CliTests
{
    private static string NewDir() =>
        Path.Combine(AppContext.BaseDirectory, "cli-tests", Guid.NewGuid().ToString("N"));

    private static FakeGraph Account()
    {
        var graph = new FakeGraph();
        graph.AddList("L1", "Work", 45, i => new JsonObject { ["id"] = $"t{i}", ["title"] = $"Task {i}", ["hasAttachments"] = i == 3 });
        graph.Steps["t5"] = new JsonArray((JsonNode)new JsonObject { ["displayName"] = "a step", ["isChecked"] = false });
        graph.Attachments["t3"] = new JsonArray((JsonNode)new JsonObject { ["id"] = "a", ["name"] = "f.pdf", ["size"] = 10 });
        return graph;
    }

    private static async Task<(int Code, string Out, string Err)> Run(FakeGraph graph, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Program.RunAsync(args, stdout, stderr, graph, CancellationToken.None, (_, _) => Task.CompletedTask);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Interrupted_read_saves_partial_archive_exits_3_and_resume_finishes()
    {
        var graph = Account();
        var posts = 0;
        graph.Intercept = (r, _) => r.Method == "POST" && ++posts == 1
            ? FakeGraph.Json("""{ "error": { "code": "InvalidAuthenticationToken", "message": "expired" } }""", HttpStatusCode.Unauthorized)
            : null;

        var outDir = NewDir();
        var (code, _, err) = await Run(graph, "export", "--token", "secret-token", "--out", outDir, "--timezone", "UTC");

        Assert.Equal(Program.ExitInterrupted, code);
        var partialPath = Path.Combine(outDir, "archive.partial.json");
        Assert.True(File.Exists(partialPath));
        Assert.Contains("401", err);
        Assert.Contains($"Saved what was read to {partialPath}. Get a new token and run the same command with --resume \"{partialPath}\".", err);
        Assert.DoesNotContain("secret-token", err);
        Assert.False(File.Exists(Path.Combine(outDir, "archive.json")));

        graph.Intercept = null;
        var mark = graph.Requests.Count;
        (code, var stdout, _) = await Run(graph, "export", "--token", "new-token", "--out", outDir, "--timezone", "UTC", "--resume", partialPath);

        Assert.Equal(Program.ExitOk, code);
        Assert.True(File.Exists(Path.Combine(outDir, "archive.json")));
        Assert.True(File.Exists(partialPath));
        Assert.Contains($"You can remove {partialPath}", stdout);
        Assert.DoesNotContain(graph.Requests.Skip(mark), r => r.Url.Contains("/tasks?"));

        var archive = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "archive.json")))!;
        Assert.Null(archive["readNotes"]);
        Assert.Equal(45, archive["lists"]![0]!["tasks"]!.AsArray().Count);
    }

    [Fact]
    public async Task From_archive_accepts_a_partial_archive_and_reports_it()
    {
        var graph = Account();
        graph.Intercept = (r, _) => r.Method == "POST"
            ? FakeGraph.Json("""{ "error": { "code": "InvalidAuthenticationToken", "message": "expired" } }""", HttpStatusCode.Unauthorized)
            : null;
        var dir = NewDir();
        var (code, _, _) = await Run(graph, "inventory", "--token", "x", "--out", dir);
        Assert.Equal(Program.ExitInterrupted, code);

        var outDir = NewDir();
        (code, _, var err) = await Run(new FakeGraph(), "export", "--from-archive", Path.Combine(dir, "archive.partial.json"), "--out", outDir);

        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("incomplete", err);
        Assert.StartsWith("# todo-exit report\n\n**This archive is incomplete.**", await File.ReadAllTextAsync(Path.Combine(outDir, "report.md")));
    }

    [Fact]
    public async Task Old_skip_completed_steps_flag_is_accepted_as_a_no_op_and_summary_lines_are_printed()
    {
        var graph = Account();
        graph.AddList("L2", "Done list", 3, i => new JsonObject { ["id"] = $"d{i}", ["title"] = $"Done {i}", ["status"] = "completed" });
        var outDir = NewDir();

        var (code, stdout, err) = await Run(graph, "export", "--token", "x", "--out", outDir, "--skip-completed-steps");

        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("--skip-completed-steps is no longer needed", err);
        Assert.Contains("Todoist CSV: 3 completed tasks left out by design, 1 attachment to re-add by hand.", stdout);
        Assert.DoesNotContain("could not be carried over", stdout);
        Assert.DoesNotContain(graph.Requests, r => r.Url.Contains("checklistItems")
            || (r.Body?.ToJsonString().Contains("checklistItems") ?? false));
    }

    [Fact]
    public async Task Verify_steps_flag_reports_the_step_check()
    {
        var graph = Account();
        var (code, _, err) = await Run(graph, "inventory", "--token", "x", "--verify-steps");

        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("Step check: every task without inline steps really has none.", err);
        Assert.Contains(graph.Posts, p => p.Body!.ToJsonString().Contains("checklistItems"));
    }

    [Fact]
    public async Task Resume_needs_a_token_not_an_archive()
    {
        var (code, _, err) = await Run(new FakeGraph(), "export", "--from-archive", "a.json", "--resume", "b.json", "--out", NewDir());
        Assert.Equal(Program.ExitUsage, code);
        Assert.Contains("--resume", err);
    }

    [Fact]
    public async Task Missing_resume_file_is_a_failure_not_a_crash()
    {
        var (code, _, err) = await Run(Account(), "export", "--token", "x", "--resume", Path.Combine(NewDir(), "nope.json"), "--out", NewDir());
        Assert.Equal(Program.ExitFailure, code);
        Assert.Contains("not found", err);
    }
}

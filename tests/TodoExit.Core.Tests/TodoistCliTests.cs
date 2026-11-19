using System.Text.Json.Nodes;
using TodoExit.Cli;

namespace TodoExit.Core.Tests;

/// <summary>Runs "todo-exit transfer" in-process against FakeTodoist.</summary>
public class TodoistCliTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "transfer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteArchive(string dir, (JsonObject, JsonObject[])[]? lists = null)
    {
        var path = Path.Combine(dir, "archive.json");
        File.WriteAllBytes(path, TestData.Document(lists ?? TodoistTransferTests.RichLists()).ToUtf8Bytes());
        return path;
    }

    private static async Task<(int Code, string Out, string Err)> Run(FakeTodoist fake, Func<string, string?>? env, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Program.RunAsync(args, stdout, stderr, fake, CancellationToken.None, (_, _) => Task.CompletedTask,
            clock: () => TodoistTransferTests.Now, environment: env ?? (_ => null));
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static Task<(int Code, string Out, string Err)> Run(FakeTodoist fake, params string[] args) => Run(fake, null, args);

    private static void AssertNoTokenAnywhere(string outDir, params string[] texts)
    {
        foreach (var text in texts)
        {
            Assert.DoesNotContain(FakeTodoist.Token, text);
        }

        foreach (var file in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(FakeTodoist.Token, File.ReadAllText(file));
        }
    }

    [Fact]
    public async Task Without_yes_it_prints_the_plan_and_writes_nothing()
    {
        var dir = NewDir();
        var archive = WriteArchive(dir);
        var outDir = Path.Combine(dir, "out");
        var fake = new FakeTodoist();

        var (code, stdout, err) = await Run(fake, "transfer", "--from-archive", archive, "--out", outDir, "--todoist-token", FakeTodoist.Token,
            "--timezone", "Europe/Belgrade");

        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("Todoist account: tester@example.com (plan: Beginner)", stdout);
        Assert.Contains("Limits: 5 projects, 0 in use, 5 free; 300 active tasks per project; reminders no", stdout);
        Assert.Contains("Work -> \"Work\": 2 tasks, 2 sub-tasks", stdout);
        Assert.Contains("Empty: skipped (nothing to transfer)", stdout);
        Assert.Contains("Home -> \"Home\": 1 tasks, 0 sub-tasks", stdout);
        Assert.Contains("reminder-not-carried: 1.", stdout);
        Assert.Contains("Nothing was written to Todoist. Run the same command with --yes to transfer.", stdout);
        Assert.Empty(fake.Writes);
        Assert.False(Directory.Exists(outDir));
        AssertNoTokenAnywhere(dir, stdout, err);
    }

    [Fact]
    public async Task Offline_plan_needs_no_token_and_sends_nothing()
    {
        var dir = NewDir();
        var fake = new FakeTodoist();
        var (code, stdout, _) = await Run(fake, "transfer", "--from-archive", WriteArchive(dir), "--include-completed");

        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("No Todoist token: account limits and existing projects are not checked.", stdout);
        Assert.Contains("Work -> \"Work\": 2 tasks, 2 sub-tasks, 3 completed", stdout);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Yes_needs_a_token_and_unknown_lists_are_usage_errors()
    {
        var dir = NewDir();
        var archive = WriteArchive(dir);
        var (code, _, err) = await Run(new FakeTodoist(), "transfer", "--from-archive", archive, "--yes");
        Assert.Equal(Program.ExitUsage, code);
        Assert.Contains("--yes needs a Todoist token", err);

        (code, _, err) = await Run(new FakeTodoist(), "transfer", "--from-archive", archive, "--lists", "Home,Nope");
        Assert.Equal(Program.ExitUsage, code);
        Assert.Contains("no list named \"Nope\"", err);

        (code, _, err) = await Run(new FakeTodoist(), "transfer", "--lists", "Home");
        Assert.Equal(Program.ExitUsage, code);
        Assert.Contains("--from-archive", err);
    }

    [Fact]
    public async Task Misspelled_option_never_echoes_the_token()
    {
        var dir = NewDir();
        var archive = WriteArchive(dir);
        var (code, stdout, err) = await Run(new FakeTodoist(), "transfer", "--from-archive", archive, "--todoist-tokn", FakeTodoist.Token);
        Assert.Equal(Program.ExitUsage, code);
        AssertNoTokenAnywhere(dir, stdout, err);

        (code, stdout, err) = await Run(new FakeTodoist(), "transfer", "--from-archive", archive, "--todoist-tokn=" + FakeTodoist.Token);
        Assert.Equal(Program.ExitUsage, code);
        AssertNoTokenAnywhere(dir, stdout, err);
    }

    [Fact]
    public async Task Transfer_with_yes_writes_state_and_reports_and_never_leaks_the_token()
    {
        var dir = NewDir();
        var archive = WriteArchive(dir);
        var outDir = Path.Combine(dir, "out");
        var tokenFile = Path.Combine(dir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, FakeTodoist.Token + "\n");
        var fake = new FakeTodoist();

        var (code, stdout, err) = await Run(fake, "transfer", "--from-archive", archive, "--out", outDir, "--todoist-token-file", tokenFile,
            "--timezone", "Europe/Belgrade", "--yes");

        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("Projects created: 2, tasks: 3, sub-tasks: 2, completed: 0, reminders: 0, errors: 0, skipped: 0, time:", stdout);
        Assert.Contains("The transfer finished.", stdout);
        Assert.Contains("Transferring: ", err);
        Assert.Equal(5, fake.Items.Count);
        Assert.All(fake.Requests, r => Assert.Equal("https://api.todoist.com/api/v1/sync", r.Url));
        Assert.All(fake.Requests, r => Assert.Equal("Bearer " + FakeTodoist.Token, r.Authorization));

        var statePath = Path.Combine(outDir, "todoist-transfer-state.json");
        var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
        Assert.Equal("todo-exit-todoist-transfer", (string?)state["format"]);
        Assert.True((bool)state["finished"]!);
        Assert.Equal("Europe/Belgrade", (string?)state["settings"]!["timeZone"]);
        var md = await File.ReadAllTextAsync(Path.Combine(outDir, "todoist-transfer-report.md"));
        Assert.StartsWith("# todo-exit transfer to Todoist\n\nThe transfer finished.\n\nTodoist account: tester@example.com", md);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "todoist-transfer-report.json")))!;
        Assert.Equal("completed", (string?)json["transfer"]!["status"]);
        Assert.Equal(2, (int)json["transfer"]!["projectsCreated"]!);
        Assert.NotNull(json["losses"]);
        Assert.False(File.Exists(statePath + ".tmp"));
        AssertNoTokenAnywhere(outDir, stdout, err);

        // Running it again: the state file is in the way, and the projects now exist.
        (code, _, err) = await Run(fake, "transfer", "--from-archive", archive, "--out", outDir, "--todoist-token", FakeTodoist.Token, "--yes");
        Assert.Equal(Program.ExitFailure, code);
        Assert.Contains("already has projects named \"Work\", \"Home\"", (await Run(fake, "transfer", "--from-archive", archive, "--todoist-token", FakeTodoist.Token)).Out);
        Assert.Equal(5, fake.Items.Count);
    }

    [Fact]
    public async Task Token_from_environment_and_rejected_token_exits_2()
    {
        var dir = NewDir();
        var archive = WriteArchive(dir);
        var fake = new FakeTodoist();
        var (code, stdout, err) = await Run(fake, name => name == "TODOIST_TOKEN" ? "wrong-token-value" : null, "transfer", "--from-archive", archive);

        Assert.Equal(Program.ExitFailure, code);
        Assert.Contains("Todoist rejected the token (HTTP 401)", err);
        Assert.DoesNotContain("wrong-token-value", stdout + err);
        Assert.Equal("Bearer wrong-token-value", Assert.Single(fake.Requests).Authorization);
    }

    [Fact]
    public async Task Interrupted_transfer_exits_3_and_resume_finishes_without_duplicates()
    {
        var dir = NewDir();
        var archive = WriteArchive(dir);
        var outDir = Path.Combine(dir, "out");
        var fake = new FakeTodoist { ServerErrors = 1000 };

        var (code, _, err) = await Run(fake, "transfer", "--from-archive", archive, "--out", outDir, "--todoist-token", FakeTodoist.Token, "--yes");
        Assert.Equal(Program.ExitInterrupted, code);
        Assert.Contains("did not answer after 6 attempts", err);
        var statePath = Path.Combine(outDir, "todoist-transfer-state.json");
        Assert.Contains($"--resume \"{statePath}\"", err);
        Assert.Contains("interrupted", await File.ReadAllTextAsync(Path.Combine(outDir, "todoist-transfer-report.md")));

        // Without --yes a resume only shows what is left.
        fake.ServerErrors = 0;
        var (planCode, planOut, _) = await Run(fake, "transfer", "--from-archive", archive, "--resume", statePath, "--todoist-token", FakeTodoist.Token,
            "--lists", "Home");
        Assert.Equal(Program.ExitOk, planCode);
        Assert.Contains("Resuming transfer", planOut);
        Assert.Contains("0 of 7 commands done", planOut);
        Assert.Empty(fake.Items);

        (code, var stdout, err) = await Run(fake, "transfer", "--from-archive", archive, "--resume", statePath, "--todoist-token", FakeTodoist.Token, "--yes");
        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("Projects created: 2, tasks: 3, sub-tasks: 2", stdout);
        Assert.Equal(5, fake.Items.Count);
        Assert.Equal(5, fake.Items.Select(i => i.Content).Distinct().Count());
        Assert.Equal(3, fake.Projects.Count);
    }

    [Fact]
    public async Task Resume_refuses_a_different_archive()
    {
        var dir = NewDir();
        var archive = WriteArchive(dir);
        var outDir = Path.Combine(dir, "out");
        var fake = new FakeTodoist { ServerErrors = 1000 };
        await Run(fake, "transfer", "--from-archive", archive, "--out", outDir, "--todoist-token", FakeTodoist.Token, "--yes");

        var other = WriteArchive(NewDir(), [(TestData.List("Other"), [TestData.Task("x")])]);
        var (code, _, err) = await Run(fake, "transfer", "--from-archive", other, "--resume", Path.Combine(outDir, "todoist-transfer-state.json"),
            "--todoist-token", FakeTodoist.Token, "--yes");
        Assert.Equal(Program.ExitFailure, code);
        Assert.Contains("not the archive the transfer was started with", err);
    }

    [Fact]
    public async Task Projects_that_do_not_fit_stop_the_transfer_with_exit_2()
    {
        var dir = NewDir();
        var archive = WriteArchive(dir);
        var fake = new FakeTodoist { MaxProjects = 1 };
        var (code, stdout, err) = await Run(fake, "transfer", "--from-archive", archive, "--out", Path.Combine(dir, "out"),
            "--todoist-token", FakeTodoist.Token, "--yes");

        Assert.Equal(Program.ExitFailure, code);
        Assert.Contains("Problem: This transfer needs 2 new Todoist projects", stdout);
        Assert.Contains("Nothing was written to Todoist", err);
        Assert.Empty(fake.Writes);

        (code, _, _) = await Run(fake, "transfer", "--from-archive", archive, "--out", Path.Combine(dir, "out"),
            "--todoist-token", FakeTodoist.Token, "--lists", "Home", "--yes");
        Assert.Equal(Program.ExitOk, code);
        Assert.Equal(["Milk"], fake.Items.Select(i => i.Content));
    }

    [Fact]
    public async Task Help_lists_the_transfer_command()
    {
        var (code, stdout, _) = await Run(new FakeTodoist(), "transfer", "--help");
        Assert.Equal(Program.ExitOk, code);
        Assert.Contains("todo-exit transfer  --from-archive <file>", stdout);
        Assert.Contains("--todoist-token-file", stdout);
    }
}

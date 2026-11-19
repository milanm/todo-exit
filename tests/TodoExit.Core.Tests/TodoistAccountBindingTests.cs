using System.Text.Json.Nodes;
using TodoExit.Cli;
using TodoExit.Core.Model;
using TodoExit.Core.Todoist;

namespace TodoExit.Core.Tests;

/// <summary>A transfer only ever writes to the Todoist account its plan, or its state, was made for.</summary>
public class TodoistAccountBindingTests
{
    private const string TokenA = "token-of-account-a-1234567890";
    private const string TokenB = "token-of-account-b-0987654321";

    private static (FakeTodoist A, FakeTodoist B, TodoistAccounts Handler) TwoAccounts()
    {
        var a = new FakeTodoist { AcceptedToken = TokenA, UserId = "11111111", Email = "a@example.com", MaxTasks = 1000 };
        var b = new FakeTodoist { AcceptedToken = TokenB, UserId = "22222222", Email = "b@example.com", MaxTasks = 1000 };
        b.Projects.Clear(); // every account has its own Inbox, with its own id
        b.Projects.Add(new FakeTodoist.Project { Id = "inbox-b", Name = "Inbox", Inbox = true });
        return (a, b, new TodoistAccounts(a, b));
    }

    private static TodoArchive Archive(int tasks = 5) =>
        TestData.Archive((TestData.List("Work"),
            Enumerable.Range(0, tasks).Select(i => TestData.Task($"Task {i}", $$"""{ "id": "w{{i}}" }""")).ToArray()));

    private static TodoistClient Client(HttpMessageHandler handler, string token, int maxAttempts = 6) =>
        new(new HttpClient(handler, disposeHandler: false), new TodoistToken(token), (_, _) => Task.CompletedTask) { MaxAttempts = maxAttempts };

    private static async Task<TodoistPlan> PlanFor(HttpMessageHandler handler, string token, TodoArchive archive)
    {
        var account = await Client(handler, token).ReadAccountAsync(CancellationToken.None);
        return TodoistPlanner.Build(archive, new TodoistPlanSettings
        {
            TimeZoneId = "UTC",
            MaxTasksPerProject = 1000,
            PlannedAt = TodoistTransferTests.Now,
        }, account);
    }

    private static Task<TodoistTransferResult> Run(TodoistPlan plan, TodoistTransferState state, TodoistClient client) =>
        TodoistTransfer.RunAsync(plan, state, client, (_, _) => Task.CompletedTask, null, CancellationToken.None);

    [Fact]
    public async Task Account_read_returns_the_user_id_as_a_string()
    {
        var (a, _, handler) = TwoAccounts();
        var account = await Client(handler, TokenA).ReadAccountAsync(CancellationToken.None);
        Assert.Equal("11111111", account.UserId);
        Assert.Equal("a@example.com", account.Email);
        Assert.Single(a.Requests);
    }

    [Fact]
    public async Task Continue_with_the_token_of_another_account_writes_nothing_there()
    {
        // The reviewer's first path: plan for A, the first write is interrupted, the token of B is entered, Continue.
        var (a, b, handler) = TwoAccounts();
        var archive = Archive();
        var plan = await PlanFor(handler, TokenA, archive);
        var state = TodoistTransfer.NewState(plan, "fingerprint", TodoistTransferTests.Now);
        Assert.Equal(("11111111", "a@example.com"), (state.TodoistUserId, state.TodoistEmail));

        a.ServerErrors = 1;
        var first = await Run(plan, state, Client(handler, TokenA, maxAttempts: 1));
        Assert.Equal(TransferStatus.Interrupted, first.Status);
        var before = state.ToUtf8Bytes();

        var wrong = await Run(plan, state, Client(handler, TokenB));

        Assert.Equal(TransferStatus.WrongAccount, wrong.Status);
        Assert.Contains("different Todoist account (b@example.com)", wrong.Message);
        Assert.Contains("(a@example.com)", wrong.Message);
        Assert.DoesNotContain(TokenB, wrong.Message);
        Assert.Empty(b.Writes);
        Assert.Equal("""["user"]""", Assert.Single(b.Requests).Form["resource_types"]);
        Assert.Equal(["Inbox"], b.Projects.Select(p => p.Name));
        Assert.Equal(before, state.ToUtf8Bytes());

        var resumed = await Run(plan, state, Client(handler, TokenA));
        Assert.Equal(TransferStatus.Completed, resumed.Status);
        Assert.Equal(5, a.ItemsIn("Work").Count);
        Assert.Single(a.Projects, p => p.Name == "Work");
    }

    [Fact]
    public async Task First_write_with_a_token_of_another_account_than_the_plan_writes_nothing()
    {
        // The reviewer's second path at the Core level: the plan was read with A, the transfer starts with B.
        var (_, b, handler) = TwoAccounts();
        var plan = await PlanFor(handler, TokenA, Archive());
        var state = TodoistTransfer.NewState(plan, "fingerprint", TodoistTransferTests.Now);

        var result = await Run(plan, state, Client(handler, TokenB));

        Assert.Equal(TransferStatus.WrongAccount, result.Status);
        Assert.Empty(b.Writes);
        Assert.Empty(state.Sent);
    }

    [Fact]
    public async Task Happy_path_adds_one_read_and_no_write()
    {
        var (a, _, handler) = TwoAccounts();
        var plan = await PlanFor(handler, TokenA, Archive(150));
        var mark = a.Requests.Count;
        var state = TodoistTransfer.NewState(plan, "fingerprint", TodoistTransferTests.Now);

        var result = await Run(plan, state, Client(handler, TokenA));

        Assert.Equal(TransferStatus.Completed, result.Status);
        var run = a.Requests.Skip(mark).ToList();
        var reads = run.Where(r => !r.Form.ContainsKey("commands")).ToList();
        Assert.Equal("""["user"]""", Assert.Single(reads).Form["resource_types"]);
        Assert.Equal(2, run.Count(r => r.Form.ContainsKey("commands"))); // 151 commands
        Assert.Same(reads[0], run[0]);
    }

    private static TodoistTransferState OldFormat(TodoistTransferState state)
    {
        var json = JsonNode.Parse(state.ToUtf8Bytes())!.AsObject();
        json.Remove("todoistUserId");
        json.Remove("todoistEmail");
        var old = TodoistTransferState.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Null(old.TodoistUserId);
        return old;
    }

    [Fact]
    public async Task Old_state_without_a_user_id_is_bound_when_its_projects_are_in_the_account()
    {
        var (a, b, handler) = TwoAccounts();
        var archive = Archive(150);
        var plan = await PlanFor(handler, TokenA, archive);
        var state = TodoistTransfer.NewState(plan, "fingerprint", TodoistTransferTests.Now);
        a.LoseResponse.Add(2); // the project and 99 tasks are created, the response to the second request is lost
        var interrupted = await Run(plan, state, Client(handler, TokenA, maxAttempts: 1));
        Assert.Equal(TransferStatus.Interrupted, interrupted.Status);
        Assert.NotEmpty(state.Sent);

        // An old state resumed with B's token, as the CLI does it (the plan is read with the token given): refused.
        var planB = await PlanFor(handler, TokenB, archive);
        b.Requests.Clear();
        var refused = await Run(planB, OldFormat(state), Client(handler, TokenB));
        Assert.Equal(TransferStatus.WrongAccount, refused.Status);
        Assert.Contains("earlier version", refused.Message);
        Assert.Contains("b@example.com", refused.Message);
        Assert.Empty(b.Writes);
        Assert.Equal("""["user","projects"]""", Assert.Single(b.Requests).Form["resource_types"]);

        // The same old state for A: its project is there, so it is bound to A and finishes.
        var old = OldFormat(state);
        var saved = new List<byte[]>();
        var done = await TodoistTransfer.RunAsync(plan, old, Client(handler, TokenA), (s, _) =>
        {
            saved.Add(s.ToUtf8Bytes());
            return Task.CompletedTask;
        }, null, CancellationToken.None);
        Assert.Equal(TransferStatus.Completed, done.Status);
        Assert.Equal("11111111", old.TodoistUserId);
        Assert.Equal("11111111", TodoistTransferState.Parse(saved[0]).TodoistUserId);
        Assert.Equal(150, a.ItemsIn("Work").Count);
    }

    [Fact]
    public async Task Old_state_whose_first_response_was_lost_is_bound_by_the_inbox_it_recorded()
    {
        // The first request may have created the project, but its id is unknown. The Inbox recorded before the
        // transfer started identifies the account.
        var (a, b, handler) = TwoAccounts();
        var plan = await PlanFor(handler, TokenA, Archive());
        var state = TodoistTransfer.NewState(plan, "fingerprint", TodoistTransferTests.Now);
        a.LoseResponse.Add(1);
        Assert.Equal(TransferStatus.Interrupted, (await Run(plan, state, Client(handler, TokenA, maxAttempts: 1))).Status);
        Assert.Empty(state.Ids);

        var refused = await Run(await PlanFor(handler, TokenB, Archive()), OldFormat(state), Client(handler, TokenB));
        Assert.Equal(TransferStatus.WrongAccount, refused.Status);
        Assert.Empty(b.Writes);

        var old = OldFormat(state);
        Assert.Equal(TransferStatus.Completed, (await Run(plan, old, Client(handler, TokenA))).Status);
        Assert.Equal("11111111", old.TodoistUserId);
        Assert.Equal(5, a.ItemsIn("Work").Count);
    }

    [Fact]
    public async Task Old_state_that_never_sent_anything_is_bound_to_the_token_account()
    {
        var (a, _, handler) = TwoAccounts();
        var plan = TodoistPlanner.Build(Archive(), new TodoistPlanSettings { TimeZoneId = "UTC", PlannedAt = TodoistTransferTests.Now }, null);
        var state = OldFormat(TodoistTransfer.NewState(plan, "fingerprint", TodoistTransferTests.Now));

        var result = await Run(plan, state, Client(handler, TokenA));

        Assert.Equal(TransferStatus.Completed, result.Status);
        Assert.Equal("11111111", state.TodoistUserId);
        Assert.Equal(5, a.ItemsIn("Work").Count);
    }

    [Fact]
    public async Task State_round_trips_its_account()
    {
        var state = new TodoistTransferState { TodoistUserId = "11111111", TodoistEmail = "a@example.com" };
        var parsed = TodoistTransferState.Parse(state.ToUtf8Bytes());
        Assert.Equal(("11111111", "a@example.com"), (parsed.TodoistUserId, parsed.TodoistEmail));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Cli_resume_with_the_token_of_another_account_exits_2_and_writes_nothing()
    {
        var (a, b, handler) = TwoAccounts();
        var dir = Path.Combine(AppContext.BaseDirectory, "binding-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var archivePath = Path.Combine(dir, "archive.json");
        await File.WriteAllBytesAsync(archivePath, TestData.Document((TestData.List("Work"),
            Enumerable.Range(0, 5).Select(i => TestData.Task($"Task {i}", $$"""{ "id": "w{{i}}" }""")).ToArray())).ToUtf8Bytes());

        async Task<(int Code, string Err)> Cli(params string[] args)
        {
            var stderr = new StringWriter();
            var code = await Program.RunAsync(args, new StringWriter(), stderr, handler, CancellationToken.None,
                (_, _) => Task.CompletedTask, clock: () => TodoistTransferTests.Now, environment: _ => null);
            return (code, stderr.ToString());
        }

        a.ServerErrors = 6;
        var (code, _) = await Cli("transfer", "--from-archive", archivePath, "--out", dir, "--todoist-token", TokenA, "--yes", "--timezone", "UTC");
        Assert.Equal(Program.ExitInterrupted, code);
        var statePath = Path.Combine(dir, "todoist-transfer-state.json");
        var stateBefore = await File.ReadAllBytesAsync(statePath);

        (code, var err) = await Cli("transfer", "--from-archive", archivePath, "--resume", statePath, "--todoist-token", TokenB, "--yes");

        Assert.Equal(Program.ExitFailure, code);
        Assert.Contains("different Todoist account (b@example.com)", err);
        Assert.Contains("a@example.com", err);
        Assert.DoesNotContain(TokenB, err);
        Assert.Empty(b.Writes);
        Assert.Equal(stateBefore, await File.ReadAllBytesAsync(statePath));

        (code, _) = await Cli("transfer", "--from-archive", archivePath, "--resume", statePath, "--todoist-token", TokenA, "--yes");
        Assert.Equal(Program.ExitOk, code);
        Assert.Equal(5, a.ItemsIn("Work").Count);
    }
}

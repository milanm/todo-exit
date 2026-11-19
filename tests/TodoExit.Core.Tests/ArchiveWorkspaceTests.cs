using TodoExit.Core.Archive;
using TodoExit.Core.Model;
using TodoExit.Core.Todoist;

namespace TodoExit.Core.Tests;

/// <summary>
/// The web page's rules, tested without a browser: the page (Graph read, sample data) and step 5 (plan, transfer)
/// share one <see cref="ArchiveWorkspace"/>, and late answers never land in another session.
/// </summary>
public class ArchiveWorkspaceTests
{
    private const string TokenA = "token-of-account-a-1234567890";
    private const string TokenB = "token-of-account-b-0987654321";

    private sealed class Setup
    {
        public required FakeTodoist A { get; init; }

        public required FakeTodoist B { get; init; }

        public required TodoistAccounts Handler { get; init; }

        public required ArchiveWorkspace Workspace { get; init; }

        /// <summary>When set, requests whose body matches wait until <see cref="Release"/>.</summary>
        public Func<string, bool>? HoldWhen { get; set; }

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static (TodoArchiveDocument, TodoArchive) Archive(string listName = "Work", int tasks = 3)
    {
        var document = TestData.Document((TestData.List(listName),
            Enumerable.Range(0, tasks).Select(i => TestData.Task($"{listName} {i}", $$"""{ "id": "{{listName}}-{{i}}" }""")).ToArray()));
        return (document, ArchiveParser.Parse(document, TimeZoneInfo.Utc));
    }

    private static Setup Create()
    {
        var a = new FakeTodoist { AcceptedToken = TokenA, UserId = "11111111", Email = "a@example.com" };
        var b = new FakeTodoist { AcceptedToken = TokenB, UserId = "22222222", Email = "b@example.com" };
        b.Projects.Clear();
        b.Projects.Add(new FakeTodoist.Project { Id = "inbox-b", Name = "Inbox", Inbox = true });
        var handler = new TodoistAccounts(a, b);
        var workspace = new ArchiveWorkspace(() => new HttpClient(handler, disposeHandler: false), (_, _) => Task.CompletedTask);
        var setup = new Setup { A = a, B = b, Handler = handler, Workspace = workspace };
        handler.Before = async request =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            if (setup.HoldWhen?.Invoke(body) == true)
            {
                setup.HoldWhen = null;
                setup.Held.TrySetResult();
                await setup.Release.Task;
            }
        };
        var (document, archive) = Archive();
        Assert.True(workspace.TrySetArchive(document, archive));
        return setup;
    }

    private static Task<TodoistPlan?> Plan(TodoistTransferSession session, string token) =>
        session.PlanAsync(new TodoistToken(token), null, false, null, CancellationToken.None);

    private static Task<TodoistTransferResult> Run(TodoistTransferSession session, string token, CancellationToken cancellationToken = default) =>
        session.RunAsync(new TodoistToken(token), new Progress<TransferProgress>(), cancellationToken);

    private static bool IsWrite(string body) => body.Contains("commands=", StringComparison.Ordinal);

    [Fact]
    public async Task Token_changed_while_the_plan_is_read_drops_that_plan()
    {
        var s = Create();
        var session = s.Workspace.Session!;
        s.HoldWhen = _ => true;

        var planning = Plan(session, TokenA);
        await s.Held.Task;
        Assert.True(session.IsPlanning);
        Assert.Contains("reading your Todoist account", s.Workspace.ArchiveLockReason);

        session.ClearPlan(); // what step 5 does when the token field or a choice changes
        Assert.False(session.IsPlanning);
        s.Release.SetResult();

        Assert.Null(await planning);
        Assert.Null(session.Plan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(session, TokenB));
        Assert.Empty(s.A.Writes);
        Assert.Empty(s.B.Writes);
    }

    [Fact]
    public async Task Stale_plan_used_with_another_token_writes_nothing_and_can_be_planned_again()
    {
        // If a plan for A were used with B's token anyway, Core refuses before the first write.
        var s = Create();
        var session = s.Workspace.Session!;
        Assert.NotNull(await Plan(session, TokenA));

        var result = await Run(session, TokenB);

        Assert.Equal(TransferStatus.WrongAccount, result.Status);
        Assert.Empty(s.B.Writes);
        Assert.Null(session.State); // nothing was sent, so the plan can be used or shown again
        Assert.Null(session.Result);
        Assert.NotNull(session.Plan);
        Assert.True(s.Workspace.CanReplaceArchive);
    }

    [Fact]
    public async Task Continue_with_the_token_of_another_account_is_refused_and_the_transfer_can_still_finish()
    {
        var s = Create();
        var session = s.Workspace.Session!;
        Assert.NotNull(await Plan(session, TokenA));
        s.A.ServerErrors = 6;
        Assert.Equal(TransferStatus.Interrupted, (await Run(session, TokenA)).Status);
        Assert.True(session.CanContinue);

        var wrong = await Run(session, TokenB);

        Assert.Equal(TransferStatus.WrongAccount, wrong.Status);
        Assert.Contains("b@example.com", wrong.Message);
        Assert.Contains("a@example.com", wrong.Message);
        Assert.Empty(s.B.Writes);
        Assert.True(session.CanContinue);
        Assert.False(s.Workspace.CanReplaceArchive);

        Assert.Equal(TransferStatus.Completed, (await Run(session, TokenA)).Status);
        Assert.Equal(3, s.A.ItemsIn("Work").Count);
        Assert.True(s.Workspace.CanReplaceArchive);
    }

    [Fact]
    public async Task Archive_cannot_be_replaced_while_a_transfer_runs_or_is_unfinished_until_it_is_discarded()
    {
        var s = Create();
        var session = s.Workspace.Session!;
        Assert.NotNull(await Plan(session, TokenA));
        s.HoldWhen = IsWrite;
        using var cts = new CancellationTokenSource();

        var running = Run(session, TokenA, cts.Token);
        await s.Held.Task;

        Assert.True(session.IsRunning);
        Assert.Contains("is running", s.Workspace.ArchiveLockReason);
        Assert.False(s.Workspace.TryBeginGraphRead());
        var (other, otherArchive) = Archive("Other");
        Assert.False(s.Workspace.TrySetArchive(other, otherArchive));
        Assert.Same(session, s.Workspace.Session);
        Assert.Throws<InvalidOperationException>(() => s.Workspace.DiscardTransfer());

        // The write is lost (Todoist kept failing), so the transfer stops unfinished.
        s.A.ServerErrors = 6;
        s.Release.SetResult();
        Assert.Equal(TransferStatus.Interrupted, (await running).Status);

        Assert.True(session.HasUnfinishedTransfer);
        Assert.Contains("not finished", s.Workspace.ArchiveLockReason);
        Assert.False(s.Workspace.TryBeginGraphRead());
        Assert.False(s.Workspace.TrySetArchive(other, otherArchive));
        Assert.NotNull(session.StateFile());

        s.Workspace.DiscardTransfer();

        Assert.True(session.IsDiscarded);
        Assert.NotSame(session, s.Workspace.Session);
        Assert.Null(s.Workspace.Session!.State);
        Assert.True(s.Workspace.CanReplaceArchive);
        Assert.True(s.Workspace.TrySetArchive(other, otherArchive));
        Assert.Equal("Other", s.Workspace.Session.Archive.Lists[0].Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Plan(session, TokenA));
    }

    [Fact]
    public async Task While_Graph_is_read_step_5_cannot_plan_or_transfer()
    {
        var s = Create();
        var session = s.Workspace.Session!;
        Assert.NotNull(await Plan(session, TokenA));

        Assert.True(s.Workspace.TryBeginGraphRead());
        Assert.NotNull(s.Workspace.TodoistLockReason);
        var plan = await Assert.ThrowsAsync<InvalidOperationException>(() => Plan(session, TokenA));
        Assert.Contains("being read from Microsoft", plan.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(session, TokenA));
        Assert.Empty(s.A.Writes);

        // The read finishes with a new archive: a new session, the old plan is gone.
        var (other, otherArchive) = Archive("Other");
        Assert.True(s.Workspace.TrySetArchive(other, otherArchive));
        s.Workspace.EndGraphRead();
        Assert.Null(s.Workspace.TodoistLockReason);
        Assert.Null(s.Workspace.Session!.Plan);
        Assert.True(session.IsDiscarded);
    }

    [Fact]
    public async Task Plan_answer_that_arrives_after_a_discard_lands_nowhere()
    {
        var s = Create();
        var session = s.Workspace.Session!;
        s.HoldWhen = _ => true;

        var planning = Plan(session, TokenA);
        await s.Held.Task;
        s.Workspace.DiscardTransfer();
        var fresh = s.Workspace.Session!;
        s.Release.SetResult();

        Assert.Null(await planning);
        Assert.Null(session.Plan);
        Assert.NotSame(session, fresh);
        Assert.Null(fresh.Plan);
        Assert.False(fresh.IsPlanning);
        Assert.True(s.Workspace.CanReplaceArchive);
    }

    [Fact]
    public async Task Archive_cannot_be_replaced_while_the_account_is_read()
    {
        var s = Create();
        s.HoldWhen = _ => true;
        var planning = Plan(s.Workspace.Session!, TokenA);
        await s.Held.Task;

        var (other, otherArchive) = Archive("Other");
        Assert.False(s.Workspace.TrySetArchive(other, otherArchive));
        Assert.False(s.Workspace.TryBeginGraphRead());

        s.Release.SetResult();
        Assert.NotNull(await planning);
        Assert.True(s.Workspace.TrySetArchive(other, otherArchive));
    }
}

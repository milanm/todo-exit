using System.Diagnostics;
using TodoExit.Core.Archive;
using TodoExit.Core.Export;
using TodoExit.Core.Model;

namespace TodoExit.Core.Todoist;

/// <summary>
/// One archive's way into Todoist from the web page: read the account and build the plan, then run, cancel and
/// continue the transfer, with the state kept in memory. Uses the same Core types as the command-line tool.
/// Holds no token: each call gets it and drops it when it returns.
/// </summary>
/// <remarks>
/// Async calls change only this session. A caller that awaits a call must check afterwards that this is still the
/// session it shows (see <see cref="ArchiveWorkspace.Session"/>); a discarded session never gets a new plan.
/// </remarks>
public sealed class TodoistTransferSession
{
    private readonly TodoArchiveDocument _document;
    private readonly Func<HttpClient> _http;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private readonly Stopwatch _clock = new();
    private int _planVersion;

    /// <param name="delay">Delay between Todoist retries. Tests inject one that does not sleep.</param>
    public TodoistTransferSession(
        TodoArchiveDocument document, TodoArchive archive, Func<HttpClient> http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        Archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _delay = delay;
    }

    public TodoArchive Archive { get; }

    public TodoistPlan? Plan { get; private set; }

    /// <summary>Set when the transfer has started. Kept after an interruption so it can continue.</summary>
    public TodoistTransferState? State { get; private set; }

    public TodoistTransferResult? Result { get; private set; }

    /// <summary>The Todoist account is being read for a plan.</summary>
    public bool IsPlanning { get; private set; }

    /// <summary>A transfer request may be in flight.</summary>
    public bool IsRunning { get; private set; }

    public bool IsBusy => IsPlanning || IsRunning;

    /// <summary>A transfer started and did not finish: its state is needed to continue it without duplicates.</summary>
    public bool HasUnfinishedTransfer => State is { Finished: false };

    public bool CanContinue => HasUnfinishedTransfer && Result is not null && Result.Status != TransferStatus.Completed && !IsBusy;

    public bool Finished => State?.Finished == true;

    /// <summary>The session was dropped (a new archive, or "Discard this transfer"). It cannot plan or run any more.</summary>
    public bool IsDiscarded { get; private set; }

    /// <summary>Why planning and transferring are not allowed right now, or null. Set by <see cref="ArchiveWorkspace"/>.</summary>
    public Func<string?>? BlockedReason { get; init; }

    public override string ToString() => "TodoistTransferSession";

    /// <summary>Lists that have something to transfer with these choices, by name, in archive order.</summary>
    public static IReadOnlyList<string> ListsWithTasks(TodoArchive archive, bool includeCompleted) =>
        archive.Lists
            .Where(l => l.Tasks.Any(t => includeCompleted || !t.IsCompleted))
            .Select(l => l.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Reads the account (read-only) and builds the plan. Returns null when the plan was dropped while it was being
    /// read: <see cref="ClearPlan"/> was called (the token or the choices changed) or the session was discarded.
    /// Throws <see cref="TodoistApiException"/>.
    /// </summary>
    /// <param name="lists">Chosen list names, or null for every list.</param>
    public async Task<TodoistPlan?> PlanAsync(TodoistToken token, IReadOnlyList<string>? lists, bool includeCompleted,
        Action<TimeSpan, string>? waiting, CancellationToken cancellationToken)
    {
        EnsureUsable();
        if (IsRunning || HasUnfinishedTransfer)
        {
            throw new InvalidOperationException("A transfer is in progress.");
        }

        var version = ++_planVersion;
        Plan = null;
        State = null;
        Result = null;
        IsPlanning = true;
        try
        {
            var client = new TodoistClient(_http(), token, _delay) { MaxAttempts = 3, Waiting = waiting };
            var account = await client.ReadAccountAsync(cancellationToken);
            var settings = new TodoistPlanSettings
            {
                IncludeCompleted = includeCompleted,
                Lists = lists,
                TimeZoneId = TimeZoneInfo.Local.Id,
                MaxTasksPerProject = account.MaxTasks is > 0 and var max ? max : TodoistPlanSettings.DefaultMaxTasksPerProject,
                Reminders = account.Reminders,
                PlannedAt = DateTimeOffset.UtcNow,
            };
            var plan = TodoistPlanner.Build(Archive, settings, account);
            if (version != _planVersion || IsDiscarded)
            {
                return null; // the token or the choices changed while the account was read: this plan may be for another account
            }

            Plan = plan;
            return plan;
        }
        finally
        {
            if (version == _planVersion)
            {
                IsPlanning = false;
            }
        }
    }

    /// <summary>
    /// Drops a plan that was not used, and any plan still being read, because what it was built from changed.
    /// Does nothing once a transfer has started: its plan stays.
    /// </summary>
    public void ClearPlan()
    {
        if (State is not null)
        {
            return;
        }

        _planVersion++;
        IsPlanning = false;
        Plan = null;
        Result = null;
    }

    /// <summary>
    /// Starts the transfer of the current plan, or continues an interrupted one. Before writing, Core checks that the
    /// token belongs to the account the plan (or the transfer state) is for.
    /// </summary>
    public async Task<TodoistTransferResult> RunAsync(TodoistToken token, IProgress<TransferProgress> progress, CancellationToken cancellationToken)
    {
        EnsureUsable();
        if (IsBusy)
        {
            throw new InvalidOperationException("The Todoist account is being read or a transfer is running.");
        }

        if (Plan is not { CanStart: true } plan)
        {
            throw new InvalidOperationException("Show a plan without problems first.");
        }

        var created = State is null;
        var state = State ??= TodoistTransfer.NewState(plan, TodoistTransferState.Fingerprint(_document), DateTimeOffset.UtcNow);
        var client = new TodoistClient(_http(), token, _delay);
        IsRunning = true;
        _clock.Start();
        TodoistTransferResult result;
        try
        {
            result = await TodoistTransfer.RunAsync(plan, state, client, (_, _) => Task.CompletedTask, progress, cancellationToken);
        }
        finally
        {
            _clock.Stop();
            IsRunning = false;
        }

        if (result.Status == TransferStatus.WrongAccount && created && state.Sent.Count == 0)
        {
            // Nothing was sent: forget the state so the plan can be shown again for the right account.
            State = null;
            Result = null;
            return result;
        }

        Result = result;
        return result;
    }

    /// <summary>Drops this session and everything it knows about the transfer. Not while a transfer is running.</summary>
    public void Discard()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Cancel the transfer before discarding it.");
        }

        IsDiscarded = true;
        _planVersion++;
        IsPlanning = false;
    }

    public TransferCounts Counts() =>
        Plan is not null && State is not null ? TransferCounts.From(Plan, State) : new TransferCounts(0, 0, 0, 0, 0, 0, 0);

    public (int Done, int Total) CommandProgress() =>
        Plan is not null && State is not null ? TodoistTransfer.CommandProgress(Plan, State) : (0, 0);

    /// <summary>todoist-transfer-report.md and .json, as the command-line tool writes them.</summary>
    public IReadOnlyList<ExportFile> ReportFiles() =>
        Plan is not null && State is not null && Result is not null
            ? TodoistTransferReport.ToFiles(Archive, Plan, State, Result, _clock.Elapsed)
            : [];

    /// <summary>The state file the command-line tool continues with --resume. Holds no token.</summary>
    public byte[]? StateFile() => State?.ToUtf8Bytes();

    private void EnsureUsable()
    {
        if (IsDiscarded)
        {
            throw new InvalidOperationException("This transfer was discarded.");
        }

        if (BlockedReason?.Invoke() is { } why)
        {
            throw new InvalidOperationException(why);
        }
    }
}

/// <summary>
/// What the web page works on: the archive and the Todoist transfer that belongs to it, plus whether Microsoft Graph
/// is being read. The one place that decides which operation may start, shared by the page and step 5.
/// </summary>
/// <remarks>
/// Rules: while Graph is read, step 5 cannot plan or transfer. While a transfer runs, or a started transfer has not
/// finished, the archive cannot be replaced; <see cref="DiscardTransfer"/> is the explicit way out.
/// </remarks>
public sealed class ArchiveWorkspace(Func<HttpClient> http, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public TodoArchiveDocument? Document { get; private set; }

    public TodoArchive? Archive { get; private set; }

    /// <summary>The transfer session for the current archive. A new one replaces it on a new archive or a discard.</summary>
    public TodoistTransferSession? Session { get; private set; }

    public bool GraphReading { get; private set; }

    /// <summary>Why the archive cannot be replaced now (by a new read or the sample data), or null.</summary>
    public string? ArchiveLockReason => Session switch
    {
        { IsRunning: true } => "A Todoist transfer is running in step 5. Wait until it stops, or cancel it, before you read your tasks again or load the sample data.",
        { HasUnfinishedTransfer: true } => "Step 5 has a Todoist transfer that is not finished. Continue it, or discard it in step 5, before you read your tasks again or load the sample data.",
        { IsPlanning: true } => "Step 5 is reading your Todoist account. Wait until it is done.",
        _ => null,
    };

    public bool CanReplaceArchive => !GraphReading && ArchiveLockReason is null;

    /// <summary>Why step 5 cannot plan or transfer now, or null.</summary>
    public string? TodoistLockReason =>
        GraphReading ? "Your tasks are being read from Microsoft in step 1. Step 5 is available again when that is done." : null;

    /// <summary>Marks the start of a Graph read. False when it may not start.</summary>
    public bool TryBeginGraphRead()
    {
        if (!CanReplaceArchive)
        {
            return false;
        }

        GraphReading = true;
        return true;
    }

    public void EndGraphRead() => GraphReading = false;

    /// <summary>Shows a new archive with a fresh transfer session. False (and nothing changes) while the archive is locked.</summary>
    public bool TrySetArchive(TodoArchiveDocument document, TodoArchive archive)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(archive);
        if (ArchiveLockReason is not null)
        {
            return false;
        }

        Session?.Discard();
        Document = document;
        Archive = archive;
        Session = NewSession();
        return true;
    }

    /// <summary>
    /// Forgets the transfer of the current archive, finished or not, and starts a fresh session for the same archive.
    /// What was already created in Todoist stays there. Not while a transfer is running.
    /// </summary>
    public void DiscardTransfer()
    {
        if (Session is null)
        {
            return;
        }

        Session.Discard();
        Session = NewSession();
    }

    private TodoistTransferSession NewSession() =>
        new(Document!, Archive!, http, delay) { BlockedReason = () => TodoistLockReason };
}

using System.Diagnostics;
using TodoExit.Core.Archive;
using TodoExit.Core.Export;
using TodoExit.Core.Model;
using TodoExit.Core.Todoist;

namespace TodoExit.Web.Services;

/// <summary>
/// One archive's way into Todoist from the web page: read the account and build the plan, then run, cancel and
/// continue the transfer, with the state kept in memory. Uses the same Core types as the command-line tool.
/// Holds no token: each call gets it and drops it when it returns.
/// </summary>
public sealed class TodoistTransferSession(TodoArchiveDocument document, TodoArchive archive, Func<HttpClient> http)
{
    private readonly Stopwatch _clock = new();

    public TodoArchive Archive { get; } = archive;

    public TodoistPlan? Plan { get; private set; }

    /// <summary>Set when the transfer has started. Kept after an interruption so it can continue.</summary>
    public TodoistTransferState? State { get; private set; }

    public TodoistTransferResult? Result { get; private set; }

    public bool CanContinue => State is { Finished: false } && Result is not null && Result.Status != TransferStatus.Completed;

    public bool Finished => State?.Finished == true;

    public override string ToString() => "TodoistTransferSession";

    /// <summary>Lists that have something to transfer with these choices, by name, in archive order.</summary>
    public static IReadOnlyList<string> ListsWithTasks(TodoArchive archive, bool includeCompleted) =>
        archive.Lists
            .Where(l => l.Tasks.Any(t => includeCompleted || !t.IsCompleted))
            .Select(l => l.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Reads the account (read-only) and builds the plan. Throws <see cref="TodoistApiException"/>.</summary>
    /// <param name="lists">Chosen list names, or null for every list.</param>
    public async Task<TodoistPlan> PlanAsync(TodoistToken token, IReadOnlyList<string>? lists, bool includeCompleted,
        Action<TimeSpan, string>? waiting, CancellationToken cancellationToken)
    {
        if (State is { Finished: false })
        {
            throw new InvalidOperationException("A transfer is in progress.");
        }

        Plan = null;
        State = null;
        Result = null;
        var client = new TodoistClient(http(), token) { MaxAttempts = 3, Waiting = waiting };
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
        Plan = TodoistPlanner.Build(Archive, settings, account);
        return Plan;
    }

    /// <summary>Starts the transfer of the current plan, or continues an interrupted one.</summary>
    public async Task<TodoistTransferResult> RunAsync(TodoistToken token, IProgress<TransferProgress> progress, CancellationToken cancellationToken)
    {
        if (Plan is not { CanStart: true } plan)
        {
            throw new InvalidOperationException("Show a plan without problems first.");
        }

        State ??= TodoistTransfer.NewState(plan, TodoistTransferState.Fingerprint(document), DateTimeOffset.UtcNow);
        var client = new TodoistClient(http(), token);
        _clock.Start();
        try
        {
            Result = await TodoistTransfer.RunAsync(plan, State, client, (_, _) => Task.CompletedTask, progress, cancellationToken);
        }
        finally
        {
            _clock.Stop();
        }

        return Result;
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
}

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using TodoExit.Core.Todoist;

namespace TodoExit.Web.Pages;

/// <summary>
/// Step 5 of the page. The token lives only in <see cref="_token"/> (bound to the password field) and is cleared
/// when the transfer finishes, on "Forget token", and when the page goes away. It is never written to storage,
/// the URL, logs or a file.
/// </summary>
public sealed partial class SendToTodoist : IDisposable
{
    private TodoistTransferSession? _shownSession;
    private IReadOnlyList<string> _lists = [];
    private readonly HashSet<string> _chosen = new(StringComparer.Ordinal);
    private bool _includeCompleted;
    private bool _confirmed;
    private bool _confirmDiscard;
    private string _token = string.Empty;
    private string? _status;
    private string? _error;
    private CancellationTokenSource? _cts;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>The archive and its transfer session, shared with the page, which decides what may start.</summary>
    [Parameter, EditorRequired]
    public ArchiveWorkspace Workspace { get; set; } = default!;

    [Parameter]
    public bool IsSample { get; set; }

    /// <summary>Raised when planning or a transfer starts or stops, so the page updates what it allows.</summary>
    [Parameter]
    public EventCallback ActivityChanged { get; set; }

    private TodoistTransferSession? Session => Workspace.Session;

    private bool Running => Session?.IsRunning == true;

    private bool Planning => Session?.IsPlanning == true;

    /// <summary>Why step 5 cannot plan or transfer now (Microsoft Graph is being read), or null.</summary>
    private string? Blocked => Workspace.TodoistLockReason;

    /// <summary>An unfinished transfer fixes the choices: its state belongs to this plan.</summary>
    private bool ChoicesLocked => Running || Planning || Blocked is not null || Session?.HasUnfinishedTransfer == true;

    private bool CanShowPlan => !ChoicesLocked && _token.Trim().Length > 0 && _chosen.Count > 0;

    private bool CanTransfer => !Running && !Planning && Blocked is null && _confirmed && _token.Trim().Length > 0
        && Session is { Plan.CanStart: true, State: null };

    private bool CanContinue => Session is { CanContinue: true } && !Running && Blocked is null && _token.Trim().Length > 0;

    protected override void OnParametersSet() => SyncSession();

    /// <summary>A new session (new archive, or a discarded transfer) starts with fresh choices.</summary>
    private void SyncSession()
    {
        if (ReferenceEquals(Session, _shownSession))
        {
            return;
        }

        _shownSession = Session;
        _includeCompleted = false;
        _lists = Session is null ? [] : TodoistTransferSession.ListsWithTasks(Session.Archive, _includeCompleted);
        _chosen.Clear();
        _chosen.UnionWith(_lists);
        _confirmed = false;
        _confirmDiscard = false;
        _status = null;
        _error = null;
    }

    /// <summary>
    /// A plan that was shown but not used is dropped when what it was built from changes. A plan still being read is
    /// dropped too, so its answer cannot show up later for other choices or another token.
    /// </summary>
    private void ResetPlan(string why = "The choices changed")
    {
        if (Session is { State: null } session)
        {
            if (session.Plan is not null || session.IsPlanning)
            {
                _status = why + ", so the plan was cleared. Choose Show plan to see it again before transferring.";
            }

            session.ClearPlan();
        }

        _confirmed = false;
    }

    private void TokenChanged()
    {
        _error = null;
        ResetPlan("The token changed"); // a plan read with another token may be for another account
    }

    private void ForgetToken()
    {
        var before = _status;
        _token = string.Empty;
        TokenChanged();
        var cleared = _status is not null && !ReferenceEquals(_status, before) ? _status + " " : string.Empty;
        _status = cleared + "The token was removed from this page.";
    }

    private void ToggleList(string name, ChangeEventArgs e)
    {
        if (e.Value is true)
        {
            _chosen.Add(name);
        }
        else
        {
            _chosen.Remove(name);
        }

        ResetPlan();
    }

    private void IncludeCompletedChanged()
    {
        if (Session is null)
        {
            return;
        }

        var before = _lists;
        _lists = TodoistTransferSession.ListsWithTasks(Session.Archive, _includeCompleted);

        // Lists that appear are chosen like the others were by default; lists that disappear are dropped.
        _chosen.UnionWith(_lists.Except(before, StringComparer.Ordinal));
        _chosen.IntersectWith(_lists);
        ResetPlan();
    }

    private async Task ShowPlanAsync()
    {
        var session = Session;
        if (!CanShowPlan || session is null)
        {
            return;
        }

        _error = null;
        _status = "Reading your Todoist account. Nothing is written.";
        _confirmed = false;
        try
        {
            // Every offered list chosen: no filter, like the command-line tool without --lists.
            var lists = _chosen.Count == _lists.Count ? null : _lists.Where(_chosen.Contains).ToList();
            var planning = session.PlanAsync(new TodoistToken(_token), lists, _includeCompleted, (wait, reason) =>
            {
                if (ReferenceEquals(session, Session))
                {
                    _status = $"Todoist did not answer ({reason}). Trying again in {Math.Ceiling(wait.TotalSeconds)} seconds.";
                    StateHasChanged();
                }
            }, CancellationToken.None);
            await ActivityChanged.InvokeAsync(); // the page now knows the account is being read
            var plan = await planning;
            if (!ReferenceEquals(session, Session))
            {
                return; // the archive was replaced or the transfer discarded meanwhile: this answer belongs to nothing shown
            }

            if (plan is null)
            {
                _status = "The token or the choices changed while your Todoist account was read, so that plan was dropped. Choose Show plan again.";
                return;
            }

            _status = plan.CanStart
                ? "The plan is ready. Nothing has been written to Todoist."
                : "The plan has problems. Nothing has been written to Todoist.";
        }
        catch (TodoistApiException ex)
        {
            if (ReferenceEquals(session, Session))
            {
                _status = null;
                _error = ex.Failure == TodoistFailure.TokenRejected
                    ? "Todoist did not accept this token. Copy it again from Todoist: Settings, Integrations, Developer. Nothing was written."
                    : ex.Message + " Nothing was written.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            if (ReferenceEquals(session, Session))
            {
                _status = null;
                _error = ex is InvalidOperationException && Blocked is { } why
                    ? why
                    : "Todoist could not be reached. Nothing was written. Check the connection and try again.";
            }
        }
        finally
        {
            await ActivityChanged.InvokeAsync();
        }
    }

    private async Task TransferAsync()
    {
        var session = Session;
        if (session is null || _token.Trim().Length == 0 || session.IsBusy || Blocked is not null)
        {
            return;
        }

        if (session.State is null && !CanTransfer)
        {
            return;
        }

        _error = null;
        _confirmDiscard = false;
        _status = "Starting the transfer...";
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var running = session.RunAsync(new TodoistToken(_token), new ProgressCallback(p =>
            {
                if (ReferenceEquals(session, Session))
                {
                    _status = ProgressText(p);
                    StateHasChanged();
                }
            }), cts.Token);
            await ActivityChanged.InvokeAsync(); // the page now knows a transfer is running
            var result = await running;
            if (!ReferenceEquals(session, Session))
            {
                return;
            }

            switch (result.Status)
            {
                case TransferStatus.Completed:
                    var c = session.Counts();
                    _status = c.Errors + c.Skipped == 0
                        ? "The transfer finished. The token was removed from this page."
                        : "The transfer finished. Todoist did not accept some items; they are listed below. The token was removed from this page.";
                    _token = string.Empty;
                    break;
                case TransferStatus.Interrupted:
                    _status = null;
                    _error = (result.Message == "Cancelled." ? "The transfer was cancelled." : "The transfer stopped: " + result.Message)
                        + " What was sent is recorded in this page. Choose Continue to finish it.";
                    break;
                case TransferStatus.WrongAccount:
                    _status = null;
                    _error = result.Message + (session.State is null
                        ? " To transfer to the account of this token instead, choose Show plan again."
                        : " Enter the token of that account, then choose Continue.");
                    break;
                default:
                    _status = null;
                    _error = "The transfer stopped: " + result.Message + " Fix the problem, then choose Continue.";
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            if (ReferenceEquals(session, Session))
            {
                _status = null;
                _error = "The transfer stopped because of an unexpected error. What was sent is recorded in this page; choose Continue to try again.";
            }
        }
        finally
        {
            _confirmed = false;
            await ActivityChanged.InvokeAsync();
        }
    }

    /// <summary>"Discard this transfer" asks first: what is lost, and that the state can be downloaded before.</summary>
    private void AskDiscard() => _confirmDiscard = true;

    private void KeepTransfer() => _confirmDiscard = false;

    private async Task DiscardAsync()
    {
        if (Session is not { IsRunning: false })
        {
            return;
        }

        Workspace.DiscardTransfer();
        SyncSession();
        _status = "The transfer was discarded. What it already created in Todoist is still there. You can read your tasks again or plan a new transfer.";
        await ActivityChanged.InvokeAsync();
    }

    private void Cancel()
    {
        _status = "Cancelling...";
        _cts?.Cancel();
    }

    private async Task DownloadStateAsync()
    {
        if (Session?.StateFile() is { } bytes)
        {
            await JS.InvokeVoidAsync("todoExit.download", "todoist-transfer-state.json", "application/json", bytes);
        }
    }

    private Task DownloadReportAsync() => DownloadReportFileAsync(TodoistTransferReport.MarkdownFileName, "text/markdown");

    private Task DownloadReportJsonAsync() => DownloadReportFileAsync(TodoistTransferReport.JsonFileName, "application/json");

    private async Task DownloadReportFileAsync(string name, string type)
    {
        if (Session?.ReportFiles().FirstOrDefault(f => f.Path == name) is { } file)
        {
            await JS.InvokeVoidAsync("todoExit.download", file.Path, type, file.Content);
        }
    }

    private string ProgressText(TransferProgress p)
    {
        var list = p.CurrentList is null ? string.Empty : $" (list \"{p.CurrentList}\")";
        return p.Waiting is { } wait
            ? $"Todoist did not answer ({p.WaitingReason}). Trying again in {Math.Ceiling(wait.TotalSeconds)} seconds. {p.CommandsDone} of {p.CommandsTotal} commands done."
            : $"Sending to Todoist: {p.CommandsDone} of {p.CommandsTotal} commands done{list}.";
    }

    private static string PlanName(TodoistAccount? account) =>
        account is null ? "unknown" : account.PlanName ?? (account.IsPremium ? "paid" : "free");

    private static string ProjectNames(ListPlan list) =>
        list.Projects.Count == 1
            ? $"Todoist project \"{list.Projects[0].Name}\""
            : $"{list.Projects.Count} Todoist projects ({string.Join(", ", list.Projects.Select(p => $"\"{p.Name}\""))})";

    private static string ListCounts(ListPlan list)
    {
        var parts = new List<string>
        {
            TodoistPlanText.Count(list.Projects.Sum(p => p.OpenTasks), "task", "tasks"),
            TodoistPlanText.Count(list.Projects.Sum(p => p.OpenSubTasks), "sub-task", "sub-tasks"),
        };
        var done = list.Projects.Sum(p => p.CompletedTasks + p.CompletedSubTasks);
        parts.Add($"{done} completed");
        var reminders = list.Projects.Sum(p => p.Reminders);
        if (reminders > 0)
        {
            parts.Add(TodoistPlanText.Count(reminders, "reminder", "reminders"));
        }

        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        _token = string.Empty;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private sealed class ProgressCallback(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}

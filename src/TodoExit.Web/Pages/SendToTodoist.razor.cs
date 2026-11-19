using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using TodoExit.Core.Archive;
using TodoExit.Core.Model;
using TodoExit.Core.Todoist;
using TodoExit.Web.Services;

namespace TodoExit.Web.Pages;

/// <summary>
/// Step 5 of the page. The token lives only in <see cref="_token"/> (bound to the password field) and is cleared
/// when the transfer finishes, on "Forget token", and when the page goes away. It is never written to storage,
/// the URL, logs or a file.
/// </summary>
public sealed partial class SendToTodoist : IDisposable
{
    private TodoistTransferSession? _session;
    private TodoArchive? _shownArchive;
    private IReadOnlyList<string> _lists = [];
    private readonly HashSet<string> _chosen = new(StringComparer.Ordinal);
    private bool _includeCompleted;
    private bool _confirmed;
    private string _token = string.Empty;
    private string? _status;
    private string? _error;
    private bool _planning;
    private bool _running;
    private CancellationTokenSource? _cts;

    [Inject]
    private IHttpClientFactory HttpFactory { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired]
    public TodoArchiveDocument Document { get; set; } = default!;

    [Parameter, EditorRequired]
    public TodoArchive Archive { get; set; } = default!;

    [Parameter]
    public bool IsSample { get; set; }

    /// <summary>True while the account is read or a transfer runs, so the page does not swap the archive.</summary>
    [Parameter]
    public EventCallback<bool> BusyChanged { get; set; }

    private bool Running => _running;

    /// <summary>An unfinished transfer fixes the choices: its state belongs to this plan.</summary>
    private bool ChoicesLocked => _running || _planning || _session?.State is { Finished: false };

    private bool CanShowPlan => !ChoicesLocked && _token.Trim().Length > 0 && _chosen.Count > 0;

    private bool CanTransfer => !_running && !_planning && _confirmed && _token.Trim().Length > 0
        && _session is { Plan.CanStart: true, State: null };

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(Archive, _shownArchive))
        {
            return;
        }

        _shownArchive = Archive;
        _session = new TodoistTransferSession(Document, Archive, () => HttpFactory.CreateClient(AppSettings.TodoistClientName));
        _includeCompleted = false;
        _lists = TodoistTransferSession.ListsWithTasks(Archive, _includeCompleted);
        _chosen.Clear();
        _chosen.UnionWith(_lists);
        _confirmed = false;
        _status = null;
        _error = null;
    }

    /// <summary>A plan that was shown but not used is dropped when what it was built from changes.</summary>
    private void ResetPlan(string why = "The choices changed")
    {
        if (_session?.Plan is not null && _session.State is null)
        {
            _session = new TodoistTransferSession(Document, Archive, () => HttpFactory.CreateClient(AppSettings.TodoistClientName));
            _status = why + ", so the plan was cleared. Choose Show plan to see it again before transferring.";
        }

        _confirmed = false;
    }

    private void TokenChanged()
    {
        _error = null;
        if (_session?.State is null)
        {
            ResetPlan("The token changed"); // a plan read with another token may be for another account
        }
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
        var before = _lists;
        _lists = TodoistTransferSession.ListsWithTasks(Archive, _includeCompleted);

        // Lists that appear are chosen like the others were by default; lists that disappear are dropped.
        _chosen.UnionWith(_lists.Except(before, StringComparer.Ordinal));
        _chosen.IntersectWith(_lists);
        ResetPlan();
    }

    private async Task ShowPlanAsync()
    {
        if (!CanShowPlan || _session is null)
        {
            return;
        }

        _error = null;
        _status = "Reading your Todoist account. Nothing is written.";
        _planning = true;
        _confirmed = false;
        await BusyChanged.InvokeAsync(true);
        try
        {
            // Every offered list chosen: no filter, like the command-line tool without --lists.
            var lists = _chosen.Count == _lists.Count ? null : _lists.Where(_chosen.Contains).ToList();
            var plan = await _session.PlanAsync(new TodoistToken(_token), lists, _includeCompleted, (wait, reason) =>
            {
                _status = $"Todoist did not answer ({reason}). Trying again in {Math.Ceiling(wait.TotalSeconds)} seconds.";
                StateHasChanged();
            }, CancellationToken.None);
            _status = plan.CanStart
                ? "The plan is ready. Nothing has been written to Todoist."
                : "The plan has problems. Nothing has been written to Todoist.";
        }
        catch (TodoistApiException ex)
        {
            _status = null;
            _error = ex.Failure == TodoistFailure.TokenRejected
                ? "Todoist did not accept this token. Copy it again from Todoist: Settings, Integrations, Developer. Nothing was written."
                : ex.Message + " Nothing was written.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            _status = null;
            _error = "Todoist could not be reached. Nothing was written. Check the connection and try again.";
        }
        finally
        {
            _planning = false;
            await BusyChanged.InvokeAsync(false);
        }
    }

    private async Task TransferAsync()
    {
        if (_session is null || _token.Trim().Length == 0 || _running)
        {
            return;
        }

        if (_session.State is null && !CanTransfer)
        {
            return;
        }

        _error = null;
        _running = true;
        _status = "Starting the transfer...";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        await BusyChanged.InvokeAsync(true);
        try
        {
            var result = await _session.RunAsync(new TodoistToken(_token), new ProgressCallback(p =>
            {
                _status = ProgressText(p);
                StateHasChanged();
            }), _cts.Token);

            switch (result.Status)
            {
                case TransferStatus.Completed:
                    var c = _session.Counts();
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
                default:
                    _status = null;
                    _error = "The transfer stopped: " + result.Message + " Fix the problem, then choose Continue.";
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            _status = null;
            _error = "The transfer stopped because of an unexpected error. What was sent is recorded in this page; choose Continue to try again.";
        }
        finally
        {
            _running = false;
            _confirmed = false;
            await BusyChanged.InvokeAsync(false);
        }
    }

    private void Cancel()
    {
        _status = "Cancelling...";
        _cts?.Cancel();
    }

    private async Task DownloadStateAsync()
    {
        if (_session?.StateFile() is { } bytes)
        {
            await JS.InvokeVoidAsync("todoExit.download", "todoist-transfer-state.json", "application/json", bytes);
        }
    }

    private Task DownloadReportAsync() => DownloadReportFileAsync(TodoistTransferReport.MarkdownFileName, "text/markdown");

    private Task DownloadReportJsonAsync() => DownloadReportFileAsync(TodoistTransferReport.JsonFileName, "application/json");

    private async Task DownloadReportFileAsync(string name, string type)
    {
        if (_session?.ReportFiles().FirstOrDefault(f => f.Path == name) is { } file)
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

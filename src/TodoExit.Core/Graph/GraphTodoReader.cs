using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TodoExit.Core.Archive;
using TodoExit.Core.Model;

namespace TodoExit.Core.Graph;

public enum ReadPhase
{
    Lists,
    Tasks,
    Steps,
    Attachments,
}

/// <summary>
/// Progress of a Graph read. Done/Total count tasks in the Steps and Attachments phases for the current list.
/// </summary>
public sealed record ReadProgress(
    ReadPhase Phase,
    int ListsDone,
    int ListsTotal,
    int TasksRead,
    string? CurrentList,
    int Done = 0,
    int Total = 0,
    int WaitingSeconds = 0)
{
    /// <summary>True while the reader waits because Graph asked it to slow down (429/503).</summary>
    public bool IsWaiting => WaitingSeconds > 0;
}

/// <summary>
/// The read stopped before it finished: a 401, exhausted retries, an unexpected error or cancellation.
/// <see cref="PartialArchive"/> holds everything read so far and can be passed back to
/// <see cref="GraphTodoReader.ReadAsync"/> to resume. The original exception is the inner exception.
/// </summary>
public sealed class GraphReadInterruptedException : Exception
{
    public GraphReadInterruptedException(string reason, TodoArchiveDocument partialArchive, Exception inner)
        : base(reason, inner)
    {
        Reason = reason;
        PartialArchive = partialArchive;
    }

    public string Reason { get; }

    public TodoArchiveDocument PartialArchive { get; }

    /// <summary>True when the read stopped because the caller cancelled it.</summary>
    public bool WasCancelled => InnerException is OperationCanceledException;
}

/// <summary>A Graph request failed. The message carries the status code and Graph's error text, never a token.</summary>
public sealed class GraphRequestException : Exception
{
    private static readonly string[] DeniedCodes =
        ["accessDenied", "ErrorAccessDenied", "Forbidden", "Authorization_RequestDenied", "ErrorInsufficientPermissionsInAccessToken"];

    public GraphRequestException(
        string message, HttpStatusCode? statusCode = null, string? graphErrorCode = null, Exception? inner = null, bool isOutage = false)
        : base(message, inner)
    {
        StatusCode = statusCode;
        GraphErrorCode = graphErrorCode;
        IsOutage = isOutage;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Graph or the network was not available, so the failure says nothing about the data: a network error or timeout,
    /// or 429, 502, 503 or 504, still failing after every retry. A read that meets an outage stops with a checkpoint
    /// and never records the data it was reading as unreadable.
    /// </summary>
    public bool IsOutage { get; }

    /// <summary>
    /// An error status that may come from one record Graph cannot serialize as well as from the service:
    /// 500 and other 5xx statuses that are not an outage. Only a 2xx or 4xx answer for other records tells them apart.
    /// </summary>
    internal bool IsAmbiguousServerError => !IsOutage && StatusCode is { } s && (int)s >= 500;

    public string? GraphErrorCode { get; }

    /// <summary>
    /// The token itself is the problem: a 401 with InvalidAuthenticationToken, or a 401 without a Graph error.
    /// Always fatal.
    /// </summary>
    public bool IsTokenProblem => IsTokenProblemFor(StatusCode, GraphErrorCode);

    /// <summary>The token is fine but not allowed to read this resource (401 or 403 with accessDenied and similar).</summary>
    public bool IsAccessDenied => IsAccessDeniedFor(StatusCode, GraphErrorCode);

    public static bool IsTokenProblemFor(HttpStatusCode? status, string? code) =>
        status == HttpStatusCode.Unauthorized && !IsAccessDeniedFor(status, code);

    public static bool IsAccessDeniedFor(HttpStatusCode? status, string? code) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        && code is not null
        && (DeniedCodes.Contains(code, StringComparer.OrdinalIgnoreCase)
            || code.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || (status == HttpStatusCode.Forbidden && !code.Contains("token", StringComparison.OrdinalIgnoreCase)));

    /// <summary>"401 Unauthorized: InvalidAuthenticationToken - Access token has expired." (code and message when known).</summary>
    internal static string Describe(HttpStatusCode? status, string? code, string? message)
    {
        var text = status is { } s ? $"{(int)s} {s}" : "error";
        if (code is not null || message is not null)
        {
            text += $": {code}{(code is not null && message is not null ? " - " : string.Empty)}{message}";
        }

        return text;
    }
}

/// <summary>
/// Reads every To Do list, task, checklist item and attachment metadata entry through Microsoft Graph
/// and returns them as a lossless archive. The <see cref="HttpClient"/> must already add authorization;
/// this class never sees a token. All requests are read-only: GETs, plus POSTs to Graph's $batch endpoint
/// whose sub-requests are GETs.
/// </summary>
/// <remarks>
/// Facts measured against a live account that shape this class:
/// Graph accepts $expand=checklistItems on the tasks collection but ignores it, so steps are always read
/// per task, through $batch. Explicit $top/$skip paging works. A corrupt task can make every page that contains
/// it return 200 with truncated JSON; such pages are bisected down to the single task, whose broken body is parsed
/// leniently to keep every property Graph sent (see <see cref="BrokenTaskParser"/>). Graph rejects $select on
/// To Do tasks, so no request uses it.
/// </remarks>
public sealed class GraphTodoReader
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0/";
    public const int MaxRetries = 5;
    public const int PageSize = 100;
    public const int BatchSize = 20;
    /// <summary>
    /// One batch at a time. To Do is served by Outlook, whose documented limits are 4 concurrent requests and
    /// 10,000 requests per 10 minutes per app and mailbox. Graph sends up to 4 sub-requests of an unordered
    /// batch to the service at once, so a single batch already uses the whole concurrency allowance; parallel
    /// batches get 429 (MailboxConcurrency). Nothing else is sent while a batch runs.
    /// </summary>
    public const int MaxBatchesInFlight = 1;

    /// <summary>Batch size after a throttled sub-request, until <see cref="CleanBatchesToGrow"/> clean batches in a row.</summary>
    public const int ReducedBatchSize = 4;

    public const int CleanBatchesToGrow = 5;

    /// <summary>Retries for 429/503, separate from <see cref="MaxRetries"/> for network errors and other 5xx.</summary>
    public const int MaxThrottleRounds = 10;

    /// <summary>Longest single wait after a 429/503.</summary>
    public static readonly TimeSpan MaxThrottleDelay = TimeSpan.FromMinutes(5);

    /// <summary>After this many windows in a row yield no task at all, the reader stops reading that list.</summary>
    internal const int MaxDeadWindows = 2;

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _errorsLock = new();
    private List<ReadError> _errors = new();
    private bool _batchUnavailable;
    private bool _attachmentsDenied;
    private bool _attachmentsSucceeded;
    private IProgress<ReadProgress>? _progress;
    private ReadProgress _lastProgress = new(ReadPhase.Lists, 0, 0, 0, null);

    /// <param name="http">An HttpClient already configured with authentication.</param>
    /// <param name="delay">Delay function used for retries. Tests inject one that does not sleep.</param>
    /// <param name="clock">Clock for the archive's exportedAt value.</param>
    public GraphTodoReader(
        HttpClient http,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _delay = delay ?? Task.Delay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Steps come inline with the task pages: Graph includes "checklistItems" on every task that has steps and omits
    /// it on tasks that have none, so by default no step requests are sent. When true, tasks without inline steps are
    /// checked through /checklistItems (in batches); anything found is merged and counted in
    /// <see cref="ReadNotes.StepsFoundByVerification"/>.
    /// </summary>
    public bool VerifySteps { get; init; }

    /// <summary>
    /// How steps were read in the last run: "inline" (from the task pages only), or with <see cref="VerifySteps"/>
    /// "batch" or "individual" (batching unavailable) for the verification requests.
    /// </summary>
    public string StepReadMode { get; private set; } = "inline";

    /// <summary>With <see cref="VerifySteps"/>: tasks without inline steps that turned out to have steps, in the last run.</summary>
    public int StepsFoundByVerification { get; private set; }

    /// <summary>
    /// Reads everything. When <paramref name="resumeFrom"/> is a partial archive from an earlier interrupted read,
    /// work that is already done is not requested again.
    /// </summary>
    /// <exception cref="GraphReadInterruptedException">
    /// The read stopped early, including on cancellation. The exception carries the partial archive.
    /// </exception>
    public async Task<TodoArchiveDocument> ReadAsync(
        IProgress<ReadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TodoArchiveDocument? resumeFrom = null)
    {
        _batchUnavailable = false;
        _attachmentsDenied = false;
        _attachmentsSucceeded = false;
        StepReadMode = "inline";
        StepsFoundByVerification = 0;
        _progress = progress;
        progress = progress is null ? null : new ProgressRelay(this);
        BatchSizesSent.Clear();

        var run = RunState.From(resumeFrom);
        lock (_errorsLock)
        {
            _errors = run.InitialErrors;
        }

        try
        {
            await ReadAllAsync(run, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var reason = ex switch
            {
                OperationCanceledException => "The read was cancelled.",
                GraphRequestException g => g.Message,
                _ => $"Unexpected error: {ex.GetType().Name}: {ex.Message}",
            };
            throw new GraphReadInterruptedException(reason, BuildArchive(run, reason), ex);
        }

        return BuildArchive(run, null);
    }

    private async Task ReadAllAsync(RunState run, IProgress<ReadProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ReadProgress(ReadPhase.Lists, 0, 0, run.TasksRead, null));
        List<JsonObject>? fetched;
        try
        {
            fetched = await GetCollectionAsync(BaseUrl + "me/todo/lists", cancellationToken).ConfigureAwait(false);
        }
        catch (GraphRequestException ex) when (ex.IsAccessDenied)
        {
            throw WithPermissionHint(ex);
        }

        var lists = fetched ?? throw new GraphRequestException("Graph returned invalid JSON for the list of task lists, twice.");

        run.ListCollection = ToArray(lists.Select(l => l.DeepClone().AsObject()));

        for (var i = 0; i < lists.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = lists[i];
            var listId = GetString(list, "id") ?? throw new GraphRequestException("Graph returned a list without an id.");
            var state = run.Find(listId);
            if (state is null)
            {
                state = new ListState(listId, list);
                run.Lists.Add(state);
            }
            else
            {
                state.List = list; // merged by id: the fresh list object replaces the archived one
            }

            var context = new ListContext(listId, GetString(list, "displayName"), i, lists.Count);
            if (state.IsComplete)
            {
                if (run.AttachmentsDeniedBefore && !_attachmentsDenied)
                {
                    // The earlier run was refused attachments; try once more with this run's token.
                    await ReadAttachmentsAsync(context, state.Tasks, run.TasksRead, progress, cancellationToken, retryDenied: true).ConfigureAwait(false);
                }

                continue;
            }

            if (!state.TasksComplete)
            {
                progress?.Report(new ReadProgress(ReadPhase.Tasks, i, lists.Count, run.TasksRead, context.Name));
                try
                {
                    await ReadTasksAsync(context, state, run, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (GraphRequestException ex) when (ex.IsAccessDenied)
                {
                    throw WithPermissionHint(ex);
                }

                state.TasksComplete = true;
            }

            if (!state.StepsComplete)
            {
                await ReadStepsAsync(context, state.Tasks, run.TasksRead, progress, cancellationToken).ConfigureAwait(false);
                state.StepsComplete = true;
            }

            if (!state.AttachmentsComplete || (run.AttachmentsDeniedBefore && !_attachmentsDenied))
            {
                await ReadAttachmentsAsync(context, state.Tasks, run.TasksRead, progress, cancellationToken,
                    retryDenied: run.AttachmentsDeniedBefore).ConfigureAwait(false);
                state.AttachmentsComplete = true;
            }

            progress?.Report(new ReadProgress(ReadPhase.Attachments, i + 1, lists.Count, run.TasksRead, context.Name));
        }
    }

    /// <summary>
    /// Builds an archive from the current state. Safe to call at any moment: everything is copied, so the
    /// state is not changed. A non-null <paramref name="interruptedReason"/> makes it a partial archive.
    /// </summary>
    private TodoArchiveDocument BuildArchive(RunState run, string? interruptedReason)
    {
        var lists = run.Lists
            .Select(l => new ArchivedList(l.List.DeepClone().AsObject(), l.Tasks.Select(t => t.DeepClone().AsObject()).ToList()))
            .ToList();

        List<ReadError> errors;
        lock (_errorsLock)
        {
            errors = _errors.ToList();
        }

        var denied = _attachmentsDenied || (run.AttachmentsDeniedBefore && !_attachmentsSucceeded);
        if (!denied)
        {
            errors.RemoveAll(e => e.Kind == ReadErrorKinds.AttachmentsDenied);
        }

        var interrupted = interruptedReason is not null;
        var notes = new ReadNotes(errors)
        {
            StepsFoundByVerification = run.StepsFoundBefore + StepsFoundByVerification,
            AttachmentsDenied = denied,
            Interrupted = interrupted,
            InterruptedReason = interruptedReason,
            Progress = interrupted
                ? run.Lists.Select(l => new ListReadProgress(l.Id, l.TasksComplete, l.NextSkip, l.StepsComplete, l.AttachmentsComplete)).ToList()
                : [],
            PendingLists = interrupted ? run.ListCollection ?? run.PendingListsBefore : null,
        };

        return TodoArchiveDocument.Create(_clock(), lists, notes);
    }

    /// <summary>One list being read. Tasks are detached JSON objects that are changed in place as work completes.</summary>
    private sealed class ListState(string id, JsonObject list)
    {
        public string Id { get; } = id;

        public JsonObject List { get; set; } = list;

        public List<JsonObject> Tasks { get; } = new();

        public bool TasksComplete { get; set; }

        public int NextSkip { get; set; }

        public bool StepsComplete { get; set; }

        public bool AttachmentsComplete { get; set; }

        public bool IsComplete => TasksComplete && StepsComplete && AttachmentsComplete;
    }

    private sealed class RunState
    {
        public List<ListState> Lists { get; } = new();

        public List<ReadError> InitialErrors { get; private init; } = new();

        public int StepsFoundBefore { get; private init; }

        public JsonArray? PendingListsBefore { get; private init; }

        /// <summary>The archive being resumed recorded that Graph refused to list attachments.</summary>
        public bool AttachmentsDeniedBefore { get; private init; }

        /// <summary>The list collection fetched in this run (raw copies).</summary>
        public JsonArray? ListCollection { get; set; }

        public int TasksRead => Lists.Sum(l => l.Tasks.Count);

        public ListState? Find(string id) => Lists.FirstOrDefault(l => l.Id == id);

        public static RunState From(TodoArchiveDocument? resume)
        {
            if (resume is null)
            {
                return new RunState();
            }

            // Work on a copy so the caller's document is not changed.
            var root = resume.Root.DeepClone().AsObject();
            var notes = ReadNotes.FromJson(root[ReadNotes.PropertyName]);
            var progress = new Dictionary<string, ListReadProgress>();
            foreach (var p in notes.Progress)
            {
                progress.TryAdd(p.ListId, p);
            }

            var run = new RunState
            {
                InitialErrors = notes.Errors.ToList(),
                StepsFoundBefore = notes.StepsFoundByVerification,
                PendingListsBefore = notes.PendingLists,
                AttachmentsDeniedBefore = notes.AttachmentsDenied,
            };

            if (root["lists"] is JsonArray entries)
            {
                foreach (var entry in entries.OfType<JsonObject>())
                {
                    if (entry["list"] is not JsonObject list || GetString(list, "id") is not { } id || run.Find(id) is not null)
                    {
                        continue;
                    }

                    entry.Remove("list");
                    var state = new ListState(id, list);
                    if (entry["tasks"] is JsonArray tasks)
                    {
                        var items = tasks.OfType<JsonObject>().ToList();
                        tasks.Clear();
                        state.Tasks.AddRange(items);
                    }

                    // Lists without a progress entry were complete when the archive was written.
                    var p = progress.GetValueOrDefault(id);
                    state.TasksComplete = p?.TasksComplete ?? true;
                    state.NextSkip = p?.NextSkip ?? state.Tasks.Count;
                    state.StepsComplete = p?.StepsComplete ?? true;
                    state.AttachmentsComplete = p?.AttachmentsComplete ?? true;
                    run.Lists.Add(state);
                }
            }

            return run;
        }
    }

    private sealed record ListContext(string Id, string? Name, int Index, int Total)
    {
        public string TasksUrl => $"{BaseUrl}me/todo/lists/{Uri.EscapeDataString(Id)}/tasks";

        public string TaskUrl(string taskId) => $"{TasksUrl}/{Uri.EscapeDataString(taskId)}";

        public string TaskRelativeUrl(string taskId) =>
            $"/me/todo/lists/{Uri.EscapeDataString(Id)}/tasks/{Uri.EscapeDataString(taskId)}";
    }

    // ------------------------------------------------------------------ tasks

    private static string PageUrl(ListContext list, int skip, int top)
    {
        // Never add $select: Graph answers 400 for any $select on To Do tasks that names title or status,
        // and ignores the others.
        var url = $"{list.TasksUrl}?$top={top.ToString(CultureInfo.InvariantCulture)}";
        if (skip > 0)
        {
            url += $"&$skip={skip.ToString(CultureInfo.InvariantCulture)}";
        }

        return url;
    }

    /// <summary>
    /// Reads all tasks with explicit $top/$skip windows. @odata.nextLink is only the signal that more pages exist.
    /// A window that cannot be read (invalid JSON twice, or an error status other than 401, access denied or an
    /// outage) is bisected, so every readable task is still read. An outage is never bisected: it stops the read with
    /// the checkpoint at the start of the window.
    /// </summary>
    private async Task ReadTasksAsync(
        ListContext list, ListState state, RunState run, IProgress<ReadProgress>? progress, CancellationToken cancellationToken)
    {
        var deadWindows = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var skip = state.NextSkip;
            var result = await TryGetPageAsync(PageUrl(list, skip, PageSize), cancellationToken).ConfigureAwait(false);
            if (result.Page is { } page)
            {
                var values = TakeValues(page);
                var next = GetString(page, "@odata.nextLink");
                if (next is not null)
                {
                    EnsureTrustedNextLink(next);
                }

                // Tasks and the paging position change together, so a partial archive never holds half a window.
                state.Tasks.AddRange(values);
                state.NextSkip = skip + values.Count;
                progress?.Report(new ReadProgress(ReadPhase.Tasks, list.Index, list.Total, run.TasksRead, list.Name));
                if (next is null || values.Count == 0)
                {
                    break;
                }

                deadWindows = 0;
                continue;
            }

            // Broken window: read what can be read, then continue with the next window.
            // If this was the last window, the next request returns an empty page without a nextLink.
            // Bisection collects tasks and errors separately, so an interruption leaves nothing of the window behind.
            var window = new WindowRead(skip, PageSize) { Responded = result.RawBody is not null, LastError = result.Error };
            await BisectAsync(list, skip, PageSize, result, window, cancellationToken).ConfigureAwait(false);
            if (!window.Responded)
            {
                throw WindowOutage(list, window);
            }

            state.Tasks.AddRange(window.Tasks);
            foreach (var error in window.Errors)
            {
                AddError(error);
            }

            state.NextSkip = skip + PageSize;
            progress?.Report(new ReadProgress(ReadPhase.Tasks, list.Index, list.Total, run.TasksRead, list.Name));

            var dead = window.Tasks.Count == 0 && window.Errors.Count > 0;
            deadWindows = dead ? deadWindows + 1 : 0;
            if (deadWindows >= MaxDeadWindows)
            {
                // No task came out of the last windows: stop instead of probing forever. The list is not complete,
                // so a resumed read continues after these windows.
                throw new GraphRequestException(
                    $"Microsoft Graph returned no readable task in {MaxDeadWindows * PageSize} positions in a row in the list '{list.Name}', " +
                    $"up to position {skip + PageSize - 1}. The read stopped there; the list is not complete.");
            }
        }
    }

    /// <summary>What bisecting one window produced. Committed to the list only when the whole window is done.</summary>
    private sealed class WindowRead(int skip, int top)
    {
        public int Skip { get; } = skip;

        public int Top { get; } = top;

        public List<JsonObject> Tasks { get; } = new();

        public List<ReadError> Errors { get; } = new();

        /// <summary>Some request for this window got a 2xx answer, even if its body was not valid JSON.</summary>
        public bool Responded { get; set; }

        /// <summary>A single-task window failed with an error status while nothing in the window had answered yet.</summary>
        public bool LeafFailedBeforeAnyAnswer { get; set; }

        public GraphRequestException? LastError { get; set; }
    }

    /// <summary>No request inside a window got an answer: Graph is not available, whatever status it sent.</summary>
    private static GraphRequestException WindowOutage(ListContext list, WindowRead window)
    {
        var last = window.LastError;
        return new GraphRequestException(
            $"Microsoft Graph answered no request for positions {window.Skip} to {window.Skip + window.Top - 1} of the list '{list.Name}', " +
            "so the read stopped there and nothing in that range was recorded as unreadable." +
            (last is null ? string.Empty : $" Last error: {last.Message}"),
            last?.StatusCode, last?.GraphErrorCode, last, isOutage: true);
    }

    /// <param name="failed">The failed result for [skip, skip+top). Its raw body is used to salvage a single task.</param>
    /// <remarks>
    /// An error status can come from one record or from the service. Once a single-task window has failed with an error
    /// status before anything in the window answered, the next such failure without an answer in between ends the
    /// window as an outage. This bounds a persistent 500 to about log2(PageSize) + 2 requests.
    /// </remarks>
    private async Task BisectAsync(
        ListContext list, int skip, int top, PageResult failed, WindowRead window, CancellationToken cancellationToken)
    {
        if (top <= 1)
        {
            if (!window.Responded && failed.RawBody is null)
            {
                window.LeafFailedBeforeAnyAnswer = true;
            }

            Salvage(list, skip, failed, window);
            return;
        }

        var half = top / 2;
        foreach (var (s, t) in new[] { (skip, half), (skip + half, top - half) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await TryGetPageAsync(PageUrl(list, s, t), cancellationToken).ConfigureAwait(false);
            if (result.Page is { } page)
            {
                window.Responded = true;
                var values = TakeValues(page);
                window.Tasks.AddRange(values);
                if (values.Count < t && GetString(page, "@odata.nextLink") is null)
                {
                    return; // past the end of the list
                }
            }
            else
            {
                if (result.RawBody is not null)
                {
                    window.Responded = true;
                }
                else
                {
                    window.LastError = result.Error ?? window.LastError;
                    if (!window.Responded && window.LeafFailedBeforeAnyAnswer)
                    {
                        throw WindowOutage(list, window);
                    }
                }

                await BisectAsync(list, s, t, result, window, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A single task whose page could not be read. If Graph sent a broken body, keep every property it managed
    /// to send. Without an id the task is recorded as unreadable.
    /// </summary>
    private static void Salvage(ListContext list, int position, PageResult failed, WindowRead window)
    {
        if (failed.RawBody is { } raw)
        {
            BrokenTaskParser.Result salvaged;
            try
            {
                salvaged = BrokenTaskParser.Parse(raw);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                salvaged = new BrokenTaskParser.Result(null, null, null);
            }

            var task = salvaged.Task;
            if (task is null || task.Count == 0)
            {
                // Nothing at all could be read from the body.
            }
            else if (GetString(task, "id") is { } id)
            {
                task[ReadNotes.PartialMarker] = true;
                window.Tasks.Add(task);
                window.Errors.Add(new ReadError(ReadErrorKinds.TaskPartial, list.Id, list.Name, id, GetString(task, "title"), position,
                    DescribeBreak(salvaged) + " The task is kept with the properties Graph sent before that."));
                return;
            }
            else
            {
                window.Errors.Add(new ReadError(ReadErrorKinds.TaskUnreadable, list.Id, list.Name, null, GetString(task, "title"), position,
                    DescribeBreak(salvaged) + " The task id was not among the properties Graph sent, so the task is not in the archive."));
                return;
            }

            window.Errors.Add(new ReadError(ReadErrorKinds.TaskUnreadable, list.Id, list.Name, null, null, position,
                "Graph returned broken data for this task and nothing could be recovered. The task is not in the archive."));
            return;
        }

        var reason = failed.Error is { } error ? error.Message : "Graph returned no usable data for this task.";
        window.Errors.Add(new ReadError(ReadErrorKinds.TaskUnreadable, list.Id, list.Name, null, null, position,
            reason + " The task is not in the archive."));
    }

    private static string DescribeBreak(BrokenTaskParser.Result salvaged)
    {
        var code = salvaged.ErrorCode is null ? string.Empty : $" ({salvaged.ErrorCode})";
        return salvaged.FailedProperty is { } property
            ? $"Graph failed while writing '{property}' for this task{code}."
            : $"Graph returned broken data for this task{code}.";
    }

    // ------------------------------------------------------------------ steps and attachments

    /// <summary>A per-task collection read through $batch: steps (checklistItems) or attachment metadata.</summary>
    private sealed record SubCollection(string Segment, string ErrorKind, string Noun, ReadPhase Phase);

    private static readonly SubCollection StepsCollection =
        new("checklistItems", ReadErrorKinds.StepsUnreadable, "steps", ReadPhase.Steps);

    private static readonly SubCollection AttachmentsCollection =
        new("attachments", ReadErrorKinds.AttachmentsUnreadable, "attachment list", ReadPhase.Attachments);

    private async Task ReadStepsAsync(
        ListContext list, List<JsonObject> tasks, int tasksRead, IProgress<ReadProgress>? progress, CancellationToken cancellationToken)
    {
        var pending = new List<JsonObject>();
        foreach (var task in tasks)
        {
            if (task["checklistItems"] is JsonArray)
            {
                continue; // already read (earlier run), or Graph may start honoring $expand one day
            }

            // No inline array means the task has no steps (measured on a live account). That does not hold for a task
            // salvaged from a broken body: the body was cut before "checklistItems", so its steps are always requested.
            if (GetString(task, "id") is null || (!VerifySteps && !IsPartial(task)))
            {
                task["checklistItems"] = new JsonArray();
                continue;
            }

            pending.Add(task);
        }

        if (pending.Count == 0)
        {
            return;
        }

        await ReadSubCollectionAsync(list, StepsCollection, pending, tasksRead, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="retryDenied">
    /// The archive being resumed was refused attachments: tasks marked hasAttachments with an empty array are tried again.
    /// </param>
    private async Task ReadAttachmentsAsync(
        ListContext list, List<JsonObject> tasks, int tasksRead, IProgress<ReadProgress>? progress, CancellationToken cancellationToken,
        bool retryDenied = false)
    {
        var pending = new List<JsonObject>();
        foreach (var task in tasks)
        {
            var hasAttachments = GetString(task, "id") is not null
                && task["hasAttachments"] is JsonValue hv && hv.TryGetValue<bool>(out var has) && has;
            if (task["attachments"] is JsonArray existing)
            {
                if (retryDenied && hasAttachments && existing.Count == 0)
                {
                    pending.Add(task);
                }

                continue;
            }

            if (hasAttachments)
            {
                pending.Add(task);
            }
            else
            {
                task["attachments"] = new JsonArray();
            }
        }

        if (_attachmentsDenied)
        {
            // Graph already refused attachments in this run: do not ask again.
            foreach (var task in pending)
            {
                task["attachments"] ??= new JsonArray();
            }

            return;
        }

        await ReadSubCollectionAsync(list, AttachmentsCollection, pending, tasksRead, progress, cancellationToken).ConfigureAwait(false);
        if (_attachmentsDenied)
        {
            foreach (var task in pending)
            {
                task["attachments"] ??= new JsonArray();
            }
        }
    }

    /// <summary>Graph refused to list attachments: stop asking for the rest of the run and record it once.</summary>
    private void MarkAttachmentsDenied(string described)
    {
        _attachmentsDenied = true;
        lock (_errorsLock)
        {
            if (_errors.Any(e => e.Kind == ReadErrorKinds.AttachmentsDenied))
            {
                return;
            }

            _errors.Add(new ReadError(ReadErrorKinds.AttachmentsDenied, null, null, null, null, null,
                $"Microsoft Graph refused to list attachments with the read-only Tasks.Read permission ({described}). " +
                "Attachment names are not in the archive; tasks that have attachments are still marked."));
        }
    }

    private async Task ReadSubCollectionAsync(
        ListContext list, SubCollection kind, List<JsonObject> pending, int tasksRead,
        IProgress<ReadProgress>? progress, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var total = pending.Count;
        var done = 0;
        void Report() => progress?.Report(new ReadProgress(kind.Phase, list.Index, list.Total, tasksRead, list.Name, done, total));
        void MarkDone(int n)
        {
            done += n;
            Report();
        }

        Report();

        // StepReadMode describes the --verify-steps requests; steps of salvaged tasks are read without changing it.
        var isSteps = kind == StepsCollection && VerifySteps;
        var individual = new List<JsonObject>();
        var answered = false;
        if (_batchUnavailable)
        {
            individual.AddRange(pending);
        }
        else
        {
            if (isSteps)
            {
                StepReadMode = "batch";
            }

            var batches = await RunBatchesAsync(list, kind, pending, MarkDone, cancellationToken).ConfigureAwait(false);
            individual.AddRange(batches.Individual);
            answered = batches.Answered;
        }

        if (individual.Count == 0)
        {
            return;
        }

        if (isSteps && (StepReadMode == "inline" || _batchUnavailable))
        {
            StepReadMode = "individual";
        }

        // A 500 for one task can come from that task or from the service. It is recorded as unreadable only after
        // Graph has answered a request for another task in this pass; until then the task waits for a second try.
        var deferred = new List<JsonObject>();
        GraphRequestException? lastAmbiguous = null;
        foreach (var task in individual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await ReadOneAsync(list, kind, task, recordAmbiguous: answered, cancellationToken).ConfigureAwait(false);
            if (outcome.Denied)
            {
                break;
            }

            if (outcome.Deferred is { } ambiguous)
            {
                lastAmbiguous = ambiguous;
                deferred.Add(task);
                continue;
            }

            answered |= outcome.Answered;
            MarkDone(1);
        }

        if (deferred.Count == 0 || (kind == AttachmentsCollection && _attachmentsDenied))
        {
            return;
        }

        if (!answered)
        {
            throw new GraphRequestException(
                $"Microsoft Graph answered no request for the {kind.Noun} in the list '{list.Name}' (last error: {lastAmbiguous!.Message}) " +
                "The read stopped there; nothing was recorded as unreadable.",
                lastAmbiguous.StatusCode, lastAmbiguous.GraphErrorCode, lastAmbiguous, isOutage: true);
        }

        foreach (var task in deferred)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await ReadOneAsync(list, kind, task, recordAmbiguous: true, cancellationToken).ConfigureAwait(false);
            if (outcome.Denied)
            {
                break;
            }

            MarkDone(1);
        }
    }

    /// <param name="Answered">Graph answered for this task (2xx, or a 4xx about it).</param>
    /// <param name="Deferred">The request failed with an ambiguous 5xx and was not recorded.</param>
    /// <param name="Denied">Graph refused attachments; stop asking.</param>
    private sealed record OneOutcome(bool Answered, GraphRequestException? Deferred = null, bool Denied = false);

    /// <summary>
    /// Reads one task's collection with individual GETs. An outage and a token problem are thrown; access denied for
    /// attachments stops the attachment requests. Other errors are recorded for the task, except an ambiguous 5xx
    /// when <paramref name="recordAmbiguous"/> is false.
    /// </summary>
    private async Task<OneOutcome> ReadOneAsync(
        ListContext list, SubCollection kind, JsonObject task, bool recordAmbiguous, CancellationToken cancellationToken)
    {
        var isAttachments = kind == AttachmentsCollection;
        if (isAttachments && _attachmentsDenied)
        {
            return new OneOutcome(false, Denied: true);
        }

        var url = list.TaskUrl(GetString(task, "id")!) + "/" + kind.Segment;
        List<JsonObject>? items;
        try
        {
            items = await GetCollectionAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphRequestException ex) when (isAttachments && ex.IsAccessDenied)
        {
            MarkAttachmentsDenied(Short(ex.StatusCode, ex.GraphErrorCode));
            return new OneOutcome(false, Denied: true);
        }
        catch (GraphRequestException ex) when (!ex.IsTokenProblem && !ex.IsOutage)
        {
            if (ex.IsAmbiguousServerError && !recordAmbiguous)
            {
                return new OneOutcome(false, Deferred: ex);
            }

            AddKindError(list, kind, task, ex.Message);
            task[kind.Segment] = new JsonArray();
            return new OneOutcome(!ex.IsAmbiguousServerError);
        }

        if (items is null)
        {
            AddKindError(list, kind, task, $"Graph returned invalid JSON for the {kind.Noun} of this task.");
            task[kind.Segment] = new JsonArray();
            return new OneOutcome(true);
        }

        if (isAttachments)
        {
            _attachmentsSucceeded = true;
        }

        Store(task, kind, ToArray(items));
        return new OneOutcome(true);
    }

    /// <summary>
    /// Stores a collection on a task. Attachment content is never requested; drop it defensively if present.
    /// Steps stored here come from verification (tasks without inline steps), so any non-empty result is counted,
    /// except for salvaged tasks, which never had a chance to carry their steps inline.
    /// </summary>
    private void Store(JsonObject task, SubCollection kind, JsonArray items)
    {
        if (kind == StepsCollection && items.Count > 0 && !IsPartial(task))
        {
            StepsFoundByVerification++;
        }

        foreach (var item in items.OfType<JsonObject>())
        {
            item.Remove("contentBytes");
        }

        task[kind.Segment] = items;
    }

    private static bool IsPartial(JsonObject task) =>
        task[ReadNotes.PartialMarker] is JsonValue v && v.TryGetValue<bool>(out var partial) && partial;

    /// <param name="Retry">Sub-requests to send again: 429/503, 5xx, or no sub-response at all.</param>
    /// <param name="ServerErrors">The part of <paramref name="Retry"/> that failed with 500, 502 or 504.</param>
    /// <param name="Answered">Sub-requests Graph answered (2xx, or a 4xx about that task).</param>
    private sealed record BatchRoundOutcome(
        List<JsonObject> Retry, HashSet<JsonObject> ServerErrors, List<JsonObject> Individual, TimeSpan? RetryAfter, int Answered);

    private static readonly BatchRoundOutcome Stopped = new([], new(ReferenceEqualityComparer.Instance), [], null, 0);

    private sealed record BatchesOutcome(List<JsonObject> Individual, bool Answered);

    /// <summary>
    /// Sends the requests one batch at a time. Throttled sub-requests go back to the front of the queue and are
    /// retried in a new batch after the longest Retry-After (or the backoff, whichever is longer). After a throttle the
    /// batch size drops to <see cref="ReducedBatchSize"/> and returns to <see cref="BatchSize"/> after
    /// <see cref="CleanBatchesToGrow"/> clean batches. Returns the tasks that need individual GETs.
    /// </summary>
    /// <remarks>
    /// Batches are awaited one after another, so at most <see cref="MaxBatchesInFlight"/> (1) is ever in flight.
    /// A sub-request that is still throttled after <see cref="MaxThrottleRounds"/> rounds (or still fails with 500, 502
    /// or 504 after <see cref="MaxRetries"/> retries) is recorded as unreadable only when the failure is specific to
    /// that task: in the latest batch that held it together with other tasks, Graph answered at least one of the others.
    /// Otherwise it is an outage and the read stops with a checkpoint.
    /// </remarks>
    private async Task<BatchesOutcome> RunBatchesAsync(
        ListContext list, SubCollection kind, List<JsonObject> tasks, Action<int> markDone, CancellationToken cancellationToken)
    {
        var individual = new List<JsonObject>();
        var queue = new LinkedList<JsonObject>(tasks);
        var throttleCounts = new Dictionary<JsonObject, int>(ReferenceEqualityComparer.Instance);
        var serverErrorCounts = new Dictionary<JsonObject, int>(ReferenceEqualityComparer.Instance);
        // Per failing task: in the latest batch that held it and other tasks, did Graph answer any of the others?
        var othersAnswered = new Dictionary<JsonObject, bool>(ReferenceEqualityComparer.Instance);
        var size = BatchSize;
        var cleanStreak = 0;
        var anyAnswered = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_batchUnavailable)
            {
                individual.AddRange(queue);
                break;
            }

            var chunk = new JsonObject[Math.Min(size, queue.Count)];
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = queue.First!.Value;
                queue.RemoveFirst();
            }

            BatchSizesSent.Add(chunk.Length);
            var outcome = await RunBatchAsync(list, kind, chunk, markDone, cancellationToken).ConfigureAwait(false);
            individual.AddRange(outcome.Individual);
            anyAnswered |= outcome.Answered > 0;
            if (kind == AttachmentsCollection && _attachmentsDenied)
            {
                break; // Graph refused attachments; the caller leaves the remaining arrays empty
            }

            if (outcome.Retry.Count == 0)
            {
                cleanStreak++;
                if (size < BatchSize && cleanStreak >= CleanBatchesToGrow)
                {
                    size = BatchSize;
                }

                continue;
            }

            size = ReducedBatchSize;
            cleanStreak = 0;
            var round = 0;
            var retry = new List<JsonObject>();
            foreach (var task in outcome.Retry)
            {
                if (outcome.Answered > 0 || chunk.Length > 1)
                {
                    othersAnswered[task] = outcome.Answered > 0;
                }

                var serverError = outcome.ServerErrors.Contains(task);
                var counts = serverError ? serverErrorCounts : throttleCounts;
                var count = counts.GetValueOrDefault(task) + 1;
                counts[task] = count;
                round = Math.Max(round, count);
                var limit = serverError ? MaxRetries + 1 : MaxThrottleRounds;
                if (count < limit)
                {
                    retry.Add(task);
                    continue;
                }

                var what = serverError
                    ? $"Graph kept failing the {kind.Noun} request with a server error after {MaxRetries} retries."
                    : $"Graph kept throttling the {kind.Noun} request after {MaxThrottleRounds} attempts.";
                if (!othersAnswered.GetValueOrDefault(task))
                {
                    throw new GraphRequestException(
                        $"{what} Other requests were not answered either, so Microsoft Graph is not available (list '{list.Name}'). " +
                        "The read stopped there; nothing was recorded as unreadable. Try again later.",
                        isOutage: true);
                }

                task[kind.Segment] = new JsonArray();
                AddKindError(list, kind, task, what);
                markDone(1);
            }

            // Throttled items go first, in their original order, so they are retried in the next batch.
            for (var i = retry.Count - 1; i >= 0; i--)
            {
                queue.AddFirst(retry[i]);
            }

            if (queue.Count > 0)
            {
                await WaitForThrottleAsync(ThrottleDelay(outcome.RetryAfter, round - 1), cancellationToken).ConfigureAwait(false);
            }
        }

        return new BatchesOutcome(individual, anyAnswered);
    }

    /// <summary>Sizes of the batches sent in the last run (steps and attachments), in order. For tests and diagnostics.</summary>
    internal List<int> BatchSizesSent { get; } = new();

    private async Task<BatchRoundOutcome> RunBatchAsync(
        ListContext list, SubCollection kind, JsonObject[] chunk, Action<int> markDone, CancellationToken cancellationToken)
    {
        var requests = new JsonArray();
        for (var i = 0; i < chunk.Length; i++)
        {
            requests.Add((JsonNode)new JsonObject
            {
                ["id"] = i.ToString(CultureInfo.InvariantCulture),
                ["method"] = "GET",
                ["url"] = list.TaskRelativeUrl(GetString(chunk[i], "id")!) + "/" + kind.Segment,
            });
        }

        var payload = new JsonObject { ["requests"] = requests }.ToJsonString();

        JsonObject? response;
        try
        {
            response = await SendBatchAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed)
        {
            // $batch is not available: use individual GETs for the rest of the run.
            _batchUnavailable = true;
            return new BatchRoundOutcome([], new(ReferenceEqualityComparer.Instance), chunk.ToList(), null, 0);
        }
        catch (GraphRequestException ex) when (kind == AttachmentsCollection && ex.IsAccessDenied)
        {
            MarkAttachmentsDenied(Short(ex.StatusCode, ex.GraphErrorCode));
            return Stopped;
        }

        if (response?["responses"] is not JsonArray responses)
        {
            return new BatchRoundOutcome([], new(ReferenceEqualityComparer.Instance), chunk.ToList(), null, 0);
        }

        var retry = new List<JsonObject>();
        var serverErrors = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        var handled = new bool[chunk.Length];
        TimeSpan? retryAfter = null;
        var answered = 0;

        foreach (var sub in responses.OfType<JsonObject>())
        {
            if (!int.TryParse(GetString(sub, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= chunk.Length || handled[index])
            {
                continue;
            }

            handled[index] = true;
            var task = chunk[index];
            var status = sub["status"] is JsonValue sv && sv.TryGetValue<int>(out var st) ? st : 0;
            var body = sub["body"] as JsonObject;

            if (status is 429 or 503 or 500 or 502 or 504)
            {
                // Graph does not retry sub-requests itself, so a passing 500, 502 or 504 is retried here like a throttle.
                retry.Add(task);
                if (status is 500 or 502 or 504)
                {
                    serverErrors.Add(task);
                }

                var delay = SubResponseRetryAfter(sub);
                if (delay is { } d && (retryAfter is null || d > retryAfter))
                {
                    retryAfter = d;
                }

                continue;
            }

            if (status is 401 or 403)
            {
                var (code, message) = ParseGraphError(body);
                var httpStatus = (HttpStatusCode)status;
                if (GraphRequestException.IsTokenProblemFor(httpStatus, code))
                {
                    throw ErrorFor(httpStatus, code, message, BaseUrl + "$batch");
                }

                if (kind == AttachmentsCollection && GraphRequestException.IsAccessDeniedFor(httpStatus, code))
                {
                    MarkAttachmentsDenied(Short(httpStatus, code));
                    continue; // remaining sub-responses: successes are kept, denials need no further record
                }
            }

            answered++;
            if (status is >= 200 and < 300)
            {
                if (kind == AttachmentsCollection)
                {
                    _attachmentsSucceeded = true;
                }

                var items = body is null ? new List<JsonObject>() : TakeValues(body);
                if (body is not null && GetString(body, "@odata.nextLink") is { } next)
                {
                    try
                    {
                        EnsureTrustedNextLink(next);
                        var more = await GetCollectionAsync(next, cancellationToken).ConfigureAwait(false);
                        if (more is null)
                        {
                            AddKindError(list, kind, task, $"Graph returned invalid JSON for a later page of the {kind.Noun}; some entries may be missing.");
                        }
                        else
                        {
                            items.AddRange(more);
                        }
                    }
                    catch (GraphRequestException ex) when (!ex.IsTokenProblem && !ex.IsOutage)
                    {
                        // The first page of this task's collection was answered, so the failure belongs to this task.
                        AddKindError(list, kind, task, ex.Message);
                    }
                }

                Store(task, kind, ToArray(items));
            }
            else
            {
                task[kind.Segment] = new JsonArray();
                var (code, message) = ParseGraphError(body);
                var text = status == 404
                    ? $"Graph returned 404 Not Found for the {kind.Noun} of this task."
                    : $"Graph returned {status} for the {kind.Noun} of this task.";
                if (code is not null || message is not null)
                {
                    text += $" {code}{(code is not null && message is not null ? " - " : string.Empty)}{message}";
                }

                AddKindError(list, kind, task, text);
            }

            markDone(1);
        }

        if (kind == AttachmentsCollection && _attachmentsDenied)
        {
            return Stopped;
        }

        // Sub-responses that never came back are retried like throttled ones.
        for (var i = 0; i < chunk.Length; i++)
        {
            if (!handled[i])
            {
                retry.Add(chunk[i]);
            }
        }

        // Keep the batch order so retries are deterministic, whatever order Graph answered in.
        retry.Sort((a, b) => Array.IndexOf(chunk, a).CompareTo(Array.IndexOf(chunk, b)));
        return new BatchRoundOutcome(retry, serverErrors, [], retryAfter, answered);
    }

    private static TimeSpan? SubResponseRetryAfter(JsonObject sub)
    {
        if (sub["headers"] is not JsonObject headers)
        {
            return null;
        }

        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Retry-After", StringComparison.OrdinalIgnoreCase)
                && value is JsonValue v
                && (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    || v.TryGetValue<double>(out seconds)))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ HTTP

    /// <summary>GET a collection, following validated next links. Returns null when a page stays invalid JSON.</summary>
    private async Task<List<JsonObject>?> GetCollectionAsync(string url, CancellationToken cancellationToken)
    {
        var items = new List<JsonObject>();
        string? next = url;
        while (next is not null)
        {
            var page = await GetPageAsync(next, cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                return null;
            }

            items.AddRange(TakeValues(page));
            next = GetString(page, "@odata.nextLink");
            if (next is not null)
            {
                EnsureTrustedNextLink(next);
            }
        }

        return items;
    }

    /// <summary>Outcome of reading one page: a parsed page, or the raw invalid body and/or the error.</summary>
    private sealed record PageResult(JsonObject? Page, string? RawBody, GraphRequestException? Error);

    /// <summary>
    /// GET one page without throwing for Graph errors other than 401, access denied and an outage
    /// (<see cref="GraphRequestException.IsOutage"/>), which are thrown. A body that is not valid JSON is retried once.
    /// When the retry fails with an error status, the raw body of the first attempt is kept.
    /// </summary>
    private async Task<PageResult> TryGetPageAsync(string url, CancellationToken cancellationToken)
    {
        string? raw = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string body;
            try
            {
                body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), url, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphRequestException ex) when (!ex.IsTokenProblem && !ex.IsAccessDenied && !ex.IsOutage)
            {
                return new PageResult(null, raw, ex);
            }

            if (TryParseObject(body) is { } obj)
            {
                return new PageResult(obj, null, null);
            }

            raw = body;
        }

        return new PageResult(null, raw, null);
    }

    /// <summary>GET one page. Throws for Graph errors; returns null when the body stays invalid JSON.</summary>
    private async Task<JsonObject?> GetPageAsync(string url, CancellationToken cancellationToken)
    {
        var result = await TryGetPageAsync(url, cancellationToken).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw error;
        }

        return result.Page;
    }

    private async Task<JsonObject?> SendBatchAsync(string payload, CancellationToken cancellationToken)
    {
        const string url = BaseUrl + "$batch";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var body = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") },
                url,
                cancellationToken).ConfigureAwait(false);
            if (TryParseObject(body) is { } obj)
            {
                return obj;
            }
        }

        return null;
    }

    private static JsonObject? TryParseObject(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends a request with bounded retries for 429, 5xx, network failures and timeouts. Returns the body of a
    /// successful response. Throws <see cref="GraphRequestException"/> otherwise; messages never contain the token.
    /// </summary>
    private async Task<string> SendAsync(Func<HttpRequestMessage> createRequest, string url, CancellationToken cancellationToken)
    {
        var throttles = 0;
        var failures = 0;
        (string? Code, string? Message) lastError = (null, null);
        while (true)
        {
            HttpStatusCode? status = null;
            Exception? failure = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var request = createRequest();
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                status = response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }

                var (errorCode, message) = ParseGraphError(TryParseObject(await SafeReadAsync(response, cancellationToken).ConfigureAwait(false)));
                lastError = (errorCode, message);
                var code = (int)status.Value;
                if (status != HttpStatusCode.TooManyRequests && code < 500)
                {
                    throw ErrorFor(status.Value, errorCode, message, url);
                }

                retryAfter = RetryAfterOf(response);
            }
            catch (GraphRequestException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // Network failure, or an HttpClient timeout (not the caller's cancellation).
                failure = ex;
                status = null;
            }

            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                // Patient policy: Graph asked us to slow down.
                if (throttles >= MaxThrottleRounds)
                {
                    throw new GraphRequestException(
                        $"Microsoft Graph kept throttling the request ({GraphRequestException.Describe(status, lastError.Code, lastError.Message)}) after {MaxThrottleRounds} attempts: {DescribeUrl(url)}. Try again later.",
                        status,
                        lastError.Code,
                        isOutage: true);
                }

                await WaitForThrottleAsync(ThrottleDelay(retryAfter, throttles), cancellationToken).ConfigureAwait(false);
                throttles++;
                continue;
            }

            if (failures >= MaxRetries)
            {
                var reason = failure is not null
                    ? $"a network error ({failure.Message})"
                    : GraphRequestException.Describe(status, lastError.Code, lastError.Message);
                throw new GraphRequestException(
                    $"Microsoft Graph request kept failing with {reason} after {MaxRetries} retries: {DescribeUrl(url)}. Try again later.",
                    status,
                    failure is null ? lastError.Code : null,
                    inner: failure,
                    isOutage: failure is not null || status is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout);
            }

            await _delay(Clamp(Backoff(failures)), cancellationToken).ConfigureAwait(false);
            failures++;
        }
    }

    /// <summary>Builds the exception for a non-retryable status, classifying 401/403 by Graph's error code.</summary>
    internal static GraphRequestException ErrorFor(HttpStatusCode status, string? code, string? message, string url)
    {
        var described = GraphRequestException.Describe(status, code, message);
        if (GraphRequestException.IsTokenProblemFor(status, code))
        {
            return new GraphRequestException(
                $"Microsoft Graph rejected the access token ({described}). The token is missing, invalid or expired. Get a new token and run the command again.",
                status, code);
        }

        if (GraphRequestException.IsAccessDeniedFor(status, code))
        {
            return new GraphRequestException($"Microsoft Graph denied access ({described}) for {DescribeUrl(url)}.", status, code);
        }

        return new GraphRequestException($"Microsoft Graph request failed with {described} for {DescribeUrl(url)}", status, code);
    }

    /// <summary>"401 accessDenied" style text for the attachments-denied note.</summary>
    private static string Short(HttpStatusCode? status, string? code) =>
        $"{(status is { } s ? ((int)s).ToString(CultureInfo.InvariantCulture) : "error")}{(code is null ? string.Empty : " " + code)}";

    /// <summary>Adds the Tasks.Read hint to an access-denied error on a list or task collection. Fatal.</summary>
    private static GraphRequestException WithPermissionHint(GraphRequestException ex) =>
        new($"{ex.Message} Access to your task lists was denied; the token needs the Tasks.Read permission.", ex.StatusCode, ex.GraphErrorCode, ex);

    /// <summary>max(Retry-After, backoff), capped at <see cref="MaxThrottleDelay"/>.</summary>
    internal static TimeSpan ThrottleDelay(TimeSpan? retryAfter, int attempt)
    {
        var backoff = Backoff(Math.Max(attempt, 0));
        var value = retryAfter is { } r && r > backoff ? r : backoff;
        return value > MaxThrottleDelay ? MaxThrottleDelay : value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    /// <summary>Waits after a 429/503 and reports the wait, so a long throttle does not look like a hang.</summary>
    private async Task WaitForThrottleAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));
        var before = _lastProgress;
        _progress?.Report(before with { WaitingSeconds = seconds });
        await _delay(delay, cancellationToken).ConfigureAwait(false);
        _progress?.Report(before with { WaitingSeconds = 0 });
    }

    /// <summary>Remembers the last progress so throttle waits can be reported with their context.</summary>
    private sealed class ProgressRelay(GraphTodoReader owner) : IProgress<ReadProgress>
    {
        public void Report(ReadProgress value)
        {
            owner._lastProgress = value;
            owner._progress?.Report(value);
        }
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        return null;
    }

    /// <summary>2, 4, 8, ... seconds.</summary>
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempt, 6) + 1));

    private static TimeSpan Clamp(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > MaxRetryDelay ? MaxRetryDelay : value;

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }

    internal static (string? Code, string? Message) ParseGraphError(JsonObject? body)
    {
        if (body?["error"] is not JsonObject error)
        {
            return (null, null);
        }

        return (GetString(error, "code"), GetString(error, "message"));
    }

    /// <summary>
    /// The HttpClient may carry a bearer token as a default header, so a next link is only accepted
    /// when it points at the same https host as <see cref="BaseUrl"/>.
    /// </summary>
    internal static void EnsureTrustedNextLink(string nextLink)
    {
        var expected = new Uri(BaseUrl);
        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort)
        {
            throw new GraphRequestException(
                $"Refusing to follow @odata.nextLink outside {expected.Scheme}://{expected.Host}: {DescribeUrl(nextLink)}");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Adds a read error, unless one of the same kind is already recorded for the same list and position or task.</summary>
    private void AddError(ReadError error)
    {
        lock (_errorsLock)
        {
            var duplicate = (error.Position is not null || error.TaskId is not null)
                && _errors.Any(e => e.Kind == error.Kind && e.ListId == error.ListId && e.Position == error.Position && e.TaskId == error.TaskId);
            if (!duplicate)
            {
                _errors.Add(error);
            }
        }
    }

    private void AddKindError(ListContext list, SubCollection kind, JsonObject task, string message) =>
        AddError(new ReadError(kind.ErrorKind, list.Id, list.Name, GetString(task, "id"), GetString(task, "title"), null, message));

    /// <summary>Path without query string, for error messages.</summary>
    private static string DescribeUrl(string url)
    {
        var q = url.IndexOf('?');
        return q < 0 ? url : url[..q];
    }

    /// <summary>Detaches the objects in "value" so they can be re-parented into the archive.</summary>
    private static List<JsonObject> TakeValues(JsonObject page)
    {
        var items = new List<JsonObject>();
        if (page["value"] is JsonArray values)
        {
            var detached = values.ToList();
            values.Clear();
            items.AddRange(detached.OfType<JsonObject>());
        }

        return items;
    }

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonArray ToArray(IEnumerable<JsonObject> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add((JsonNode)item);
        }

        return array;
    }
}

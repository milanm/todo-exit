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

/// <summary>Progress of a Graph read. StepsDone/StepsTotal are set in the Steps phase for the current list.</summary>
public sealed record ReadProgress(
    ReadPhase Phase,
    int ListsDone,
    int ListsTotal,
    int TasksRead,
    string? CurrentList,
    int StepsDone = 0,
    int StepsTotal = 0);

/// <summary>A Graph request failed. The message carries the status code and Graph's error text, never a token.</summary>
public sealed class GraphRequestException : Exception
{
    public GraphRequestException(string message, HttpStatusCode? statusCode = null, string? graphErrorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        GraphErrorCode = graphErrorCode;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? GraphErrorCode { get; }
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
    public const int MaxBatchesInFlight = 4;

    /// <summary>After this many windows in a row yield no task at all, the reader stops reading that list.</summary>
    internal const int MaxDeadWindows = 2;

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _errorsLock = new();
    private List<ReadError> _errors = new();
    private bool _batchUnavailable;

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

    /// <summary>When false, completed tasks get an empty step list without any request. Default true.</summary>
    public bool IncludeStepsForCompletedTasks { get; init; } = true;

    /// <summary>How steps were read in the last run: "batch", "individual" (batching unavailable) or "none".</summary>
    public string StepReadMode { get; private set; } = "none";

    public async Task<TodoArchiveDocument> ReadAsync(
        IProgress<ReadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _errors = new List<ReadError>();
        _batchUnavailable = false;
        StepReadMode = "none";

        progress?.Report(new ReadProgress(ReadPhase.Lists, 0, 0, 0, null));
        var lists = await GetCollectionAsync(BaseUrl + "me/todo/lists", cancellationToken).ConfigureAwait(false)
            ?? throw new GraphRequestException("Graph returned invalid JSON for the list of task lists, twice.");

        var result = new List<ArchivedList>(lists.Count);
        var tasksRead = 0;

        for (var i = 0; i < lists.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = lists[i];
            var listId = GetString(list, "id") ?? throw new GraphRequestException("Graph returned a list without an id.");
            var context = new ListContext(listId, GetString(list, "displayName"), i, lists.Count);

            progress?.Report(new ReadProgress(ReadPhase.Tasks, i, lists.Count, tasksRead, context.Name));
            var tasks = await ReadTasksAsync(context, tasksRead, progress, cancellationToken).ConfigureAwait(false);
            tasksRead += tasks.Count;

            await ReadStepsAsync(context, tasks, tasksRead, progress, cancellationToken).ConfigureAwait(false);
            await ReadAttachmentsAsync(context, tasks, tasksRead, progress, cancellationToken).ConfigureAwait(false);

            result.Add(new ArchivedList(list, tasks));
            progress?.Report(new ReadProgress(ReadPhase.Attachments, i + 1, lists.Count, tasksRead, context.Name));
        }

        var notes = new ReadNotes(!IncludeStepsForCompletedTasks, _errors.ToList());
        return TodoArchiveDocument.Create(_clock(), result, notes);
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
    /// A window that cannot be read (invalid JSON twice, or an error status other than 401) is bisected,
    /// so every readable task is still read.
    /// </summary>
    private async Task<List<JsonObject>> ReadTasksAsync(
        ListContext list, int tasksBefore, IProgress<ReadProgress>? progress, CancellationToken cancellationToken)
    {
        var tasks = new List<JsonObject>();
        var skip = 0;
        var deadWindows = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await TryGetPageAsync(PageUrl(list, skip, PageSize), cancellationToken).ConfigureAwait(false);
            if (result.Page is { } page)
            {
                var values = TakeValues(page);
                tasks.AddRange(values);
                progress?.Report(new ReadProgress(ReadPhase.Tasks, list.Index, list.Total, tasksBefore + tasks.Count, list.Name));
                var next = GetString(page, "@odata.nextLink");
                if (next is null || values.Count == 0)
                {
                    break;
                }

                EnsureTrustedNextLink(next);
                skip += values.Count;
                deadWindows = 0;
                continue;
            }

            // Broken window: read what can be read, then continue with the next window.
            // If this was the last window, the next request returns an empty page without a nextLink.
            var before = tasks.Count;
            var errorsBefore = ErrorCount();
            await BisectAsync(list, skip, PageSize, result, tasks, cancellationToken).ConfigureAwait(false);
            skip += PageSize;
            progress?.Report(new ReadProgress(ReadPhase.Tasks, list.Index, list.Total, tasksBefore + tasks.Count, list.Name));

            var dead = tasks.Count == before && ErrorCount() > errorsBefore;
            deadWindows = dead ? deadWindows + 1 : 0;
            if (deadWindows >= MaxDeadWindows)
            {
                // Every request in the last windows failed: stop instead of probing forever.
                break;
            }
        }

        return tasks;
    }

    /// <param name="failed">The failed result for [skip, skip+top). Its raw body is used to salvage a single task.</param>
    private async Task BisectAsync(
        ListContext list, int skip, int top, PageResult failed, List<JsonObject> into, CancellationToken cancellationToken)
    {
        if (top <= 1)
        {
            Salvage(list, skip, failed, into);
            return;
        }

        var half = top / 2;
        foreach (var (s, t) in new[] { (skip, half), (skip + half, top - half) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await TryGetPageAsync(PageUrl(list, s, t), cancellationToken).ConfigureAwait(false);
            if (result.Page is { } page)
            {
                var values = TakeValues(page);
                into.AddRange(values);
                if (values.Count < t && GetString(page, "@odata.nextLink") is null)
                {
                    return; // past the end of the list
                }
            }
            else
            {
                await BisectAsync(list, s, t, result, into, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A single task whose page could not be read. If Graph sent a broken body, keep every property it managed
    /// to send. Without an id the task is recorded as unreadable.
    /// </summary>
    private void Salvage(ListContext list, int position, PageResult failed, List<JsonObject> into)
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
                into.Add(task);
                AddError(new ReadError(ReadErrorKinds.TaskPartial, list.Id, list.Name, id, GetString(task, "title"), position,
                    DescribeBreak(salvaged) + " The task is kept with the properties Graph sent before that."));
                return;
            }
            else
            {
                AddError(new ReadError(ReadErrorKinds.TaskUnreadable, list.Id, list.Name, null, GetString(task, "title"), position,
                    DescribeBreak(salvaged) + " The task id was not among the properties Graph sent, so the task is not in the archive."));
                return;
            }

            AddError(new ReadError(ReadErrorKinds.TaskUnreadable, list.Id, list.Name, null, null, position,
                "Graph returned broken data for this task and nothing could be recovered. The task is not in the archive."));
            return;
        }

        var reason = failed.Error is { } error ? error.Message : "Graph returned no usable data for this task.";
        AddError(new ReadError(ReadErrorKinds.TaskUnreadable, list.Id, list.Name, null, null, position,
            reason + " The task is not in the archive."));
    }

    private static string DescribeBreak(BrokenTaskParser.Result salvaged)
    {
        var code = salvaged.ErrorCode is null ? string.Empty : $" ({salvaged.ErrorCode})";
        return salvaged.FailedProperty is { } property
            ? $"Graph failed while writing '{property}' for this task{code}."
            : $"Graph returned broken data for this task{code}.";
    }

        // ------------------------------------------------------------------ steps

    private async Task ReadStepsAsync(
        ListContext list, List<JsonObject> tasks, int tasksRead, IProgress<ReadProgress>? progress, CancellationToken cancellationToken)
    {
        var pending = new List<JsonObject>();
        foreach (var task in tasks)
        {
            if (task["checklistItems"] is JsonArray)
            {
                continue; // already present (Graph may start honoring $expand one day)
            }

            if (GetString(task, "id") is null || (!IncludeStepsForCompletedTasks && IsCompleted(task)))
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

        var total = pending.Count;
        var done = 0;
        void Report() => progress?.Report(new ReadProgress(ReadPhase.Steps, list.Index, list.Total, tasksRead, list.Name, Volatile.Read(ref done), total));
        void MarkDone(int n)
        {
            Interlocked.Add(ref done, n);
            Report();
        }

        Report();

        var individual = new List<JsonObject>();
        if (_batchUnavailable)
        {
            individual.AddRange(pending);
        }
        else
        {
            StepReadMode = "batch";
            var round = pending;
            for (var attempt = 0; round.Count > 0; attempt++)
            {
                var outcome = await RunBatchRoundAsync(list, round, MarkDone, cancellationToken).ConfigureAwait(false);
                individual.AddRange(outcome.Individual);
                if (outcome.Throttled.Count == 0)
                {
                    break;
                }

                if (attempt >= MaxRetries)
                {
                    foreach (var task in outcome.Throttled)
                    {
                        task["checklistItems"] = new JsonArray();
                        AddStepsError(list, task, $"Graph kept throttling the steps request after {MaxRetries} retries.");
                    }

                    MarkDone(outcome.Throttled.Count);
                    break;
                }

                await _delay(Clamp(outcome.RetryAfter ?? Backoff(attempt)), cancellationToken).ConfigureAwait(false);
                round = outcome.Throttled;
            }
        }

        if (individual.Count > 0)
        {
            if (StepReadMode == "none" || _batchUnavailable)
            {
                StepReadMode = "individual";
            }

            foreach (var task in individual)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = list.TaskUrl(GetString(task, "id")!) + "/checklistItems";
                task["checklistItems"] = await GetOptionalCollectionAsync(url, () => AddStepsError(list, task, "Graph returned invalid JSON for the steps of this task."),
                    ex => AddStepsError(list, task, ex.Message), cancellationToken).ConfigureAwait(false);
                MarkDone(1);
            }
        }
    }

    private sealed record BatchRoundOutcome(List<JsonObject> Throttled, List<JsonObject> Individual, TimeSpan? RetryAfter);

    private async Task<BatchRoundOutcome> RunBatchRoundAsync(
        ListContext list, List<JsonObject> tasks, Action<int> markDone, CancellationToken cancellationToken)
    {
        var throttled = new List<JsonObject>();
        var individual = new List<JsonObject>();
        TimeSpan? retryAfter = null;
        var gate = new object();
        using var semaphore = new SemaphoreSlim(MaxBatchesInFlight);

        var chunks = tasks.Chunk(BatchSize).ToList();
        var work = chunks.Select(async chunk =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var outcome = await RunBatchAsync(list, chunk, markDone, cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    throttled.AddRange(outcome.Throttled);
                    individual.AddRange(outcome.Individual);
                    if (outcome.RetryAfter is { } r && (retryAfter is null || r > retryAfter))
                    {
                        retryAfter = r;
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(work).ConfigureAwait(false);

        // Keep the original order so retries and fallbacks are deterministic.
        var order = new Dictionary<JsonObject, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < tasks.Count; i++)
        {
            order[tasks[i]] = i;
        }

        throttled.Sort((a, b) => order[a].CompareTo(order[b]));
        individual.Sort((a, b) => order[a].CompareTo(order[b]));
        return new BatchRoundOutcome(throttled, individual, retryAfter);
    }

    private async Task<BatchRoundOutcome> RunBatchAsync(
        ListContext list, JsonObject[] chunk, Action<int> markDone, CancellationToken cancellationToken)
    {
        var requests = new JsonArray();
        for (var i = 0; i < chunk.Length; i++)
        {
            requests.Add((JsonNode)new JsonObject
            {
                ["id"] = i.ToString(CultureInfo.InvariantCulture),
                ["method"] = "GET",
                ["url"] = list.TaskRelativeUrl(GetString(chunk[i], "id")!) + "/checklistItems",
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
            return new BatchRoundOutcome([], chunk.ToList(), null);
        }

        if (response?["responses"] is not JsonArray responses)
        {
            return new BatchRoundOutcome([], chunk.ToList(), null);
        }

        var throttled = new List<JsonObject>();
        var handled = new bool[chunk.Length];
        TimeSpan? retryAfter = null;

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

            if (status is 429 or 503)
            {
                throttled.Add(task);
                var delay = SubResponseRetryAfter(sub);
                if (delay is { } d && (retryAfter is null || d > retryAfter))
                {
                    retryAfter = d;
                }

                continue;
            }

            if (status is >= 200 and < 300)
            {
                var items = body is null ? new List<JsonObject>() : TakeValues(body);
                if (body is not null && GetString(body, "@odata.nextLink") is { } next)
                {
                    try
                    {
                        EnsureTrustedNextLink(next);
                        var more = await GetCollectionAsync(next, cancellationToken).ConfigureAwait(false);
                        if (more is null)
                        {
                            AddStepsError(list, task, "Graph returned invalid JSON for a later page of steps; some steps may be missing.");
                        }
                        else
                        {
                            items.AddRange(more);
                        }
                    }
                    catch (GraphRequestException ex) when (ex.StatusCode != HttpStatusCode.Unauthorized)
                    {
                        AddStepsError(list, task, ex.Message);
                    }
                }

                task["checklistItems"] = ToArray(items);
            }
            else
            {
                task["checklistItems"] = new JsonArray();
                var (code, message) = ParseGraphError(body);
                var text = status == 404
                    ? "Graph returned 404 Not Found for the steps of this task."
                    : $"Graph returned {status} for the steps of this task.";
                if (code is not null || message is not null)
                {
                    text += $" {code}{(code is not null && message is not null ? " - " : string.Empty)}{message}";
                }

                AddStepsError(list, task, text);
            }

            markDone(1);
        }

        // Sub-responses that never came back are retried like throttled ones.
        for (var i = 0; i < chunk.Length; i++)
        {
            if (!handled[i])
            {
                throttled.Add(chunk[i]);
            }
        }

        return new BatchRoundOutcome(throttled, [], retryAfter);
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

    // ------------------------------------------------------------------ attachments

    private async Task ReadAttachmentsAsync(
        ListContext list, List<JsonObject> tasks, int tasksRead, IProgress<ReadProgress>? progress, CancellationToken cancellationToken)
    {
        var withAttachments = new List<JsonObject>();
        foreach (var task in tasks)
        {
            if (GetString(task, "id") is not null
                && task["hasAttachments"] is JsonValue hv && hv.TryGetValue<bool>(out var has) && has)
            {
                withAttachments.Add(task);
            }
            else if (task["attachments"] is not JsonArray)
            {
                task["attachments"] = new JsonArray();
            }
        }

        if (withAttachments.Count == 0)
        {
            return;
        }

        progress?.Report(new ReadProgress(ReadPhase.Attachments, list.Index, list.Total, tasksRead, list.Name));
        foreach (var task in withAttachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = list.TaskUrl(GetString(task, "id")!) + "/attachments";
            var items = await GetOptionalCollectionAsync(url,
                () => AddError(new ReadError(ReadErrorKinds.AttachmentsUnreadable, list.Id, list.Name, GetString(task, "id"), GetString(task, "title"), null,
                    "Graph returned invalid JSON for the attachment list of this task.")),
                ex => AddError(new ReadError(ReadErrorKinds.AttachmentsUnreadable, list.Id, list.Name, GetString(task, "id"), GetString(task, "title"), null, ex.Message)),
                cancellationToken).ConfigureAwait(false);

            foreach (var attachment in items.OfType<JsonObject>())
            {
                // Metadata only. contentBytes is never requested, but drop it defensively if present.
                attachment.Remove("contentBytes");
            }

            task["attachments"] = items;
        }
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

    /// <summary>
    /// GET a collection for a single task. Errors other than 401 are recorded through the callbacks and
    /// give an empty array, so one bad task does not stop the read.
    /// </summary>
    private async Task<JsonArray> GetOptionalCollectionAsync(
        string url, Action onInvalidJson, Action<GraphRequestException> onError, CancellationToken cancellationToken)
    {
        try
        {
            var items = await GetCollectionAsync(url, cancellationToken).ConfigureAwait(false);
            if (items is null)
            {
                onInvalidJson();
                return new JsonArray();
            }

            return ToArray(items);
        }
        catch (GraphRequestException ex) when (ex.StatusCode != HttpStatusCode.Unauthorized)
        {
            onError(ex);
            return new JsonArray();
        }
    }

    /// <summary>Outcome of reading one page: a parsed page, or the raw invalid body and/or the error.</summary>
    private sealed record PageResult(JsonObject? Page, string? RawBody, GraphRequestException? Error);

    /// <summary>
    /// GET one page without throwing for Graph errors other than 401. A body that is not valid JSON is retried once.
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
            catch (GraphRequestException ex) when (ex.StatusCode != HttpStatusCode.Unauthorized)
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
        for (var attempt = 0; ; attempt++)
        {
            HttpStatusCode? status = null;
            TimeSpan delay;
            Exception? failure = null;
            try
            {
                using var request = createRequest();
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                status = response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }

                if (status == HttpStatusCode.Unauthorized)
                {
                    throw new GraphRequestException(
                        "Microsoft Graph rejected the access token (401 Unauthorized). The token is missing, invalid or expired. Get a new token and run the command again.",
                        status);
                }

                var code = (int)status.Value;
                if (status != HttpStatusCode.TooManyRequests && code < 500)
                {
                    var (errorCode, message) = ParseGraphError(TryParseObject(await SafeReadAsync(response, cancellationToken).ConfigureAwait(false)));
                    var text = $"Microsoft Graph request failed with {code} {status} for {DescribeUrl(url)}";
                    if (errorCode is not null || message is not null)
                    {
                        text += $": {errorCode}{(errorCode is not null && message is not null ? " - " : string.Empty)}{message}";
                    }

                    throw new GraphRequestException(text, status, errorCode);
                }

                delay = status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                    ? RetryAfterOf(response) ?? Backoff(attempt)
                    : Backoff(attempt);
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
                delay = Backoff(attempt);
            }

            if (attempt >= MaxRetries)
            {
                var reason = failure is not null
                    ? $"a network error ({failure.Message})"
                    : $"{(int)status!.Value} {status}";
                throw new GraphRequestException(
                    $"Microsoft Graph request kept failing with {reason} after {MaxRetries} retries: {DescribeUrl(url)}. Try again later.",
                    status,
                    inner: failure);
            }

            await _delay(Clamp(delay), cancellationToken).ConfigureAwait(false);
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

    private int ErrorCount()
    {
        lock (_errorsLock)
        {
            return _errors.Count;
        }
    }

    private void AddError(ReadError error)
    {
        lock (_errorsLock)
        {
            _errors.Add(error);
        }
    }

    private void AddStepsError(ListContext list, JsonObject task, string message) =>
        AddError(new ReadError(ReadErrorKinds.StepsUnreadable, list.Id, list.Name, GetString(task, "id"), GetString(task, "title"), null, message));

    private static bool IsCompleted(JsonObject task) =>
        string.Equals(GetString(task, "status"), "completed", StringComparison.OrdinalIgnoreCase);

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

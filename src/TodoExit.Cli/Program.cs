using System.Globalization;
using System.Net.Http.Headers;
using TodoExit.Cli.Auth;
using TodoExit.Core;
using TodoExit.Core.Archive;
using TodoExit.Core.Export;
using TodoExit.Core.Graph;
using TodoExit.Core.Model;

namespace TodoExit.Cli;

public static class Program
{
    internal const int ExitOk = 0;
    internal const int ExitUsage = 1;
    internal const int ExitFailure = 2;
    internal const int ExitInterrupted = 3;

    internal const string PartialArchiveFileName = "archive.partial.json";

    private const string Usage = """
        todo-exit: export Microsoft To Do to files

        Usage:
          todo-exit inventory [--token <t> | --token-file <path> | --from-archive <file>] [--timezone <IANA id>]
                              [--verify-steps] [--resume <file>] [--out <dir>] [--client-id <guid>] [--tenant <id>]
          todo-exit export    [--token <t> | --token-file <path> | --from-archive <file>] --out <dir>
                              [--formats json,markdown,todoist,csv] [--include-completed] [--timezone <IANA id>]
                              [--verify-steps] [--resume <file>] [--client-id <guid>] [--tenant <id>]

        Options:
          (no token)            Without --token, --token-file, TODOEXIT_TOKEN or --from-archive, the tool signs you in:
                                it prints a web address and a code; open it, enter the code, sign in.
          --client-id <guid>    Use your own app registration (also TODOEXIT_CLIENT_ID).
          --tenant <id>         common (default), consumers, organizations, a tenant ID or a domain.
          --token <t>           Advanced: a Microsoft Graph access token with Tasks.Read. Also TODOEXIT_TOKEN.
          --token-file <path>   Read the token from a file.
          --from-archive <file> Read a todo-exit archive.json (or archive.partial.json) instead of calling Microsoft Graph.
          --resume <file>       Continue an interrupted read from archive.partial.json. Signs in again (or takes a token).
          --out <dir>           Output directory. For inventory, only used to save a partial archive.
          --formats <list>      Comma-separated: json, markdown, todoist, csv. Default: all four.
          --include-completed   Write completed tasks and checked steps to the Todoist CSV as open tasks.
          --verify-steps        Also ask Graph for the steps of every task that came without inline steps,
                                to double-check. Slower; normally not needed.
          --timezone <id>       Time zone for dates, e.g. Europe/Belgrade. Default: this computer's zone.
          -h, --help            Show this help.

        Exit codes: 0 success, 1 usage error, 2 failure, 3 interrupted (partial archive saved).
        """;

    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Ctrl+C stops the read; what was read is saved as a partial archive.
            e.Cancel = true;
            cts.Cancel();
        };

        return await RunAsync(args, Console.Out, Console.Error, handler: null, cts.Token);
    }

    /// <summary>The whole command. <paramref name="handler"/> replaces the network for tests.</summary>
    /// <param name="authorityBase">Sign-in endpoint base for tests; default https://login.microsoftonline.com/.</param>
    /// <param name="clock">Clock for token expiry; tests inject one.</param>
    internal static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, HttpMessageHandler? handler, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null, string? authorityBase = null, Func<DateTimeOffset>? clock = null)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (UsageException ex)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            await stderr.WriteLineAsync();
            await stderr.WriteLineAsync(Usage);
            return ExitUsage;
        }

        if (options.Help)
        {
            await stdout.WriteLineAsync(Usage);
            return ExitOk;
        }

        foreach (var notice in options.Notices)
        {
            await stderr.WriteLineAsync(notice);
        }

        try
        {
            TodoArchiveDocument document;
            try
            {
                document = await LoadAsync(options, stderr, handler, delay, authorityBase, clock ?? (() => DateTimeOffset.UtcNow), cancellationToken);
            }
            catch (SignInException ex)
            {
                await stderr.WriteLineAsync($"error: {ex.Message}");
                return ExitFailure;
            }
            catch (GraphReadInterruptedException ex)
            {
                await stderr.WriteLineAsync();
                await stderr.WriteLineAsync(ex.WasCancelled ? "Cancelled." : $"The read stopped: {ex.Reason}");
                var outDir = Path.GetFullPath(options.OutDir ?? ".");
                Directory.CreateDirectory(outDir);
                var partialPath = Path.Combine(outDir, PartialArchiveFileName);
                await File.WriteAllBytesAsync(partialPath, ex.PartialArchive.ToUtf8Bytes(), CancellationToken.None);
                await stderr.WriteLineAsync(options.SignIn
                    ? $"Saved what was read to {partialPath}. Run the same command with --resume \"{partialPath}\"; it will ask you to sign in again."
                    : $"Saved what was read to {partialPath}. Get a new token and run the same command with --resume \"{partialPath}\".");
                return ExitInterrupted;
            }

            var archive = ArchiveParser.Parse(document, options.TimeZone);
            if (archive.ReadNotes.Interrupted)
            {
                await stderr.WriteLineAsync($"Note: this archive is incomplete. The read stopped: {archive.ReadNotes.InterruptedReason}");
            }

            if (options.Command == "inventory")
            {
                await stdout.WriteAsync(FormatInventory(Inventory.From(archive)));
                await WriteResumeDoneAsync(options, stdout);
                return ExitOk;
            }

            var result = ExportService.Export(document, archive, new ExportOptions(options.Formats, options.IncludeCompleted));
            var exportDir = Path.GetFullPath(options.OutDir!);
            foreach (var file in result.Files)
            {
                var path = Path.Combine(exportDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, file.Content, cancellationToken);
            }

            await stdout.WriteLineAsync($"Wrote {result.Files.Count} files to {exportDir}");
            foreach (var file in result.Files)
            {
                await stdout.WriteLineAsync("  " + file.Path);
            }

            foreach (var line in result.Report.SummaryLines())
            {
                await stdout.WriteLineAsync(line);
            }
            await WriteResumeDoneAsync(options, stdout);
            return ExitOk;
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("Cancelled.");
            return ExitFailure;
        }
        catch (Exception ex) when (ex is GraphRequestException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or HttpRequestException)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            return ExitFailure;
        }
    }

    private static async Task WriteResumeDoneAsync(Options options, TextWriter stdout)
    {
        if (options.ResumePath is { } resume)
        {
            await stdout.WriteLineAsync($"The read is complete. You can remove {Path.GetFullPath(resume)}.");
        }
    }

    private static async Task<TodoArchiveDocument> LoadAsync(
        Options options, TextWriter stderr, HttpMessageHandler? handler, Func<TimeSpan, CancellationToken, Task>? delay,
        string? authorityBase, Func<DateTimeOffset> clock, CancellationToken cancellationToken)
    {
        if (options.ArchivePath is { } archivePath)
        {
            return await LoadFileAsync(archivePath, cancellationToken);
        }

        TodoArchiveDocument? resume = null;
        if (options.ResumePath is { } resumePath)
        {
            resume = await LoadFileAsync(resumePath, cancellationToken);
        }

        HttpClient http;
        if (options.SignIn)
        {
            // Device code sign-in. Tokens stay in memory; the handler adds them to Graph requests only.
            var signInOptions = new SignInOptions(options.ClientId, options.Tenant, authorityBase ?? SignInOptions.DefaultAuthorityBase);
            var authHttp = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            authHttp.Timeout = TimeSpan.FromMinutes(1);
            var signIn = new DeviceCodeSignIn(authHttp, signInOptions, delay ?? Task.Delay, clock);
            var tokens = await signIn.SignInAsync(message => stderr.WriteLine(message), cancellationToken);
            await stderr.WriteLineAsync("Signed in.");
            var authHandler = new GraphAuthHandler(tokens, signIn.RefreshAsync, clock) { InnerHandler = handler ?? new HttpClientHandler() };
            http = new HttpClient(authHandler, disposeHandler: handler is null);
        }
        else
        {
            http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token!);
        }

        using var disposeHttp = http;
        http.Timeout = TimeSpan.FromMinutes(2);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("todo-exit/0.1");

        var reader = new GraphTodoReader(http, delay) { VerifySteps = options.VerifySteps };
        var progress = new SyncProgress<ReadProgress>(p => stderr.Write("\r" + FormatProgress(p).PadRight(90)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var document = await reader.ReadAsync(progress, cancellationToken, resume);
        stopwatch.Stop();

        var lists = document.Lists.Count;
        var tasks = document.Lists.OfType<System.Text.Json.Nodes.JsonObject>()
            .Sum(l => (l["tasks"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0);
        var errors = document.ReadNotes.Errors.Count;
        await stderr.WriteLineAsync();
        await stderr.WriteLineAsync(
            $"Read {lists} lists and {tasks} tasks in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s. " +
            (errors == 0 ? "No read errors." : $"{errors} read errors, listed in report.md.") +
            (options.VerifySteps
                ? reader.StepsFoundByVerification == 0
                    ? " Step check: every task without inline steps really has none."
                    : $" Step check: found steps on {reader.StepsFoundByVerification} tasks that had none inline."
                : string.Empty));

        return document;
    }

    private static async Task<TodoArchiveDocument> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new IOException($"Archive file not found: {path}");
        }

        await using var stream = File.OpenRead(path);
        return await TodoArchiveDocument.LoadAsync(stream, cancellationToken);
    }

    internal static string FormatProgress(ReadProgress p)
    {
        var list = p.CurrentList is null ? string.Empty : $" ({p.CurrentList})";
        var position = p.ListsTotal > 0 ? $"list {Math.Min(p.ListsDone + 1, p.ListsTotal)}/{p.ListsTotal}{list}" : "lists";
        if (p.IsWaiting)
        {
            return $"Graph asked us to slow down, waiting {p.WaitingSeconds} s ({position}, {p.TasksRead} tasks)";
        }

        return p.Phase switch
        {
            ReadPhase.Lists => "Reading lists...",
            ReadPhase.Tasks => $"Reading tasks: {position}, {p.TasksRead} tasks",
            ReadPhase.Steps => $"Reading steps: {position}, {p.Done}/{p.Total} tasks",
            _ => p.Total > 0
                ? $"Reading attachment details: {position}, {p.Done}/{p.Total} tasks"
                : $"Reading attachment details: {position}, {p.TasksRead} tasks",
        };
    }

    internal static string FormatInventory(Inventory inventory)
    {
        var rows = inventory.Totals.Rows();
        var labelWidth = rows.Max(r => r.Label.Length);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Totals");
        foreach (var (label, value) in rows)
        {
            sb.Append("  ").Append(label.PadRight(labelWidth)).Append("  ")
                .AppendLine(value.ToString(CultureInfo.InvariantCulture).PadLeft(6));
        }

        if (inventory.PerList.Count > 0)
        {
            var nameWidth = Math.Max("List".Length, inventory.PerList.Max(l => l.ListName.Length));
            nameWidth = Math.Min(nameWidth, 40);
            string[] headers = ["Tasks", "Open", "Done", "Recur", "Steps", "Notes", "Remind", "Attach", "Links", "Shared"];
            sb.AppendLine();
            sb.Append("  ").Append("List".PadRight(nameWidth));
            foreach (var h in headers)
            {
                sb.Append("  ").Append(h.PadLeft(6));
            }

            sb.AppendLine();
            foreach (var list in inventory.PerList)
            {
                var c = list.Counts;
                var name = list.ListName.Length > nameWidth ? list.ListName[..(nameWidth - 3)] + "..." : list.ListName;
                sb.Append("  ").Append(name.PadRight(nameWidth));
                int[] values = [c.Tasks, c.Open, c.Completed, c.Recurring, c.WithSteps, c.WithNotes, c.WithReminders, c.WithAttachments, c.WithLinkedResources];
                foreach (var v in values)
                {
                    sb.Append("  ").Append(v.ToString(CultureInfo.InvariantCulture).PadLeft(6));
                }

                sb.Append("  ").AppendLine((c.SharedLists > 0 ? "yes" : "no").PadLeft(6));
            }
        }

        return sb.ToString();
    }

    /// <summary>Reports progress synchronously on the calling thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed class Options
{
    public string Command { get; private set; } = string.Empty;

    public bool Help { get; private set; }

    public string? Token { get; private set; }

    public string? ArchivePath { get; private set; }

    public string? OutDir { get; private set; }

    public ExportFormats Formats { get; private set; } = ExportFormats.All;

    public bool IncludeCompleted { get; private set; }

    public bool VerifySteps { get; private set; }

    /// <summary>No token was given: sign in with the device code flow.</summary>
    public bool SignIn { get; private set; }

    public string ClientId { get; private set; } = SignInOptions.DefaultClientId;

    public string Tenant { get; private set; } = "common";

    /// <summary>Lines to print on stderr before running, e.g. for options that are no longer needed.</summary>
    public List<string> Notices { get; } = new();

    public string? ResumePath { get; private set; }

    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Local;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        if (args.Length == 0)
        {
            throw new UsageException("missing command");
        }

        if (args[0] is "-h" or "--help" or "help")
        {
            o.Help = true;
            return o;
        }

        o.Command = args[0];
        if (o.Command is not ("inventory" or "export"))
        {
            throw new UsageException($"unknown command '{args[0]}'");
        }

        string? token = null, tokenFile = null, archive = null, timezone = null, formats = null, clientId = null, tenant = null;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            string? inline = null;
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                inline = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            string Value()
            {
                if (inline is not null)
                {
                    return inline;
                }

                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"{arg} needs a value");
                }

                return args[++i];
            }

            switch (arg)
            {
                case "-h":
                case "--help":
                    o.Help = true;
                    return o;
                case "--token":
                    token = Value();
                    break;
                case "--token-file":
                    tokenFile = Value();
                    break;
                case "--from-archive":
                    archive = Value();
                    break;
                case "--timezone":
                    timezone = Value();
                    break;
                case "--verify-steps":
                    if (inline is not null)
                    {
                        throw new UsageException("--verify-steps does not take a value");
                    }

                    o.VerifySteps = true;
                    break;
                case "--client-id":
                    clientId = Value();
                    break;
                case "--tenant":
                    tenant = Value();
                    break;
                case "--resume":
                    o.ResumePath = Value();
                    break;
                case "--out":
                    o.OutDir = Value();
                    break;
                case "--formats" when o.Command == "export":
                    formats = Value();
                    break;
                case "--skip-completed-steps":
                    if (inline is not null)
                    {
                        throw new UsageException("--skip-completed-steps does not take a value");
                    }

                    o.Notices.Add("Note: --skip-completed-steps is no longer needed. Steps now come with the tasks, so it is ignored.");
                    break;
                case "--include-completed" when o.Command == "export":
                    if (inline is not null)
                    {
                        throw new UsageException("--include-completed does not take a value");
                    }

                    o.IncludeCompleted = true;
                    break;
                default:
                    throw new UsageException($"unknown option '{args[i]}' for '{o.Command}'");
            }
        }

        clientId ??= Environment.GetEnvironmentVariable("TODOEXIT_CLIENT_ID") is { Length: > 0 } envClient ? envClient.Trim() : null;
        if (clientId is not null)
        {
            if (!Guid.TryParse(clientId, out var parsedClient) || parsedClient == Guid.Empty)
            {
                throw new UsageException($"--client-id must be an application (client) ID, a GUID; got '{clientId}'");
            }

            o.ClientId = parsedClient.ToString();
        }

        if (tenant is not null)
        {
            if (tenant.Length == 0 || !tenant.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            {
                throw new UsageException($"--tenant must be common, consumers, organizations, a tenant ID or a domain; got '{tenant}'");
            }

            o.Tenant = tenant;
        }

        var sources = new[] { token, tokenFile, archive }.Count(s => s is not null);
        if (sources > 1)
        {
            throw new UsageException("use only one of --token, --token-file and --from-archive");
        }

        if (archive is not null && o.ResumePath is not null)
        {
            throw new UsageException("--resume continues a read from Microsoft Graph and cannot be combined with --from-archive");
        }

        if (archive is not null)
        {
            o.ArchivePath = archive;
        }
        else if (token is not null)
        {
            o.Token = token;
        }
        else if (tokenFile is not null)
        {
            try
            {
                o.Token = File.ReadAllText(tokenFile).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UsageException($"cannot read token file: {ex.Message}");
            }
        }
        else if (Environment.GetEnvironmentVariable("TODOEXIT_TOKEN") is { Length: > 0 } envToken)
        {
            o.Token = envToken.Trim();
        }
        else
        {
            o.SignIn = true;
        }

        if (o.ArchivePath is null && !o.SignIn && string.IsNullOrWhiteSpace(o.Token))
        {
            throw new UsageException("the token is empty");
        }

        if (o.Token is { } t && t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            o.Token = t[7..].Trim();
        }

        if (o.Command == "export" && string.IsNullOrWhiteSpace(o.OutDir))
        {
            throw new UsageException("export needs --out <dir>");
        }

        if (formats is not null)
        {
            if (!ExportFormatNames.TryParse(formats, out var parsed, out var invalid))
            {
                throw new UsageException($"unknown format '{invalid}'. Use json, markdown, todoist, csv");
            }

            o.Formats = parsed;
        }

        if (timezone is not null)
        {
            try
            {
                o.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new UsageException($"unknown time zone '{timezone}'");
            }
        }

        return o;
    }
}

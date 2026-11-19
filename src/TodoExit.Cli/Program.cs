using System.Globalization;
using System.Net.Http.Headers;
using TodoExit.Core;
using TodoExit.Core.Archive;
using TodoExit.Core.Export;
using TodoExit.Core.Graph;
using TodoExit.Core.Model;

namespace TodoExit.Cli;

public static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitFailure = 2;

    private const string Usage = """
        todo-exit: export Microsoft To Do to files

        Usage:
          todo-exit inventory (--token <t> | --token-file <path> | --from-archive <file>) [--timezone <IANA id>]
                              [--skip-completed-steps]
          todo-exit export    (--token <t> | --token-file <path> | --from-archive <file>) --out <dir>
                              [--formats json,markdown,todoist,csv] [--include-completed] [--timezone <IANA id>]
                              [--skip-completed-steps]

        Options:
          --token <t>           Microsoft Graph access token with Tasks.Read. Can also come from TODOEXIT_TOKEN.
          --token-file <path>   Read the token from a file.
          --from-archive <file> Read a todo-exit archive.json instead of calling Microsoft Graph.
          --out <dir>           Output directory for export.
          --formats <list>      Comma-separated: json, markdown, todoist, csv. Default: all four.
          --include-completed   Write completed tasks and checked steps to the Todoist CSV as open tasks.
          --skip-completed-steps Do not read steps of completed tasks (faster for large accounts).
          --timezone <id>       Time zone for dates, e.g. Europe/Belgrade. Default: this computer's zone.
          -h, --help            Show this help.
        """;

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }

        if (options.Help)
        {
            Console.WriteLine(Usage);
            return ExitOk;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var document = await LoadAsync(options, cts.Token);
            var archive = ArchiveParser.Parse(document, options.TimeZone);

            if (options.Command == "inventory")
            {
                Console.Write(FormatInventory(Inventory.From(archive)));
                return ExitOk;
            }

            var result = ExportService.Export(document, archive, new ExportOptions(options.Formats, options.IncludeCompleted));
            var outDir = Path.GetFullPath(options.OutDir!);
            foreach (var file in result.Files)
            {
                var path = Path.Combine(outDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, file.Content, cts.Token);
            }

            Console.WriteLine($"Wrote {result.Files.Count} files to {outDir}");
            foreach (var file in result.Files)
            {
                Console.WriteLine("  " + file.Path);
            }

            var losses = result.Report.Losses;
            Console.WriteLine(losses.Count == 0
                ? "Nothing was lost in the selected formats."
                : $"{losses.Count} items could not be carried over in full. See report.md.");
            return ExitOk;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitFailure;
        }
        catch (Exception ex) when (ex is GraphRequestException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or HttpRequestException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitFailure;
        }
    }

    private static async Task<TodoArchiveDocument> LoadAsync(Options options, CancellationToken cancellationToken)
    {
        if (options.ArchivePath is { } archivePath)
        {
            if (!File.Exists(archivePath))
            {
                throw new IOException($"Archive file not found: {archivePath}");
            }

            await using var stream = File.OpenRead(archivePath);
            return await TodoArchiveDocument.LoadAsync(stream, cancellationToken);
        }

        var token = options.Token!;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("todo-exit/0.1");

        var reader = new GraphTodoReader(http) { IncludeStepsForCompletedTasks = !options.SkipCompletedSteps };
        var progress = new SyncProgress<ReadProgress>(p => Console.Error.Write("\r" + FormatProgress(p).PadRight(90)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var document = await reader.ReadAsync(progress, cancellationToken);
        stopwatch.Stop();

        var lists = document.Lists.Count;
        var tasks = document.Lists.OfType<System.Text.Json.Nodes.JsonObject>()
            .Sum(l => (l["tasks"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0);
        var errors = document.ReadNotes.Errors.Count;
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"Read {lists} lists and {tasks} tasks in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s. " +
            (errors == 0 ? "No read errors." : $"{errors} read errors, listed in report.md."));

        return document;
    }

    internal static string FormatProgress(ReadProgress p)
    {
        var list = p.CurrentList is null ? string.Empty : $" ({p.CurrentList})";
        var position = p.ListsTotal > 0 ? $"list {Math.Min(p.ListsDone + 1, p.ListsTotal)}/{p.ListsTotal}{list}" : "lists";
        return p.Phase switch
        {
            ReadPhase.Lists => "Reading lists...",
            ReadPhase.Tasks => $"Reading tasks: {position}, {p.TasksRead} tasks",
            ReadPhase.Steps => $"Reading steps: {position}, {p.StepsDone}/{p.StepsTotal} tasks",
            _ => $"Reading attachments: {position}, {p.TasksRead} tasks",
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

    public bool SkipCompletedSteps { get; private set; }

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

        string? token = null, tokenFile = null, archive = null, timezone = null, formats = null;
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
                case "--out" when o.Command == "export":
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

                    o.SkipCompletedSteps = true;
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

        var sources = new[] { token, tokenFile, archive }.Count(s => s is not null);
        if (sources > 1)
        {
            throw new UsageException("use only one of --token, --token-file and --from-archive");
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
            throw new UsageException("give --token, --token-file or --from-archive (or set TODOEXIT_TOKEN)");
        }

        if (o.ArchivePath is null && string.IsNullOrWhiteSpace(o.Token))
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

using System.Diagnostics;
using System.Globalization;
using System.Text;
using TodoExit.Core.Archive;
using TodoExit.Core.Export;
using TodoExit.Core.Model;
using TodoExit.Core.Todoist;

namespace TodoExit.Cli;

/// <summary>todo-exit transfer: plan, and with --yes write, an archive into Todoist through its API.</summary>
internal static class TransferCommand
{
    public const string StateFileName = "todoist-transfer-state.json";

    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, HttpMessageHandler? handler, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay, Func<DateTimeOffset> clock, Func<string, string?> environment)
    {
        TransferOptions options;
        try
        {
            options = TransferOptions.Parse(args, environment);
        }
        catch (UsageException ex)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            await stderr.WriteLineAsync();
            await stderr.WriteLineAsync(Program.UsageText);
            return Program.ExitUsage;
        }

        if (options.Help)
        {
            await stdout.WriteLineAsync(Program.UsageText);
            return Program.ExitOk;
        }

        try
        {
            TodoistTransferState? state = null;
            string? statePath = null;
            if (options.ResumePath is { } resume)
            {
                statePath = Path.GetFullPath(resume);
                if (!File.Exists(statePath))
                {
                    throw new IOException($"Transfer state not found: {statePath}");
                }

                state = TodoistTransferState.Parse(await File.ReadAllBytesAsync(statePath, cancellationToken));
            }

            var document = await Program.LoadFileAsync(options.ArchivePath, cancellationToken);
            if (state is not null && state.ArchiveFingerprint != TodoistTransferState.Fingerprint(document))
            {
                await stderr.WriteLineAsync("error: this is not the archive the transfer was started with. Use the same archive.json with --resume.");
                return Program.ExitFailure;
            }

            var zoneId = state?.Settings.TimeZoneId ?? options.TimeZone.Id;
            var archive = ArchiveParser.Parse(document, TimeZones.ResolveOrUtc(zoneId));
            if (state is not null && options.PlanOptionsGiven)
            {
                await stderr.WriteLineAsync("Note: --lists, --include-completed, --timezone and --allow-duplicate-names are taken from the transfer state when resuming; the ones given are ignored.");
            }

            if (state is null && options.Lists is { } chosen && TodoistPlanner.UnknownLists(archive, chosen) is { Count: > 0 } unknown)
            {
                await stderr.WriteLineAsync($"error: no list named {string.Join(", ", unknown.Select(n => $"\"{n}\""))}. Lists in the archive:");
                foreach (var name in archive.Lists.Select(l => l.Name))
                {
                    await stderr.WriteLineAsync("  " + name);
                }

                return Program.ExitUsage;
            }

            using var http = handler is null
                ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
                : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromMinutes(3);
            var client = options.Token is null ? null : new TodoistClient(http, options.Token, delay);

            TodoistAccount? account = null;
            if (client is not null)
            {
                await stderr.WriteLineAsync("Reading the Todoist account...");
                account = await client.ReadAccountAsync(cancellationToken);
            }

            var settings = state?.Settings ?? new TodoistPlanSettings
            {
                IncludeCompleted = options.IncludeCompleted,
                Lists = options.Lists,
                AllowDuplicateNames = options.AllowDuplicateNames,
                TimeZoneId = zoneId,
                MaxTasksPerProject = account?.MaxTasks is > 0 and var max ? max : TodoistPlanSettings.DefaultMaxTasksPerProject,
                Reminders = account?.Reminders,
                PlannedAt = clock(),
            };

            var plan = TodoistPlanner.Build(archive, settings, account, state);
            await stdout.WriteAsync(FormatPlan(plan, state));

            if (!plan.CanStart)
            {
                await stdout.WriteLineAsync();
                await stdout.WriteAsync(FormatFixes(plan));
                if (options.Yes)
                {
                    await stderr.WriteLineAsync("error: the transfer cannot start; see the problems above. Nothing was written to Todoist.");
                }

                return Program.ExitFailure;
            }

            if (!options.Yes)
            {
                await stdout.WriteLineAsync();
                await stdout.WriteLineAsync(account is null
                    ? "Nothing was written to Todoist. This plan was made without a Todoist token, so account limits and existing projects were not checked."
                    : "Nothing was written to Todoist. Run the same command with --yes to transfer.");
                return Program.ExitOk;
            }

            var outDir = Path.GetFullPath(options.OutDir ?? (statePath is null ? "." : Path.GetDirectoryName(statePath)!));
            Directory.CreateDirectory(outDir);
            if (state is null)
            {
                statePath = Path.Combine(outDir, StateFileName);
                if (File.Exists(statePath))
                {
                    await stderr.WriteLineAsync($"error: a transfer state already exists at {statePath}. Continue it with --resume \"{statePath}\", or use another --out.");
                    return Program.ExitFailure;
                }

                state = TodoistTransfer.NewState(plan, TodoistTransferState.Fingerprint(document), clock());
            }

            var path = statePath!;
            async Task Save(TodoistTransferState s, CancellationToken ct)
            {
                var temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, s.ToUtf8Bytes(), ct);
                File.Move(temp, path, overwrite: true);
            }

            var progress = new SyncProgress<TransferProgress>(p => stderr.Write("\r" + FormatProgress(p).PadRight(90)));
            var stopwatch = Stopwatch.StartNew();
            var result = await TodoistTransfer.RunAsync(plan, state, client!, Save, progress, cancellationToken);
            stopwatch.Stop();
            await stderr.WriteLineAsync();

            foreach (var file in TodoistTransferReport.ToFiles(archive, plan, state, result, stopwatch.Elapsed))
            {
                await File.WriteAllBytesAsync(Path.Combine(outDir, file.Path), file.Content, CancellationToken.None);
            }

            var c = TransferCounts.From(plan, state);
            await stdout.WriteLineAsync(
                $"Projects created: {c.Projects}, tasks: {c.Tasks}, sub-tasks: {c.SubTasks}, completed: {c.Completed}, " +
                $"reminders: {c.Reminders}, errors: {c.Errors}, skipped: {c.Skipped}, time: {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s.");
            await stdout.WriteLineAsync($"Report: {Path.Combine(outDir, TodoistTransferReport.MarkdownFileName)}");
            switch (result.Status)
            {
                case TransferStatus.Completed:
                    await stdout.WriteLineAsync(c.Errors + c.Skipped == 0
                        ? "The transfer finished."
                        : "The transfer finished. Todoist refused some items; they are listed in the report.");
                    return Program.ExitOk;
                case TransferStatus.Interrupted:
                    await stderr.WriteLineAsync(result.Message);
                    await stderr.WriteLineAsync($"The transfer state is saved. Continue with: todo-exit transfer --from-archive \"{Path.GetFullPath(options.ArchivePath)}\" --resume \"{path}\" --todoist-token-file <file> --yes");
                    return Program.ExitInterrupted;
                default:
                    await stderr.WriteLineAsync($"error: {result.Message}");
                    await stderr.WriteLineAsync($"The transfer state is saved at {path}; fix the problem and continue with --resume.");
                    return Program.ExitFailure;
            }
        }
        catch (TodoistApiException ex)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            return Program.ExitFailure;
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("Cancelled. Nothing was written to Todoist.");
            return Program.ExitFailure;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            return Program.ExitFailure;
        }
    }

    internal static string FormatProgress(TransferProgress p)
    {
        var list = p.CurrentList is null ? string.Empty : $" ({p.CurrentList})";
        return p.Waiting is { } wait
            ? $"Todoist did not answer ({p.WaitingReason}); waiting {Math.Ceiling(wait.TotalSeconds)} s, {p.CommandsDone}/{p.CommandsTotal} done"
            : $"Transferring: {p.CommandsDone}/{p.CommandsTotal} commands{list}";
    }

    internal static string FormatPlan(TodoistPlan plan, TodoistTransferState? state)
    {
        static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("Transfer plan");
        if (plan.Account is { } a)
        {
            sb.AppendLine($"  Todoist account: {a.Email ?? "(unknown)"} (plan: {a.PlanName ?? (a.IsPremium ? "premium" : "free")})");
            sb.AppendLine($"  Limits: {(a.MaxProjects is { } mp ? $"{mp} projects, {a.CountedProjects} in use, {plan.ProjectsAvailable} free" : "projects unknown")}; " +
                $"{plan.Settings.MaxTasksPerProject} active tasks per project; reminders {(a.Reminders ? "yes" : "no")}");
        }
        else
        {
            sb.AppendLine("  No Todoist token: account limits and existing projects are not checked.");
            sb.AppendLine($"  Assumed limit: {plan.Settings.MaxTasksPerProject} active tasks per project.");
        }

        sb.AppendLine($"  Completed tasks: {(plan.Settings.IncludeCompleted ? "included, completed in Todoist with their completion date" : "left out")}");
        if (state is not null)
        {
            var (done, total) = TodoistTransfer.CommandProgress(plan, state);
            sb.AppendLine($"  Resuming transfer {state.TransferId}: {N(done)} of {N(total)} commands done{(state.Finished ? " (finished)" : string.Empty)}.");
        }

        sb.AppendLine();
        sb.AppendLine("  List -> Todoist projects");
        foreach (var list in plan.Lists)
        {
            if (list.Skipped is { } why)
            {
                sb.AppendLine($"  {list.ListName}: skipped ({why})");
                continue;
            }

            var projects = list.Projects;
            var counts = $"{TodoistPlanText.Count(projects.Sum(p => p.OpenTasks), "task", "tasks")}, " +
                TodoistPlanText.Count(projects.Sum(p => p.OpenSubTasks), "sub-task", "sub-tasks");
            var done = projects.Sum(p => p.CompletedTasks + p.CompletedSubTasks);
            if (done > 0)
            {
                counts += $", {N(done)} completed";
            }

            var reminders = projects.Sum(p => p.Reminders);
            if (reminders > 0)
            {
                counts += ", " + TodoistPlanText.Count(reminders, "reminder", "reminders");
            }

            sb.AppendLine(projects.Count == 1
                ? $"  {list.ListName} -> \"{projects[0].Name}\": {counts}"
                : $"  {list.ListName} -> {projects.Count} projects ({string.Join(", ", projects.Select(p => $"\"{p.Name}\""))}): {counts}");
        }

        sb.AppendLine($"  New projects needed: {plan.ProjectsNeeded}{(plan.ProjectsAvailable is { } free ? $" (account has room for {free})" : string.Empty)}");

        var lines = TodoistPlanText.LossLines(plan.Losses);
        if (lines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Will not carry over:");
            foreach (var line in lines)
            {
                sb.AppendLine("    " + line);
            }
        }

        foreach (var warning in plan.Warnings)
        {
            sb.AppendLine().AppendLine("  Warning: " + warning);
        }

        foreach (var problem in plan.Problems)
        {
            sb.AppendLine().AppendLine("  Problem: " + problem);
        }

        return sb.ToString();
    }

    /// <summary>What has to be fixed before the transfer can start, for a plan with problems.</summary>
    internal static string FormatFixes(TodoistPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(plan.Problems.Count == 1
            ? "Nothing was written to Todoist. The transfer cannot start until this is fixed:"
            : "Nothing was written to Todoist. The transfer cannot start until these are fixed:");
        foreach (var fix in TodoistPlanText.Fixes(plan, PlanAudience.CommandLine))
        {
            sb.AppendLine("  - " + fix);
        }

        sb.AppendLine("Then run the same command again to see the new plan.");
        return sb.ToString();
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

internal sealed class TransferOptions
{
    public bool Help { get; private set; }

    public string ArchivePath { get; private set; } = string.Empty;

    public string? OutDir { get; private set; }

    public IReadOnlyList<string>? Lists { get; private set; }

    public bool IncludeCompleted { get; private set; }

    public bool AllowDuplicateNames { get; private set; }

    public bool Yes { get; private set; }

    public string? ResumePath { get; private set; }

    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Local;

    public TodoistToken? Token { get; private set; }

    /// <summary>Plan options were given on the command line (ignored when resuming).</summary>
    public bool PlanOptionsGiven { get; private set; }

    public override string ToString() => "TransferOptions";

    public static TransferOptions Parse(string[] args, Func<string, string?> environment)
    {
        var o = new TransferOptions();
        string? token = null, tokenFile = null, archive = null, timezone = null;
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

            void Flag()
            {
                if (inline is not null)
                {
                    throw new UsageException($"{arg} does not take a value");
                }
            }

            switch (arg)
            {
                case "-h":
                case "--help":
                    o.Help = true;
                    return o;
                case "--from-archive":
                    archive = Value();
                    break;
                case "--out":
                    o.OutDir = Value();
                    break;
                case "--lists":
                    o.Lists = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    o.PlanOptionsGiven = true;
                    if (o.Lists.Count == 0)
                    {
                        throw new UsageException("--lists needs at least one list name");
                    }

                    break;
                case "--include-completed":
                    Flag();
                    o.IncludeCompleted = true;
                    o.PlanOptionsGiven = true;
                    break;
                case "--allow-duplicate-names":
                    Flag();
                    o.AllowDuplicateNames = true;
                    o.PlanOptionsGiven = true;
                    break;
                case "--timezone":
                    timezone = Value();
                    o.PlanOptionsGiven = true;
                    break;
                case "--todoist-token":
                    token = Value();
                    break;
                case "--todoist-token-file":
                    tokenFile = Value();
                    break;
                case "--yes":
                    Flag();
                    o.Yes = true;
                    break;
                case "--resume":
                    o.ResumePath = Value();
                    break;
                default:
                    // Never echo a value: it could be a token typed after a misspelled option.
                    throw new UsageException(arg.StartsWith('-')
                        ? $"unknown option '{arg}' for 'transfer'"
                        : "unexpected argument for 'transfer'");
            }
        }

        if (archive is null)
        {
            throw new UsageException("transfer needs --from-archive <archive.json>");
        }

        o.ArchivePath = archive;
        if (token is not null && tokenFile is not null)
        {
            throw new UsageException("use only one of --todoist-token and --todoist-token-file");
        }

        var raw = token;
        if (tokenFile is not null)
        {
            try
            {
                raw = File.ReadAllText(tokenFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UsageException($"cannot read the Todoist token file: {ex.Message}");
            }
        }

        raw ??= environment("TODOIST_TOKEN") is { Length: > 0 } env ? env : null;
        if (raw is not null)
        {
            try
            {
                o.Token = new TodoistToken(raw);
            }
            catch (ArgumentException)
            {
                throw new UsageException("the Todoist token is empty");
            }
        }

        if (o.Yes && o.Token is null)
        {
            throw new UsageException("--yes needs a Todoist token (--todoist-token, --todoist-token-file or TODOIST_TOKEN)");
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

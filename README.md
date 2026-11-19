# todo-exit

todo-exit exports everything in your Microsoft To Do account to files you keep.

Microsoft retires the Microsoft To Do apps on June 30, 2027. Personal Microsoft accounts get no replacement app, and your tasks do not move to another service by themselves. todo-exit signs in with your Microsoft account using read-only access, reads your lists and tasks through Microsoft Graph, and writes them to files on your own device. It also writes a report of what each format could not carry over.

There are two ways to run it:

- a command-line tool (`todo-exit`), which signs you in with your Microsoft account by itself
- a web app that runs in your browser, with no server of its own. The web app will be published at https://milanm.github.io/todo-exit/; until then, run it locally as described below.

## What it exports

| File | What it is |
|---|---|
| `archive.json` | The raw Microsoft Graph data for every list, task, step and attachment record. Nothing is dropped. Keep this file even if you use one of the other formats. |
| `markdown/<list>.md` | One readable Markdown file per list, with checkboxes, steps, notes, links and due dates. |
| `todoist/<list>.csv` | One CSV file per list in Todoist's project import format. Lists with more than 300 tasks are split into `<list> - part 1.csv`, `<list> - part 2.csv` and so on, because Todoist accepts at most 300 tasks per import. |
| `tasks.csv` | One spreadsheet row per task across all lists, open and completed. Saved as UTF-8 with a byte order mark so Excel shows non-English characters correctly. |
| `report.md`, `report.json` | Counts of what was found, and every item a format could not carry over. |

## What is lost in each format

| To Do data | archive.json | Markdown | Todoist CSV | tasks.csv |
|---|---|---|---|---|
| Lists and task titles | kept | kept | kept (one CSV per list) | kept |
| Notes | kept | kept (as plain text) | kept in the description | kept |
| Steps | kept | kept, with checked state | open steps become sub-tasks; checked steps are skipped | kept, with checked state |
| Due dates | kept | kept | kept | kept |
| Repeat rules | kept | written as a phrase | written as a Todoist date phrase; some rules are approximated and reported | written as a phrase |
| Reminders | kept | kept | not carried | kept |
| Start dates | kept | not shown | not carried | kept |
| Importance | kept | high is marked | high becomes priority 1, others priority 4 | kept |
| Categories | kept | kept as `#tags` | kept as `@labels` | kept |
| Completed tasks | kept | kept under "Completed" | skipped by default; `--include-completed` writes them as open tasks | kept |
| Linked resources (for example a flagged email) | kept | kept as links | added to the description | kept |
| Attachments | which tasks have attachments; file name, size and type when Graph allows it (see below) | marked, with names when known | not carried | file names when known |
| List sharing and members | kept as Graph returned it | not carried | not carried | not carried |

Attachment content is never downloaded. If you need an attached file, open the task in To Do and save it before the apps retire.

With read-only access, Microsoft Graph refuses to list attachments on personal accounts (observed in November 2026: it answers 401 accessDenied, although the documentation says `Tasks.Read` is enough). In that case the archive still records which tasks have attachments, but not the file names, and the report says so once. todo-exit does not ask for write access to work around this.

The report lists each loss by format, list and task, so you can fix the important ones by hand.

### When Microsoft Graph cannot return something

Sometimes Graph itself cannot return a task. On one real account, a single corrupt task made every page that contained it come back broken: Graph sent most of the task, failed partway through, and stopped. todo-exit then narrows the page down until only that task is left and reads every other task normally. For the broken task it keeps every property Graph managed to send before it failed, marks the task with `"todoExitPartial": true`, and still reads its steps. The report names the property Graph failed on and the error Graph gave. If Graph failed before sending the task's id, the task is skipped and the report gives its position in the list.

All of this is recorded in a "Could not be read from Microsoft Graph" section at the top of `report.md`, and in `readErrors` in `report.json`. It applies to every format, including the JSON archive. The kinds are:

| Kind | Meaning |
|---|---|
| `task-unreadable` | The task could not be read at all and is missing. The report gives its position in the list. |
| `task-partial` | Graph failed partway through the task. It is kept with the properties Graph sent, and the report names the property that failed. |
| `steps-unreadable` | The task's steps could not be read. |
| `attachments-unreadable` | The list of attachment names could not be read for one task. |
| `attachments-denied` | Graph refused to list attachments with read-only access. Recorded once; tasks with attachments are still marked. |

Kinds that apply to many tasks at once (completed tasks left out, attachments, checked steps, reminders, start dates) are counted per list instead of listed one by one. The exception is the Todoist CSV: reminders and attachments of open tasks are listed by task, because you re-add those by hand. No section of `report.md` lists more than 200 items; the full list is in `report.json`.

## Privacy

- todo-exit asks for one permission: `Tasks.Read`. It can read your tasks. It cannot change or delete them.
- Every request it makes is read-only. Steps come with the task pages. Attachment details (and the extra step check of `--verify-steps`) are read through Graph's batch endpoint (`POST /$batch`), which bundles up to 20 read requests into one call. The batch contains only reads.
- With read-only access, Graph refuses to list attachments on personal accounts (observed November 2026). The archive then records which tasks have attachments but not the file names. todo-exit does not ask for write access to get around this.
- The web app runs entirely in your browser. The only network requests it makes are to Microsoft Graph, from your browser. Your tasks are not sent to any other server.
- The command-line tool runs on your computer and talks only to Microsoft Graph.
- Neither tool stores your sign-in. The CLI keeps the access token in memory only, sends it to `graph.microsoft.com` only, and never prints it or writes it to disk.
- The source code is in this repository, so you can check all of the above.

## Run the command-line tool

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

### 1. Sign in

Run a command without a token and todo-exit signs you in:

```
dotnet run --project src/TodoExit.Cli -- inventory
```

It prints a web address and a short code. Open the address in any browser, enter the code, and sign in with the Microsoft account that holds your tasks. The first time, Microsoft asks you to allow read-only access to your tasks (`Tasks.Read`). The command then continues by itself. The sign-in lasts for the whole run and is renewed in the background, so a long export does not stop halfway.

Options for sign-in:

- `--client-id <guid>` (or the `TODOEXIT_CLIENT_ID` environment variable) uses your own app registration instead of the built-in one. A fork needs its own registration; see [docs/entra-app-registration.md](docs/entra-app-registration.md).
- `--tenant <id>` signs in against `common` by default. Use `consumers` for personal accounts only, `organizations` for work or school accounts only, or a tenant ID or domain.

Advanced: if you already have an access token with `Tasks.Read`, pass it with `--token`, `--token-file` or the `TODOEXIT_TOKEN` environment variable, and the tool skips sign-in. Such a token is not renewed and expires after about an hour.

### 2. See what is in your account

```
dotnet run --project src/TodoExit.Cli -- inventory --timezone Europe/Belgrade
```

### 3. Export

```
dotnet run --project src/TodoExit.Cli -- export --out ./todo-export --timezone Europe/Belgrade
```

The full syntax:

```
todo-exit inventory [--token <t> | --token-file <path> | --from-archive <file>] [--timezone <IANA id>]
                    [--verify-steps] [--resume <file>] [--out <dir>] [--client-id <guid>] [--tenant <id>]
todo-exit export    [--token <t> | --token-file <path> | --from-archive <file>] --out <dir>
                    [--formats json,markdown,todoist,csv] [--include-completed] [--timezone <IANA id>]
                    [--verify-steps] [--resume <file>] [--client-id <guid>] [--tenant <id>]
```

- `--from-archive` reads an `archive.json` written earlier, so you can export again later without signing in.
- `--formats` defaults to all four. `report.md` and `report.json` are always written.
- `--timezone` sets the zone used for due dates and reminders. It defaults to your computer's zone. Use the zone you used To Do in, otherwise due dates can shift by a day.
- `--verify-steps` asks Graph for the steps of every task that came without steps, to double-check that it really has none. It is slow on a large account and normally not needed. If it finds steps anywhere, the summary line and the archive say how many.
- While reading, the tool shows progress on one line, and when it is done it prints how many lists and tasks it read, how many read errors there were, and how long it took.
- Exit codes: 0 on success, 1 for a usage error, 2 when the export fails, 3 when the read was interrupted and a partial archive was saved.

### If the read stops

If the sign-in can no longer be renewed, a token you passed expires, Graph fails for another reason, or you press Ctrl+C, todo-exit saves everything it has read so far to `archive.partial.json` in the `--out` folder (the current folder for `inventory` without `--out`) and exits with code 3. Run the same command with `--resume archive.partial.json`; it asks you to sign in again (or takes a new token). The tool continues where it stopped and does not read finished work again. When it is done, you can delete `archive.partial.json`.

A partial archive also works with `--from-archive`. The report then starts with a note that the archive is incomplete and names the list that was in progress.

### Large accounts

Steps come with the tasks, so the read is mostly paging: an account with about 12,700 tasks exported in about 5 minutes. When Graph asks todo-exit to slow down, it waits and shows the wait on the progress line. Use `--verify-steps` only if you want to double-check the steps; it adds one request per task without steps.

You can also install it as a .NET tool from a local build:

```
dotnet pack src/TodoExit.Cli -c Release -o ./artifacts/nupkg
dotnet tool install --global todo-exit --add-source ./artifacts/nupkg
todo-exit export --from-archive archive.json --out ./todo-export
```

### Import into Todoist

Read `report.md` first so you know which reminders and repeat rules to check afterwards. Then, for each list:

1. In Todoist, create a project for the list and open it.
2. Open the three-dots menu of the project.
3. Choose "Import & export", then "Import from CSV".
4. Select the matching CSV file from the `todoist` folder.

Todoist cannot undo a CSV import. Try one small list first and check the result before you import the rest.

A Todoist project holds at most 300 tasks. A list with more tasks is split into `<list> - part 1.csv`, `<list> - part 2.csv` and so on, and each part must be imported into its own project; name the projects after the files, for example "Work - part 1". The free Todoist plan allows 5 projects, so a large account may need a paid plan or fewer lists. `report.md` lists every split list.

After import, Todoist turns plain web addresses in task names and descriptions into titled links. The address itself is unchanged; this is Todoist's behavior, not a bug in todo-exit.

## Run the web app locally

```
dotnet run --project src/TodoExit.Web
```

Then open the URL it prints. "Try with sample data" works without any setup and loads a fictional account from `wwwroot/sample/sample-archive.json`.

Sign-in uses the app registration whose client ID is in `src/TodoExit.Web/wwwroot/appsettings.json`. Its redirect URIs must include the address you run the app at; see [docs/entra-app-registration.md](docs/entra-app-registration.md). Without a client ID the page hides the sign-in button.

To build the static files for hosting:

```
dotnet publish src/TodoExit.Web -c Release -o ./artifacts/web
```

The site is in `artifacts/web/wwwroot` and can be served by any static file host. The app works under a sub-path as long as `<base href>` in `index.html` names it. The GitHub Pages workflow (`.github/workflows/pages.yml`, started by hand) publishes it at https://milanm.github.io/todo-exit/ and does that rewrite.

## Project layout

```
src/TodoExit.Core         Graph reader, archive format, parser, exporters, loss report (no dependencies)
src/TodoExit.Cli          the todo-exit command-line tool
src/TodoExit.Web          Blazor WebAssembly app, runs in the browser
tests/TodoExit.Core.Tests unit tests for the core library
docs/                     app registration guide
```

The archive is the source of truth. The reader saves raw Graph JSON, and every other format is generated from that file. Properties the parser does not know about stay in the archive unchanged. When something could not be read, the archive has an extra `readNotes` property listing it.

Known Graph behavior: task pages include `checklistItems` only on tasks that have steps, and leave it out on tasks that have none; `$expand=checklistItems` changes nothing. Graph rejects `$select` on To Do tasks with 400 Bad Request when it names `title` or `status`, and ignores it otherwise, so todo-exit never uses it.

## Roadmap

- Phase 2: transfer tasks directly to Todoist through its API, including completed tasks and reminders where the API allows.
- The repeat-rule phrases in `RecurrenceText.cs` and the CSV template were checked against a live Todoist account on 2026-11-19: three imported files (up to 294 tasks) came back with matching sub-tasks, priorities, labels, descriptions and dates, and every phrase imported as a recurring task with the right next date. Rules like "the last Sunday of October" (`relativeYearly`) are still written as `every year` with a note in the report.
- A hosted version of the web app.

## Not affiliated

todo-exit is not affiliated with, endorsed by or sponsored by Microsoft or Doist. Microsoft To Do is a trademark of Microsoft. Todoist is a trademark of Doist.

## License

MIT. See [LICENSE](LICENSE).

# todo-exit

todo-exit exports everything in your Microsoft To Do account to files you keep.

Microsoft retires the Microsoft To Do apps on June 30, 2027. Personal Microsoft accounts get no replacement app, and your tasks do not move to another service by themselves. todo-exit signs in with your Microsoft account using read-only access, reads your lists and tasks through Microsoft Graph, and writes them to files on your own device. It also writes a report of what each format could not carry over.

There are two ways to run it:

- a command-line tool (`todo-exit`), which works today with a token from Graph Explorer
- a web app that runs in your browser, with no server of its own

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
| Attachments | file name, size and type only | file name and size only | not carried | file names only |
| List sharing and members | kept as Graph returned it | not carried | not carried | not carried |

Attachment content is never downloaded. If you need an attached file, open the task in To Do and save it before the apps retire.

The report lists each loss by format, list and task, so you can fix the important ones by hand.

## Privacy

- todo-exit asks for one permission: `Tasks.Read`. It can read your tasks. It cannot change or delete them.
- The web app runs entirely in your browser. The only network requests it makes are to Microsoft Graph, from your browser. Your tasks are not sent to any other server.
- The command-line tool runs on your computer and talks only to Microsoft Graph.
- Neither tool stores your token. The CLI never prints it.
- The source code is in this repository, so you can check all of the above.

## Run the command-line tool

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

### 1. Get a token from Graph Explorer

Until the web app has a registered sign-in, the simplest way to get a read-only token is Microsoft's Graph Explorer:

1. Open https://developer.microsoft.com/graph/graph-explorer and sign in with the Microsoft account that holds your tasks.
2. Open "Modify permissions", find `Tasks.Read` and consent to it.
3. Run the query `GET https://graph.microsoft.com/v1.0/me/todo/lists` once to check that it works.
4. Open the "Access token" tab and copy the token.

The token expires after about an hour. Treat it like a password: do not paste it into chats, issues or shared files.

Save the token to a file, for example `token.txt`, or put it in the `TODOEXIT_TOKEN` environment variable.

### 2. See what is in your account

```
dotnet run --project src/TodoExit.Cli -- inventory --token-file token.txt --timezone Europe/Belgrade
```

### 3. Export

```
dotnet run --project src/TodoExit.Cli -- export --token-file token.txt --out ./todo-export --timezone Europe/Belgrade
```

The full syntax:

```
todo-exit inventory (--token <t> | --token-file <path> | --from-archive <file>) [--timezone <IANA id>]
todo-exit export    (--token <t> | --token-file <path> | --from-archive <file>) --out <dir>
                    [--formats json,markdown,todoist,csv] [--include-completed] [--timezone <IANA id>]
```

- `--from-archive` reads an `archive.json` written earlier, so you can export again later without signing in.
- `--formats` defaults to all four. `report.md` and `report.json` are always written.
- `--timezone` sets the zone used for due dates and reminders. It defaults to your computer's zone. Use the zone you used To Do in, otherwise due dates can shift by a day.
- Exit codes: 0 on success, 1 for a usage error, 2 when the export fails.

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

## Run the web app locally

```
dotnet run --project src/TodoExit.Web
```

Then open the URL it prints. "Try with sample data" works without any setup and loads a fictional account from `wwwroot/sample/sample-archive.json`.

Sign-in needs an app registration in Microsoft Entra. Until `src/TodoExit.Web/wwwroot/appsettings.json` holds a real client ID, the page hides the sign-in button. The steps are in [docs/entra-app-registration.md](docs/entra-app-registration.md).

To build the static files for hosting:

```
dotnet publish src/TodoExit.Web -c Release -o ./artifacts/web
```

The site is in `artifacts/web/wwwroot` and can be served by any static file host.

## Project layout

```
src/TodoExit.Core         Graph reader, archive format, parser, exporters, loss report (no dependencies)
src/TodoExit.Cli          the todo-exit command-line tool
src/TodoExit.Web          Blazor WebAssembly app, runs in the browser
tests/TodoExit.Core.Tests unit tests for the core library
docs/                     app registration guide
```

The archive is the source of truth. The reader saves raw Graph JSON, and every other format is generated from that file. Properties the parser does not know about stay in the archive unchanged.

## Roadmap

- Phase 2: transfer tasks directly to Todoist through its API, including completed tasks and reminders where the API allows.
- Check the repeat-rule phrases and the CSV template against a live Todoist import. The phrases in `RecurrenceText.cs` are based on Todoist's date syntax and are untested against a real account.
- A hosted version of the web app.

## Not affiliated

todo-exit is not affiliated with, endorsed by or sponsored by Microsoft or Doist. Microsoft To Do is a trademark of Microsoft. Todoist is a trademark of Doist.

## License

MIT. See [LICENSE](LICENSE).

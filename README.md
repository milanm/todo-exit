# todo-exit

Export everything in your Microsoft To Do account to files you keep.

Microsoft retires the Microsoft To Do apps on June 30, 2027. Personal Microsoft accounts get no replacement app, and your tasks do not move to another service by themselves. todo-exit reads your lists and tasks with read-only access and writes them to your own device.

**Use it in your browser: https://milanm.github.io/todo-exit/**

Nothing to install. There is also a [command-line tool](#command-line-tool).

## What you get

| File | What it is |
|---|---|
| `archive.json` | Everything Microsoft Graph returned, unchanged. Keep it even if you use another format. |
| `markdown/<list>.md` | One readable file per list, with steps, notes, links and due dates. |
| `todoist/<list>.csv` | Todoist's import format, one file per list. |
| `tasks.csv` | One row per task, for Excel or any spreadsheet. |
| `report.md` | What was found, and what each format could not carry over. |

## What does not carry over

The JSON archive keeps everything. The other formats lose some things, and the report lists them by list and task.

- **Todoist CSV:** reminders, start dates, attachments and checked steps are not carried. Completed tasks are skipped unless you include them. Some repeat rules are approximated.
- **Attachments:** files are never downloaded. Save the ones you need from To Do before the apps retire. On personal accounts Graph also refuses to list attachment names with read-only access, so the export marks which tasks have attachments and nothing more.
- **Tasks Graph cannot return:** on the test account, 18 of about 12,700 tasks came back broken. todo-exit keeps what Graph sent for them and names them in the report.

The full table is in [docs/formats.md](docs/formats.md).

## Privacy

- todo-exit asks for one permission to your data: `Tasks.Read`. It can read your tasks. It cannot change or delete them.
- Microsoft's consent screen also lists the standard sign-in items (basic profile, keeping access). todo-exit uses them to sign you in and to renew the sign-in during a long read.
- The web app runs in your browser and has no server. Its Content Security Policy makes the browser block requests to any host except the site itself, Microsoft's sign-in service, Microsoft Graph and the Todoist API.
- Your tasks go to Todoist only if you choose that step, with the Todoist API token you paste. The token stays in memory and is sent to `api.todoist.com` only.
- The command-line tool keeps your sign-in in memory only. The web app keeps it in the browser tab until you sign out or close the tab.
- Sign out in the web app also signs your Microsoft account out of that browser.

## Command-line tool

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
git clone https://github.com/milanm/todo-exit
cd todo-exit
dotnet run --project src/TodoExit.Cli -- export --out ./todo-export
```

The tool prints a web address and a short code. Open the address, enter the code and sign in. The export then continues by itself. About 12,700 tasks take a little over 3 minutes.

If the read stops, the tool saves what it has and you continue with `--resume`. All options are in [docs/cli.md](docs/cli.md).

## Send your tasks to Todoist

todo-exit can create the projects and tasks in your Todoist account through the Todoist API. In the web app it is step 5. In the command-line tool:

```
dotnet run --project src/TodoExit.Cli -- transfer --from-archive ./todo-export/archive.json --todoist-token-file token.txt
```

You need your Todoist API token, from Todoist under Settings, Integrations, Developer.

- It shows a plan first and writes nothing until you confirm (`--yes` in the command-line tool).
- It creates one project per list and never changes or deletes anything that is already in your account.
- If it is interrupted, it continues without creating duplicates.
- Completed tasks are optional. They keep their completion date, and take about 15 seconds per 100 items.

Details are in [docs/cli.md](docs/cli.md#transfer-to-todoist).

## Or import the CSV files by hand

Read `report.md` first, so you know which reminders and repeat rules to check afterwards. Then, for each list:

1. In Todoist, create a project for the list and open it.
2. Open the three-dots menu of the project.
3. Choose "Import & export", then "Import from CSV".
4. Select the matching file from the `todoist` folder.

Todoist cannot undo an import, so try one small list first. A Todoist project holds at most 300 tasks. Longer lists are split into `part` files, and each part goes into its own project. The free Todoist plan allows 5 projects.

## Status

Tested on one personal Microsoft account with 14 lists and about 12,700 tasks. The command-line tool on Windows and the hosted web app produced the same files. Three Todoist CSV files were imported into Todoist and read back, and they matched.

The transfer through the Todoist API was tested with the command-line tool on a free Todoist account, including an interrupted transfer that was continued. Each task and sub-task arrived once.

Not tested yet: work and school Microsoft accounts, and Todoist reminders on a paid plan.

Planned next: ready-made executables, so the command-line tool needs no SDK.

## More

- [docs/formats.md](docs/formats.md): every file, what each format loses, and how read errors are reported
- [docs/cli.md](docs/cli.md): sign-in options, all flags, resume, exit codes
- [docs/development.md](docs/development.md): project layout, running the web app locally, test status, Microsoft Graph behavior
- [docs/entra-app-registration.md](docs/entra-app-registration.md): app registration for a fork

## Not affiliated

todo-exit is not affiliated with, endorsed by or sponsored by Microsoft or Doist. Microsoft To Do is a trademark of Microsoft. Todoist is a trademark of Doist.

## License

MIT. See [LICENSE](LICENSE).

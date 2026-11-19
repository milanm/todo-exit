# Development notes

## Project layout

```
src/TodoExit.Core         Graph reader, archive format, parser, exporters, loss report (no dependencies)
src/TodoExit.Cli          the todo-exit command-line tool
src/TodoExit.Web          Blazor WebAssembly app, runs in the browser
tests/TodoExit.Core.Tests unit tests for the core library
docs/                     reference docs
```

The archive is the source of truth. The reader saves raw Graph JSON, and every other format is generated from that file. Properties the parser does not know about stay in the archive unchanged. When something could not be read, the archive has an extra `readNotes` property listing it.

## Run the web app locally

```
dotnet run --project src/TodoExit.Web
```

Then open the URL it prints. Hot reload under `dotnet watch` does not work, because the Content Security Policy blocks its websocket. "Try with sample data" works without any setup and loads a fictional account from `wwwroot/sample/sample-archive.json`.

Sign-in uses the app registration whose client ID is in `src/TodoExit.Web/wwwroot/appsettings.json`. Its redirect URIs must include the address you run the app at; see [entra-app-registration.md](entra-app-registration.md). Without a client ID the page hides the sign-in button.

To build the static files for hosting:

```
dotnet publish src/TodoExit.Web -c Release -o ./artifacts/web
```

The site is in `artifacts/web/wwwroot` and can be served by any static file host. The app works under a sub-path as long as `<base href>` in `index.html` names it. The GitHub Pages workflow (`.github/workflows/pages.yml`, started by hand) publishes it at https://milanm.github.io/todo-exit/ and does that rewrite.

## How the read works

- Every request is read-only. Steps come with the task pages. Attachment details (and the extra step check of `--verify-steps`) are read through Graph's batch endpoint (`POST /$batch`), which bundles up to 20 read requests into one call. The batch contains only reads.
- Signing in uses the standard sign-in scopes (`openid`, `profile`, `offline_access`) next to `Tasks.Read`. `offline_access` lets a long read renew its token.
- The command-line tool sends the access token to `graph.microsoft.com` only, and never prints it or writes it to disk.
- The web app keeps the sign-in in the browser tab's session storage, which is how Microsoft's sign-in library works.

## What has been tested

As of 2026-11-19:

- One personal Microsoft account with 14 lists and about 12,700 tasks, read by the command-line tool on Windows, by the web app run locally and by the hosted web app. All three gave the same counts, and the web app's zip matched the command-line export file for file (apart from the export time and Windows line endings in the two JSON files).
- The hosted web app, with its Content Security Policy on: a full read made 475 requests, all to `graph.microsoft.com`, with no policy violations. Loading and sign-in also contact `milanm.github.io` and `login.microsoftonline.com`.
- Sign out on the hosted web app with a personal account: it signs out, but stays on Microsoft's "You signed out" page even though the logout redirect URI is registered.
- The Todoist CSV files: three of them (49, 101 and 294 tasks) were imported into a free Todoist account and read back through Todoist's API. Sub-tasks, priorities, labels, descriptions and dates matched.
- The repeat-rule phrases in `RecurrenceText.cs`: Todoist accepted every phrase the tool can write as a recurring task with the right next date.
- Transfer through the Todoist API, with the command-line tool on a free Todoist account: a plan against the real account, a real transfer read back and compared with the archive (20 tasks, 81 sub-tasks), refusal on an existing project name and on too few free projects, completed tasks and sub-tasks with past completion dates, and a transfer interrupted while a 100-command request was in flight and then continued. Every item arrived once, also after Todoist had rewritten plain web addresses into titled links.
- The web app's Todoist step: in headless Chrome against a simulated Todoist API (plan, transfer, cancel and continue, rejected token, redirect not followed, token found in no storage, page text or downloaded file). On the hosted site against the real Todoist API: one list of the sample data (6 tasks), read back from Todoist with the right priority, labels, notes, due dates and repeat rules.
- An external code review on 2026-11-19 found nine problems. All nine are fixed, each with a regression test built from the reviewer's scenario:
  - an outage during the read could produce an archive that looked complete
  - list names could make exported files overwrite each other
  - a Todoist transfer was not bound to the account of its plan
  - a new archive in the web app could drop a running transfer
  - partly read tasks lost their steps (on the test account, 5 of the 18 partly read tasks turned out to have steps)
  - a temporary error of one Todoist command ended the transfer as finished
  - id recovery after a lost answer trusted a single full sync, which can lag
  - a resumed read continued at a stored position and could miss tasks when the list had changed
  - team projects counted against the personal Todoist project limit
- The outage fix was also run against a separate simulated Graph in eight failure scenarios, and the new reader read the test account from the hosted site with the same counts as before.
- Checked on the real Todoist API: an incremental sync returns new, completed and deleted tasks; a sync token works when more resource types are added later; an unknown token gives a full sync; a sub-task whose parent does not exist is refused with PARENT_NOT_FOUND.
- The unit tests run on Linux and Windows on every push.

Not tested yet: work and school accounts, Todoist reminders on a paid plan, a transfer into more than one free project at once on the real API, browsers that are not based on Chromium, and `--resume` against the real Graph (it is covered by tests against a fake one).

### Microsoft Graph behavior observed

These are observations from that one account in November 2026, and they shaped the reader:

- Task pages include `checklistItems` only on tasks that have steps. `$expand=checklistItems` changes nothing.
- Graph rejects `$select` on To Do tasks with 400 Bad Request when it names `title` or `status`, and ignores it otherwise, so todo-exit never uses it.
- Graph answers 401 accessDenied for attachment lists with a `Tasks.Read` token.
- 18 tasks failed with InternalServerError while Graph was writing `linkedResources`, and no task with linked resources came back whole. That suggests Graph fails on every task that has them, but one account cannot prove it.
- Several batch requests in parallel are throttled. One batch already uses the four concurrent requests an account is allowed.

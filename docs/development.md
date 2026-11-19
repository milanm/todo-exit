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
- The unit tests run on Linux and Windows on every push.

Not tested yet: work and school accounts, browsers that are not based on Chromium, and `--resume` against the real Graph (it is covered by tests against a fake one).

### Microsoft Graph behavior observed

These are observations from that one account in November 2026, and they shaped the reader:

- Task pages include `checklistItems` only on tasks that have steps. `$expand=checklistItems` changes nothing.
- Graph rejects `$select` on To Do tasks with 400 Bad Request when it names `title` or `status`, and ignores it otherwise, so todo-exit never uses it.
- Graph answers 401 accessDenied for attachment lists with a `Tasks.Read` token.
- 18 tasks failed with InternalServerError while Graph was writing `linkedResources`, and no task with linked resources came back whole. That suggests Graph fails on every task that has them, but one account cannot prove it.
- Several batch requests in parallel are throttled. One batch already uses the four concurrent requests an account is allowed.

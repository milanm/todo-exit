# The command-line tool

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

## 1. Sign in

Run a command without a token and todo-exit signs you in:

```
dotnet run --project src/TodoExit.Cli -- inventory
```

It prints a web address and a short code. Open the address in any browser, enter the code, and sign in with the Microsoft account that holds your tasks. The first time, Microsoft asks you to allow read-only access to your tasks (`Tasks.Read`). The command then continues by itself. The sign-in lasts for the whole run and is renewed in the background, so a long export does not stop halfway.

Options for sign-in:

- `--client-id <guid>` (or the `TODOEXIT_CLIENT_ID` environment variable) uses your own app registration instead of the built-in one. A fork needs its own registration; see [entra-app-registration.md](entra-app-registration.md).
- `--tenant <id>` signs in against `common` by default. Use `consumers` for personal accounts only, `organizations` for work or school accounts only, or a tenant ID or domain.

Advanced: if you already have an access token with `Tasks.Read`, pass it with `--token`, `--token-file` or the `TODOEXIT_TOKEN` environment variable, and the tool skips sign-in. Such a token is not renewed and expires after about an hour.

## 2. See what is in your account

```
dotnet run --project src/TodoExit.Cli -- inventory --timezone Europe/Belgrade
```

## 3. Export

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
- `--verify-steps` asks Graph for the steps of every task that came without steps, to double-check that it has none. It is slow on a large account and normally not needed. If it finds steps anywhere, the summary line and the archive say how many.
- While reading, the tool shows progress on one line, and when it is done it prints how many lists and tasks it read, how many read errors there were, and how long it took.
- Exit codes: 0 on success, 1 for a usage error, 2 when the export fails, 3 when the read was interrupted and a partial archive was saved.

## If the read stops

If the sign-in can no longer be renewed, a token you passed expires, Graph fails for another reason, or you press Ctrl+C, todo-exit saves everything it has read so far to `archive.partial.json` in the `--out` folder (the current folder for `inventory` without `--out`) and exits with code 3. Run the same command with `--resume archive.partial.json`; it asks you to sign in again (or takes a new token). The tool continues where it stopped and does not read finished work again. When it is done, you can delete `archive.partial.json`.

A partial archive also works with `--from-archive`. The report then starts with a note that the archive is incomplete and names the list that was in progress.

## Large accounts

Steps come with the tasks, so the read is mostly paging: an account with about 12,700 tasks exported in a little over 3 minutes. When Graph asks todo-exit to slow down, it waits and shows the wait on the progress line. Use `--verify-steps` only if you want to double-check the steps; it adds one request per task without steps.

## Install as a .NET tool

You can also install it as a .NET tool from a local build:

```
dotnet pack src/TodoExit.Cli -c Release -o ./artifacts/nupkg
dotnet tool install --global todo-exit --add-source ./artifacts/nupkg
todo-exit export --from-archive archive.json --out ./todo-export
```

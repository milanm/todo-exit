# What todo-exit exports and what is lost

## Files

| File | What it is |
|---|---|
| `archive.json` | The raw Microsoft Graph data for every list, task, step and attachment record. Nothing Graph returned is dropped. Keep this file even if you use one of the other formats. |
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
| Linked resources (for example a flagged email) | kept when Graph returns them (see below) | kept as links | added to the description | kept |
| Attachments | which tasks have attachments; file name, size and type when Graph allows it (see below) | marked, with names when known | not carried | file names when known |
| List sharing and members | kept as Graph returned it | not carried | not carried | not carried |

Attachment content is never downloaded. If you need an attached file, open the task in To Do and save it before the apps retire.

With read-only access, Microsoft Graph refuses to list attachments on personal accounts (observed in November 2026: it answers 401 accessDenied, although the documentation says `Tasks.Read` is enough). In that case the archive still records which tasks have attachments, but not the file names, and the report says so once. todo-exit does not ask for write access to work around this.

The report lists each loss by format, list and task, so you can fix the important ones by hand.

### When Microsoft Graph cannot return something

Sometimes Graph itself cannot return a task. On one real account with about 12,700 tasks, 18 came back broken, all at the `linkedResources` property: Graph sent most of each task, failed partway through, and stopped. Every page that contained one of them was invalid JSON. todo-exit narrows such a page down until only the broken task is left and reads every other task normally. For the broken task it keeps every property Graph managed to send before it failed, marks the task with `"todoExitPartial": true`, and keeps its steps. The report names the property Graph failed on and the error Graph gave. If Graph failed before sending the task's id, the task is skipped and the report gives its position in the list.

All of this is recorded in a "Could not be read from Microsoft Graph" section at the top of `report.md`, and in `readErrors` in `report.json`. It applies to every format, including the JSON archive. The kinds are:

| Kind | Meaning |
|---|---|
| `task-unreadable` | The task could not be read at all and is missing. The report gives its position in the list. |
| `task-partial` | Graph failed partway through the task. It is kept with the properties Graph sent, and the report names the property that failed. |
| `steps-unreadable` | The task's steps could not be read. |
| `attachments-unreadable` | The list of attachment names could not be read for one task. |
| `attachments-denied` | Graph refused to list attachments with read-only access. Recorded once; tasks with attachments are still marked. |

Kinds that apply to many tasks at once (completed tasks left out, attachments, checked steps, reminders, start dates) are counted per list instead of listed one by one. The exception is the Todoist CSV: reminders and attachments of open tasks are listed by task, because you re-add those by hand. No section of `report.md` lists more than 200 items; the full list is in `report.json`.

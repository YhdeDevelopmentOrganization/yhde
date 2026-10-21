# ADR 0017: Statistics Read Summaries, Not the Log

- Status: Built
- Date: 2026-10-21
- See also: [admin.md](../admin.md), [database.md](../database.md),
  [ADR 0001](0001-operation-log.md)

## Situation

The admin page and the dashboard count changes and add up file sizes. Both
were worked out from the operation log on every refresh:

- The admin Overview (every 15 s) copied the whole log, payloads included,
  and scanned it four times per project. With 31 projects that is the whole
  log over a hundred times a minute while the page is open.
- The admin statistics rebuilt every 30 s with about twelve queries, among
  them a count of every operation ever and every file operation ever.
- The dashboard, the Godot projects panel and the storage check before an
  upload ran the same per-project query.

On a 2-core server this made the admin page slow to open and laggy to use,
and the cost grew with every change anyone made. The admin page's CPU
figure only covers the server process, so the database's share never showed.

## Decision

1. Two tables summarise the log, filled by a trigger on `operations` in the
   same transaction as each insert (migration 018):
   - `asset_heads`: the latest asset operation of each file on each branch
     (path, hash, size, type). What a project holds now.
   - `op_hourly`: changes per hour, project, connection and operation type,
     with the time of the last one.
2. Statistics read only these tables (and `session_log`), never scan the log.
   The one exception is the last hour in 5-minute steps on the In the editor
   page, which reads that hour of the log through its time index.
3. The trigger never raises on a payload it does not understand: a size that
   is not a whole number counts as 0. It uses no exception blocks, which
   would cost a subtransaction per operation on large imports.
4. The migration fills both tables from the existing log once. Migrations
   may run for up to 30 minutes so this cannot stop a small server starting.
5. Changes made in the editor are counted apart from changes the server made
   itself, such as a project imported from a zip: an operation whose
   connection has no `session_log` row is not someone's work.
6. The admin statistics come in three parts (health, activity, storage). A
   part that is out of date is answered from its last result while a new one
   is built in the background.

## Why

- The answers are small (a row per file, a row per active hour) while the
  log only grows. Reading summaries keeps every refresh the same cost however
  long a project has been worked on.
- A trigger keeps the numbers exact and in step with the log without
  touching each place that adds operations (edits, undo, imports, branch
  commits). Storage limits depend on these numbers, so a few seconds of lag
  from a background job was not acceptable.
- The log stays as ADR 0001 describes: nothing in it is changed or removed.
  The summaries can always be rebuilt from it.

## Consequences

- Every insert into `operations` does one more small upsert, two for file
  operations. That is far less than the scans it replaces.
- Deleting a project removes its summaries through foreign keys.
- New statistics should add to these summaries (or a new one) rather than
  read the log on a page refresh.

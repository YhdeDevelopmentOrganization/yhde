# ADR 0018: Deletions Need Proof; File Changes Name What They Replace

- Status: Built
- Date: 2026-11-10
- See also: [assets.md](../assets.md) §5, [reliability.md](../reliability.md) §6,
  [conflict_resolution.md](../conflict_resolution.md), [ADR 0005](0005-asset-versioning.md)

## Situation

Two people adding add-ons on their first day lost project files: the files
one of them had just added were deleted for both.

An editor that could not write a teammate's file (a folder it may not write,
a full disk, a locked file, a name Windows cannot store) only printed "Could
not write". The file was in its picture of the log but not on its disk, and
the next scan, a second later, took that for a deletion and sent
`DeleteAsset` to everyone. Below 20 files nothing asked first.

The same failure on a file it already had left the old bytes on disk. As
soon as something touched that file, the editor sent them back as an
`UpdateAsset`, and the server accepted it over the teammate's newer version.

## Decision

1. A file counts as deleted on an editor only when that editor had it with
   the log's bytes (or made it itself) and it then disappeared. A file it
   never managed to write is "not written here": it is kept in the editor's
   record with the reason, tried again with a growing delay and whenever the
   editor finishes a scan or import, shown in the panel, and never sent from
   that editor until the log's version is written or the person chooses
   "Keep mine".
2. A file the log has but that is not on disk, without that proof, is fetched
   again.
3. A deletion that would empty a whole add-on, or a top-level folder of more
   than one file, waits for a person to confirm, whatever the count.
4. The server checks what a file change replaces. `UpdateAsset`,
   `DeleteAsset` and `MoveAsset` name the bytes they replace (`o`); when
   that is not what the path holds now, and when `RegisterAsset` would
   replace another file at a taken path, the server refuses it with
   `Outdated`. The editor keeps its copy in the replaced files folder and
   takes the current version.
5. Add-ons older than 0.7.4 may sign in and be told to update, but their
   edits are refused (`UpdateNeeded`).
6. The server and the editor refuse the same names: also `< > " | ? *` and
   the Windows device names (`CON`, `NUL`, `COM1`...) with any extension.

## Why

- A missing file is ambiguous; only the editor that held it can say it was
  removed. Everything else is a local problem and must stay local.
- A local bug cannot be ruled out in every editor, so the server must not
  depend on it: naming the replaced bytes lets the server refuse a stale
  change from any editor, whatever went wrong there.
- An old add-on would keep deleting files it failed to write.

## Consequences

- Two people changing the same file at the same moment: the second is
  refused, keeps a copy of their version and gets the first. Before, the
  second silently replaced the first.
- A file can stay "not written here" for a long time (a read-only folder).
  The panel says so; nothing is lost on either side.
- The editor's file record (`*.assets.json`) is version 2. Version 1 is read; an
  older add-on cannot read version 2 and treats the folder as new.
- Everyone has to update to 0.7.4 to keep editing.

## Alternatives

- Ask before every deletion: safe, but a person who deletes a file would be
  asked every time, and the wrong deletions still look like real ones.
- Retry the write a few times, then give up: a full disk or a read-only
  folder outlasts any retry, and giving up brings the deletion back.
- Only check on the server: the server cannot know an editor's write failed;
  the deletion it receives names the right bytes.

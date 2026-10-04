# Authority

Who may do what in a project. Signing in (proving who you are) is in
[authentication.md](authentication.md). The original decision is
[ADR 0004](adr/0004-authority-system.md); what is built today is simpler than
that plan (section 3).

## 1. Where It Is Checked

The server checks every operation after it knows who sent it and before the
operation is validated and stored:

```
connection admitted -> may this person edit this project? -> validate -> commit
                               | no
                               v
                          OpRejected, to the sender only
```

The add-on hides or disables what a person can't do, but that is only for
convenience. The server checks again, so a modified client gains nothing
([security.md](security.md)).

## 2. What Is Built

Roles belong to each project ([teams.md](teams.md) §5,
[ADR 0016](adr/0016-people-belong-to-projects.md)):

| In a project | May |
|---|---|
| Owner | Everything about it: who is in it, their access, view links, rename, archive, delete, hand it over. Always edits. |
| Member, can edit | Work in it. Leave it. |
| Member, can view | Sees everything, chats and comments. Every change (operations, undo) is refused. |
| Viewer (view link) | Watches in Godot. Every change is refused. |

Access is stored in `project_members.access`. Someone who isn't the owner or
a member doesn't see the project, and their connection is refused. Lowering
someone's access, removing them or them leaving closes their open
connections.

A connection opened with an invite code reaches only that project; a code
made by a view link is read-only. The server access key reaches every project
and is meant only for the operator.

## 3. Planned: Finer Roles and Locks

[ADR 0004](adr/0004-authority-system.md) describes more: roles such as
Developer, Artist, QA and Viewer mapped to kinds of operations, and locks on
single nodes or subtrees:

| Lock | Meaning |
|---|---|
| Soft | "I'm working here." Others are warned but not stopped. |
| Hard | Only the holder may change the target; other operations on it are refused. |

Suggested defaults: soft for transforms and properties, hard for hierarchy
changes and resource deletion. Locks would have a lease and be released when
the holder disconnects, so a crashed editor can't freeze a node. Every lock
change and every refusal would go to the audit log.

None of this is built yet. Until then the order of operations decides clashes
([conflict_resolution.md](conflict_resolution.md)).

## 4. Rules

1. The server checks permission on every operation. Client checks are only
   for the UI.
2. Only a project's owner changes who is in it and what they may do.
3. Losing access takes effect at once: open connections are closed.

## See Also

- [teams.md](teams.md), [authentication.md](authentication.md),
  [conflict_resolution.md](conflict_resolution.md), [security.md](security.md)
- [ADR 0016](adr/0016-people-belong-to-projects.md): people belong to projects, not teams

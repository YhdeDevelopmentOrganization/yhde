# Chat and Comments

How people talk about the project inside YHDE: the project chat, direct
messages and comments pinned in scenes. Why they live outside the operation
log is in [ADR 0008](adr/0008-social-channel.md).

## 1. What It Is

| Feature | Scope | Who sees it |
|---|---|---|
| Project chat | One channel per project | Everyone connected to the project (any branch) |
| Direct messages | Between two members | Only those two, on every session they have |
| Comments | Threads pinned in a scene | Everyone on the branch |

All three are stored, so people who were away read them when they join. None
of it is project state: it never enters the operation log, and undo, replay
and rollback never touch it.

## 2. Who Wrote It

Everything is stamped with a member id by the server, never taken from a
request. With an editor sign-in, the member id and name are the account's
([authentication.md](authentication.md)). Presence entries carry the member
id too, so the panel can offer "Message ..." for people who are online.

Connections made with an invite code or the server key have no account. For
those, each project folder makes a random member id once and keeps it in its
editor data (`.godot/editor/yhde/member_id`, never shared), and `Hello`
carries it with the display name. Such ids are self-declared (section 6).

## 3. Wire Format

Channel `social` (`0x04`), [network_protocol.md](network_protocol.md) §4:

| Message | Dir | Body |
|---|---|---|
| `SocialRequest` (`0x40`) | C->S | `[request_id, kind, body_json]` |
| `SocialEvent` (`0x41`) | S->C | `[kind, body_json, request_id]` (`request_id` only on the requester's copy) |

Requests:

| Kind | Body | Answer |
|---|---|---|
| `chat.send` | `{body, to?: member}` | `chat.message` to recipients |
| `chat.history` | `{with?: member, before?: ms}` | `chat.history {with, messages, more}` to the requester |
| `comment.create` | `{scene, node?: uuid, path, anchor, body}` | `comment.thread` to the branch |
| `comment.reply` | `{thread, body}` | `comment.thread` (reopens a resolved thread) |
| `comment.resolve` | `{thread, resolved}` | `comment.thread` |
| `comment.edit` | `{message, body}` (author only) | `comment.thread` |
| `comment.delete` | `{message}` (author only) | `comment.thread` |

On subscribe the server sends `chat.recent` (the last 100 project messages and
the member's last 100 direct messages) and `comment.threads` (open threads,
plus threads resolved in the last 30 days). A refused request is answered with
`error {message, kind}` to the requester only.

## 4. Comment Anchors

`anchor` is `{"space":"2d","x","y"}` or `{"space":"3d","x","y","z"}`. With a
`node` (its YHDE UUID) the point is in that node's local space, so the pin
follows the node wherever anyone moves it. Without one it is a point in the
scene. The server rebuilds the anchor from validated numbers and never echoes
client JSON.

In the editor, press C (or the comment button in the top bar) and click.
In 2D the comment attaches to the node under the cursor (the selected node
wins). In 3D it attaches to the selected node, at its depth. Pins show the
thread's number in the author's color. Clicking a pin opens the thread;
clicking a thread in the Comments tab brings its scene forward, selects its
node and opens it.

## 5. Storage

Tables from migration `002_social.sql`: `chat_messages`, `comment_threads`,
`comment_messages` ([database.md](database.md)). Nothing is hard-deleted: a
removed comment keeps its row with `deleted_at` and is hidden. Every edit and
removal writes the previous text to `audit_log` (`comment.edit`,
`comment.delete`).

## 6. Security

- Every field is validated and bounded (text ≤ 4,000 characters, control
  characters dropped, paths `res://…`, anchors finite). Each session is rate
  limited (5 requests/s, burst 20).
- Only a comment's author may edit or remove it. Anyone on the branch may
  resolve or reopen.
- A thread of another branch cannot be read or changed.
- For connections without an account (invite codes, the server key), the
  member id is self-declared, so a modified editor could claim someone else's
  id. Direct messages are only private for signed-in people.

## 7. Unread and Notifications

Read marks are per person and per project, kept in the editor's project
metadata. The Chat and Comments tabs show unread counts. A direct message, an
`@name` or `@everyone` mention in chat or in a comment, and "bring everyone
here" show a notification. Each can be turned off in the panel's ⋮ menu
under Notify me about.

## See Also

- [network_protocol.md](network_protocol.md), [presence.md](presence.md),
  [authentication.md](authentication.md), [editor_client.md](editor_client.md),
  [ADR 0008](adr/0008-social-channel.md)

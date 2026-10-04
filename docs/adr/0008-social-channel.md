# ADR 0008: Chat and Comments Outside the Operation Log

- Status: Built
- Date: 2026-09-25
- See also: [social.md](../social.md), [network_protocol.md](../network_protocol.md),
  [presence.md](../presence.md), [security.md](../security.md),
  [ADR 0001](0001-operation-log.md)

## Situation

A team editing one project live needs to talk about it: a project chat, direct
messages, and Figma-style comments pinned to things in a scene ("this light is
too bright"). Unlike presence, these must be stored: people who were away need
to read them later.

The operation log is where stored, ordered, auditable things go in YHDE, so it
is the obvious first candidate. But the log is the *project's* history. Every
operation is replayed by every editor, can be undone, and is part of what a
branch or a rollback means. A chat line or a comment is not project state.
Undoing a node move must not delete the comment someone wrote about it, and
rolling a branch back must not remove the conversation about why. Direct
messages must also reach only two people, while the log is broadcast to
everyone on the branch.

## Decision

1. Chat and comments travel on a separate social channel (`0x04`) with two
   messages: `SocialRequest` (C->S) and `SocialEvent` (S->C). Their bodies are
   JSON, with a kind such as `chat.send` or `comment.reply`. The server
   validates every kind and field.
2. They are stored in their own tables (`chat_messages`, `comment_threads`,
   `comment_messages`), not in `operations`. They never enter the log, and
   undo, replay and rollback never touch them.
3. Chat is per project: one channel plus direct messages. Comments are per
   branch, like the scenes they talk about.
4. A comment is anchored to a node by its YHDE UUID and a point in that node's
   local space, or to a point in the scene. The pin follows the node wherever
   anyone moves it.
5. Nothing is hard-deleted. A removed comment keeps its row (`deleted_at`),
   and every edit or removal writes the previous text to `audit_log`.
6. Until accounts exist, the author is a member: a random id stored once
   per project folder and sent in `Hello`. The server stamps it from the
   session, never from a request.

## Why

- Keeping project state and conversation apart keeps the log's guarantees
  (replay, rollback, deterministic convergence) meaningful and cheap. The rule
  "everything is an immutable operation" is about the project's state.
- A separate channel gives direct messages real addressing: the server sends a
  DM only to the two members' sessions.
- Storing rows (with soft delete and audit entries) keeps it auditable without
  making it replayable.
- JSON bodies behind one pair of message types let chat and comments grow new
  kinds without protocol changes, while the server stays the validator
  ("never trust clients").

## Consequences

- Gains: comments survive undo and rollback; people who were away catch
  up on join; DMs are not broadcast.
- Cost: a second storage path with its own tests; no branch
  semantics for chat (merging a branch does not merge its comments).
- Cost: before login, member ids are self-declared. A modified
  editor with the access key could claim another member's id and read their
  direct messages. The access key stays the trust boundary until accounts
  ([authentication.md](../authentication.md)) replace member ids with account ids.
- Later: accounts, then per-member permissions (who may resolve or
  remove); notifications outside the editor; comment search.

## Turned Down

### Comments as operations in the log
Store `CreateComment`/`ReplyComment` as operations. This gives ordering and
catch-up for free, but undo and rollback would remove conversations, every
editor would replay them forever, and DMs would reach everyone on the branch.
Rejected.

### Presence-only chat
Relay messages like cursors, never stored. This is simple, but people who
were away miss everything, and comments by nature must persist. Rejected.

### An external chat service (Discord, Slack)
Teams can still use one, but it cannot pin a comment to a node, follow the
node, or open the scene at the right place. Integrations may come later on top
of this channel.

## Changes Since

Accounts exist now ([ADR 0015](0015-accounts-and-sign-in.md)). For a signed-in
editor the member id and name come from the account, so direct messages are
private between signed-in people. Self-declared member ids remain only for
connections made with an invite code or the server key.

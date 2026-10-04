# Network Protocol

How the Godot add-on and the server talk: connections, frames and message
types. What operations mean is in [operation_system.md](operation_system.md);
who may connect is in [security.md](security.md).

## 1. Two Connections

| | Used for |
|---|---|
| WebSocket (WSS) | Edits, presence, chat and comments. One connection per open project. |
| HTTPS | File contents, signing in, the website and the dashboard ([assets.md](assets.md), [authentication.md](authentication.md)). |

Everything goes over TLS on a live server. The add-on warns when someone
connects with a plain `ws://` address.

## 2. A Session

```
connect    WSS with "Authorization: Bearer <secret>" on the handshake
admit      the server checks the secret and decides which projects it opens
hello      the client sends Hello, the server answers Welcome
subscribe  the client names its branch and the last seq it has
sync       the server sends the missing operations in pages
live       operations and presence both ways
heartbeat  Ping / Pong; missed beats make the client reconnect
reconnect  after a drop: connect again, subscribe from the last seq, send
           the queued operations again
close      the others see the person leave
```

The secret is one of ([AccessGate.cs](../server/src/YHDE.Server/Gateway/AccessGate.cs)):

- an editor sign-in token, from signing in through the website
  ([authentication.md](authentication.md) §4). It opens the projects of the
  person's team that they have access to;
- an invite link's code, which opens one project ([projects.md](projects.md));
- the server access key, which opens every project (the operator's key).

A wrong or expired secret closes the socket with code 4401 before any frame
is read. The add-on treats 4401 as final and asks the person to sign in
again instead of retrying. The server also closes a signed-in person's
connections with 4401 when they sign out, are removed from the team or lose
access to the project.

Nothing about a session has to survive on the server. It can be rebuilt from
the secret and the log, so a lost socket never loses stored data
([reliability.md](reliability.md)).

## 3. Frames

Messages are binary. Each frame is a 4-byte big-endian length followed by
MessagePack ([Codec.cs](../server/src/YHDE.Server/Framing/Codec.cs)):

```
+-------------+---------------------------------------------+
| frame_len   | envelope and payload (MessagePack)          |
| uint32 BE   | [version, type, channel, flags, ref, seq,   |
|             |  payload]                                   |
+-------------+---------------------------------------------+
```

| Envelope field | Meaning |
|---|---|
| `version` | Protocol version, agreed in `Hello` / `Welcome`. |
| `type` | The message type (section 4). |
| `channel` | `system`, `ops`, `presence`, `assets-ctrl` or `social`. |
| `flags` | Reserved. |
| `ref` | `client_op_ref` for operations. |
| `seq` | The server's sequence number, or the client's ack. |

Message type values never change meaning; new ones are only added.

## 4. Message Types

### system
| Type | Direction | |
|---|---|---|
| `Hello` (0x01) | C to S | Version, capabilities and who is connecting. |
| `Welcome` (0x02) | S to C | Accepted: session id, server capabilities, the project. |
| `Ping` / `Pong` (0x03, 0x04) | both | Heartbeat. |
| `Error` (0x05) | S to C | Code, message and whether a retry can help. |
| `Close` (0x06) | both | Closing, with a reason. |

| Message | Fields |
|---|---|
| `Hello` | `[protocol_version, capabilities[], auth_token, member_id, display_name, client_version]`. With a sign-in, the account decides the name and id, not these fields. |
| `Welcome` | `[negotiated_version, session_id, server_capabilities[], server_version, project_id, project_name, branch_id]`. The project fields are set when the secret opens one project. |

### ops
| Type | Direction | |
|---|---|---|
| `Subscribe` (0x10) | C to S | Follow a branch, from the last `seq` the client has. |
| `SyncState` (0x11) | S to C | A page of missing operations. |
| `SubmitOp` (0x12) | C to S | A new operation. |
| `OpCommitted` (0x13) | S to C | A stored operation, to everyone in the branch. |
| `OpRejected` (0x14) | S to C | Refused, with the reason, to the sender only. |
| `UndoRequest` (0x15) | C to S | Undo an `op_id`. |
| `Ack` (0x16) | both | The highest `seq` received. |
| `UndoResult` (0x17) | S to C | The answer to an undo request. |

`EditText` ([text_editing.md](text_editing.md)) is an ordinary `SubmitOp`.
The server adjusts it before storing, or refuses it with `TextOutdated`.

### presence
| Type | Direction | Fields |
|---|---|---|
| `PresenceUpdate` (0x20) | C to S | `[display_name, scene, tool, state_json]`. Who sent it is added by the server. |
| `PresenceState` (0x21) | S to C | `[entries[], left[], full]`. `full` is the whole picture (after `Subscribe`); otherwise only changes. |

An entry is `[session_id, actor_id, display_name, scene, tool, state_json,
updated_at_ms, member_id]`. The server cleans every field (length limits, no
control characters, `res://` scenes only, `state_json` a JSON object of at most
16 KiB) and allows 30 updates a second per session; extra ones are dropped.
See [presence.md](presence.md).

### social
| Type | Direction | Fields |
|---|---|---|
| `SocialRequest` (0x40) | C to S | `[request_id, kind, body_json]`: send a message, create or answer a comment. |
| `SocialEvent` (0x41) | S to C | `[kind, body_json, request_id]`: a new message or thread, an answer, or an `error`. |

Kinds and rules: [social.md](social.md). The server lists `"social"` in its
capabilities.

### assets-ctrl
The types `AssetNeed`, `AssetHave` and `AssetUploadInit` (0x30 to 0x32) are
reserved. File transfer uses HTTP instead (section 5).

## 5. File Transfer over HTTP

Same host and same secret as the WebSocket, `Authorization: Bearer <secret>`
on every request (401 without it). The bytes never go over the WebSocket.

| Route | |
|---|---|
| `POST /assets/missing {"hashes":[...]}` | Returns `{"missing":[...],"partial":{hash:offset}}`: which to upload and where to resume. At most 10 000 hashes. |
| `HEAD /assets/blobs/{sha256}` | 200 with `Content-Length` when stored, 404 with `Upload-Offset` otherwise. |
| `GET /assets/blobs/{sha256}` | The bytes. `Range` works; the ETag is the hash. |
| `PATCH /assets/blobs/{sha256}` | Adds a chunk of at most 64 MiB (`Upload-Offset`, `Upload-Length`). 200 `{"offset","complete"}`; 409 `{"offset"}` means resume there; 422 means the bytes don't match the hash and were thrown away; 413 too large; 423 someone else is uploading it. |

The server lists `"assets"` in its capabilities. A file operation is accepted
only after its bytes are stored (otherwise `AssetMissing`). One that changes
nothing (same path, same bytes, as when two editors register the same
starting files) gets `AlreadyCurrent` and is not logged; the editor counts it
as done.

## 6. Delivery

- `ops` is reliable and in order per branch: every stored operation arrives,
  in `seq` order, and duplicates are dropped by `op_id`.
- Clients send `Ack` now and then so the server knows how far they are.
- `presence` may drop updates; the next one replaces them.
- Every `SubmitOp` keeps its `op_id` and `client_op_ref` when sent again after
  a reconnect, and the server ignores ones it already stored.

## 7. Limits and Paging

- A client message may be at most `Yhde:MaxFrameSizeBytes` (16 MiB by
  default), checked while it is being received.
- Missing operations come as `SyncState` pages of at most 256 operations or
  8 MiB. Every page but the last has `has_more = true` (field 5). Operations
  stored while paging also arrive live; the client drops duplicates by `seq`.
- The editor merges fast repeated changes, such as a drag, into fewer
  operations before sending ([capacity.md](capacity.md)).

## 8. Changing the Protocol

- `protocol_version` is agreed in `Hello` and `Welcome`. An editor that is too
  old gets an `Error` telling it to update.
- Changes add message types or fields at the end; existing ones never change
  meaning. Fields added this way are ignored by older clients.
- New operation types need no protocol change; they ride inside `SubmitOp`
  and `OpCommitted`.

## See Also

- [operation_system.md](operation_system.md), [presence.md](presence.md),
  [social.md](social.md), [text_editing.md](text_editing.md),
  [assets.md](assets.md), [reliability.md](reliability.md),
  [security.md](security.md)

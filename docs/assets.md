# Files

How project files (textures, models, audio, scripts, scenes, `project.godot`)
are stored and shared. The decision behind it is
[ADR 0005](adr/0005-asset-versioning.md).

Server: `server/src/YHDE.Server/Assets/`. Editor:
`client/gdextension/src/assets/`. Tested by the two-editor end-to-end test
(phase 7, and phase 8 for `.blend`).

## 1. Log and Bytes Are Separate

| | Carries | Over |
|---|---|---|
| The log | Which content each file path has | WebSocket, as operations |
| The blob store | The file contents | HTTPS |

Operations never carry file bytes, so the log stays small and fast to replay.

## 2. How a File Is Known

A file is known by its project path (`res://...`). Its content is known by
the SHA-256 of the bytes. A new version of a file is simply a new hash for the
same path, recorded by an `UpdateAsset` operation, so every change of a file
is in the log with who made it.

## 3. The Blob Store

A blob's name is the hex SHA-256 of its bytes
([BlobStore.cs](../server/src/YHDE.Server/Assets/BlobStore.cs)). So:

- Identical bytes are stored once, however many files or projects use them.
- A stored blob never changes.
- Any blob can be checked by hashing it again.

Uploads can be resumed. Bytes are appended to a part file at the offset the
client gives, and the part becomes a blob only when its hash matches the name
it was uploaded under.

Blobs no operation refers to any more (after a project is deleted) are removed
by a sweep. Blobs younger than a day are kept, because an upload in progress
is not in the log yet.

## 4. Sharing a File

1. The editor hashes the file.
2. It asks the server which hashes are missing (`POST /assets/missing`).
3. It uploads only those, in chunks (`PATCH /assets/blobs/{hash}`).
4. It sends `RegisterAsset` or `UpdateAsset`. The server accepts the
   operation only if the blob is stored.

Receiving is the reverse: the operation arrives, the editor downloads the hash
if it doesn't have it, checks the hash and writes the file. The routes are in
[network_protocol.md](network_protocol.md) §5.

## 5. What the Editor Does

- Order. A file goes into the log after its `.uid` and `.import` files and
  after the project files it depends on (`"d"` in the payload). A receiver
  holds a file until those are written, has the editor import each batch
  before scene edits that use it are applied, and lets the editor scan a new
  folder before writing into it. A new importable file is shared once the
  editor has imported it, so its uid is the same for everyone
  ([ADR 0009](adr/0009-shared-imports-and-dependencies.md)).
- Edits that use a new file wait until that file's registration has gone out.
- Files imported with an outside tool. For `.blend` (and FBX set to
  FBX2glTF), the import result in `.godot/imported/` (`dest_files` and the
  `.md5`) is shared with the source, and receivers use it instead of importing
  again. Teammates don't need Blender. These are the only `.godot` files the
  server accepts.
- Open scenes and resources travel as operations. Their bytes are shared when
  they first appear and when they change outside the editor. Files the engine
  writes itself are taken in, never sent again.
- The YHDE add-on folder (`addons/yhde/`, any letter case) is never shared or
  touched. `project.godot` can change but can't be deleted or moved; the
  editor and the server both refuse that.
- When 20 or more known files disappear in one scan (a folder deleted by
  mistake, a wrong checkout), nothing is deleted for the team. The panel asks
  whether to restore them or delete them for everyone. Files a teammate
  deletes go to this computer's trash.
- Registering bytes the log already has for that path is answered
  `AlreadyCurrent` and not logged.
- Each editor remembers the last file operation it applied per file, and skips
  ones delivered again after a re-sync, so old bytes never overwrite newer
  work. A received `.uid` or `.import` waits up to 30 seconds for its file's
  operation, and a sidecar the editor removes because its file is missing is
  never shared as a deletion.
- If the log is ahead of what an editor applied and nothing is waiting, the
  editor re-syncs after a few seconds.

## 6. Checks

- Every transfer is checked against its hash; a mismatch is thrown away (422).
- File routes need the same secret as the WebSocket and are rate limited
  ([security.md](security.md)).
- A person with view access can't change files: their file operations are
  refused like any other edit.

## 7. Rules

1. File bytes never go over the operation stream.
2. Identical bytes are stored once.
3. A stored blob never changes, so old history always finds its files.
4. Every transfer is hash checked.
5. Every file change is an operation in the log.

## See Also

- [operation_system.md](operation_system.md) §3, [network_protocol.md](network_protocol.md) §5,
  [database.md](database.md), [versioning.md](versioning.md),
  [ADR 0005](adr/0005-asset-versioning.md)

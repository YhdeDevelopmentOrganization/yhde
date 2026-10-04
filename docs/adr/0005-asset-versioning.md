# ADR 0005: Files Stored by Content Hash

- Status: Accepted
- Date: 2026-06-16
- See also: [assets.md](../assets.md), [operation_system.md](../operation_system.md)

## Situation

Besides scene edits, YHDE has to share files: textures, models, audio,
materials, shaders, scripts. The spec asks for uploads of only what changed,
no duplicate storage, and a version for every file. Old operations must find
the exact bytes they referred to, forever, for replay and rollback. And the
operation log must not fill up with binary data.

## Decision

- File bytes go in a blob store on disk, named by the SHA-256 of their
  content. The log only records which content each file path has.
- A new version of a file is a new hash for the same path, recorded by an
  operation (`RegisterAsset`, `UpdateAsset`, `MoveAsset`, `DeleteAsset`).
- Bytes travel over HTTPS, never over the operation stream. The editor first
  asks which hashes the server is missing and uploads only those.

## Why

- No duplicates: identical bytes have the same name and are stored once.
- Only new content is uploaded.
- A stored blob never changes, so any old operation still finds its bytes.
- The log stays small and fast to replay ([ADR 0001](0001-operation-log.md)).
- Every transfer can be checked by hashing it; changed bytes don't match.

## Costs

- A second transfer path (HTTPS routes) to maintain.
- Old versions stay as long as history needs them, so storage only grows
  (a blob is removed only when no operation refers to it any more).
- The blob store has to be backed up alongside the database.

## Turned Down

1. File bytes inside operations. Bloats and slows the log and makes replay
   expensive.
2. Blobs in PostgreSQL. A poor fit for large files, and harder to back up.
3. Files stored by name with a version column. No automatic dedup, harder to
   check and to keep unchanged.

## Changes Since

The first design gave each file a UUID and a version counter, chunked
uploads and a control channel on the WebSocket. What was built is simpler: a
file is known by its path, its version is its hash, uploads resume by offset,
and the negotiation is plain HTTP ([assets.md](../assets.md)).

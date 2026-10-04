# Engineering Rules

The rules every change to YHDE follows, and how the documentation is kept.

## 1. Rules

- Every change is an operation, and operations are immutable.
- Scene files are never synchronized as files; scenes are built from operations.
- The server is authoritative. Clients are never trusted.
- Undo is decided by the server.
- Operations can be replayed and rolled back.
- Every object has a UUID.
- Everything is auditable.
- Security and reliability are required, not optional.
- Performance matters, but correctness comes first.
- Prefer explicit systems over magic, composition over inheritance, and no
  hidden state.
- Keep things extensible.

## 2. Things We Learned the Hard Way

- Apple clang: never pass `size_t` into a `Variant` or `print`; cast to `int64_t`.
- godot-cpp: `String + "literal"` can be ambiguous; wrap the literal in `String(...)`.
- `EditorFileSystem::update_file` does nothing while the editor is scanning, and
  creates a new `.uid` if none exists. Write the `.uid` and `.import` files first.

## 3. Documentation Map

The design documents describe what each part is meant to do. Read the one for
the area you change first. Each subsystem's details live in one place only.

- Master spec: [master_spec.md](master_spec.md)
- Foundations: [vision.md](vision.md), [architecture.md](architecture.md),
  [roadmap.md](roadmap.md)
- Core subsystems: [operation_system.md](operation_system.md),
  [network_protocol.md](network_protocol.md), [database.md](database.md),
  [snapshots.md](snapshots.md), [assets.md](assets.md),
  [authority.md](authority.md), [conflict_resolution.md](conflict_resolution.md),
  [versioning.md](versioning.md), [presence.md](presence.md),
  [social.md](social.md), [text_editing.md](text_editing.md),
  [projects.md](projects.md), [teams.md](teams.md), [admin.md](admin.md),
  [onboarding.md](onboarding.md), [editor_client.md](editor_client.md)
- Cross-cutting: [authentication.md](authentication.md), [security.md](security.md),
  [reliability.md](reliability.md), [capacity.md](capacity.md),
  [plugin_api.md](plugin_api.md), [deployment.md](deployment.md)
- Decisions: [adr/](adr/). Accepted ADRs are not rewritten; a new ADR
  supersedes an old one.

## 4. Writing Docs

- One subsystem per document. Link to other documents instead of copying them.
- Record major decisions as ADRs: context, decision, rationale, consequences
  and alternatives.
- Keep documents consistent with [master_spec.md](master_spec.md). If a change
  contradicts the spec, update the spec on purpose.
- A subsystem document and its ADR link to each other.
- Plain writing: short sentences, no decorative characters, no filler.

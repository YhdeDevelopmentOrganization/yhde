# Plugin API

Not built yet. This is how others could extend YHDE later without changing
the core. Today the operation types are fixed
([operation_system.md](operation_system.md) §3).

## 1. What Could Be Added

| Extension | Adds | Has to follow |
|---|---|---|
| Operation types | New kinds of edits | Everything in section 2 |
| File types | Special handling for some files | The file rules in [assets.md](assets.md) |
| Access rules | Tighter rules on who may do what | May only restrict, never allow more ([authority.md](authority.md)) |
| Sync handlers | How new operations are applied in the editor | Same result on every machine |
| Inspectors | Editor UI for plugin data | UI only; the server checks everything |

Registrations would be explicit (a manifest), never patching the core at
runtime.

## 2. What a New Operation Type Must Provide

A plugin operation goes into the log like any other, forever, so it must:

- have a stable type id, namespaced to the plugin, so it travels inside
  `SubmitOp` and `OpCommitted` without a protocol change;
- apply the same way on every machine: no clocks, randomness, locale, scene
  paths or child indices;
- be invertible, so the server can undo it (the payload carries the old
  value);
- validate its payload and refuse anything unclear;
- say what access it needs;
- keep old payload versions replayable: schemas only ever grow.

An operation type that can't do all of this is refused when it registers.

## 3. Where Plugin Code Would Run

| Side | Does | Trusted |
|---|---|---|
| Server | Apply, invert, validate, access rules | Yes, but sandboxed: limited CPU, memory and time, no direct database, file or network access. A failing handler refuses its own operation and nothing else. |
| Editor | Inspectors, previews | No. The server checks everything again. |

## 4. Examples

- A studio adds a `BakeLightmap` operation.
- An art pipeline adds a file type with its own checks.
- A QA team adds review notes with an inspector.
- A team only lets admins delete resources on `main`.

## See Also

- [operation_system.md](operation_system.md), [authority.md](authority.md),
  [security.md](security.md)

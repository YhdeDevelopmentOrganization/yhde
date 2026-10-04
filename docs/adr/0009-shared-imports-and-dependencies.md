# ADR 0009: Shared Import Results and Dependency-Ordered Files

- Status: Built
- Date: 2026-09-25
- See also: [assets.md](../assets.md), [editor_client.md](../editor_client.md),
  [security.md](../security.md), [ADR 0005](0005-asset-versioning.md)

## Situation

Project files reach every editor through the asset plane
([assets.md](../assets.md)). Two problems showed up with real projects:

1. Files that need an outside tool to import. Godot imports a `.blend` by
   running Blender (and an FBX by running FBX2glTF, when chosen). A teammate
   without Blender, or with a different Blender version, gets an import error,
   or a slightly different model. The import result lives in
   `.godot/imported/`, which YHDE never shared, because it is a per-machine
   cache.
2. Files that need other files. A model uses textures, a material uses a
   texture, a scene uses sub-scenes. When a file arrived before what it needs,
   or before the editor had imported it, the editor printed load errors, and
   the result looked right only after a reload ("it errors but works"). The
   same happened when a new file's `.import` (which holds its uid) arrived
   after the file: the receiver imported it itself with another uid.

## Decision

1. For sources whose import needs an outside tool (`.blend`; `.fbx` set to
   FBX2glTF), the editor that imported them shares the import result: the
   files listed in `.import` `[deps] dest_files` and the `.md5` checksum file.
   These are the only files under `.godot/` the server accepts
   (`res://.godot/imported/<file>`, no subfolders).
2. They are shared only together with a change of their source's `.import`
   (made where the import ran), never on their own. Receivers write them
   before the `.import` and the source, and do not import those files again:
   Godot's checksum test then keeps the shared result.
3. A new or changed file goes into the log after what it needs: its
   `.uid`/`.import` sidecars first, then the project files it depends on
   (`ResourceLoader.get_dependencies`), then itself. Its operation lists those
   dependencies (`"d"`), and a receiver holds it until they are written.
4. A new importable file is shared once the editor has imported it (it waits
   while the editor imports, up to 3 minutes, then goes as is).
5. A receiver makes the editor import each batch of arrived files before
   applying later scene edits that use them. It lets the editor scan a new
   folder before writing files into it.
6. An edit that refers to a project file the log does not have yet (a texture
   assigned right after it was dropped in) waits until that file's
   registration has gone out ahead of it.

## Why

- Sharing the result, not the tool, is the only way a teammate without
  Blender can open the project. Every editor then shows exactly the same
  model, whatever Blender version the others have.
- Ordering by dependency matches what Godot needs to load a file without
  errors. It uses information the engine already has.
- The rules keep files a projection of the log ([ADR 0001](0001-operation-log.md)).
  Import results are ordinary content-addressed blobs
  ([ADR 0005](0005-asset-versioning.md)) under a narrowly validated path.

## Consequences

- Gains: `.blend` files work for the whole team with one Blender install.
  New models, textures and materials arrive without load errors, with the same
  uid everywhere.
- Cost: import results use blob storage (a `.scn` is often as big
  as its source). Only tool-imported sources pay this.
- Cost: a receiver that *has* Blender still uses the shared result
  instead of importing itself. That is intended (same result everywhere), but
  changing the import settings there shares a new result for everyone.
- Cost: dependency discovery loads resource headers, so sharing a
  large scene costs a little more time.
- Later: extend the tool list if more importers need outside
  programs; show "waiting for import" per file in the UI.

## Turned Down

### Require every teammate to install the same Blender
This is fragile (versions drift) and excludes people, such as programmers
and testers, who never touch Blender. Rejected.

### Share all of `.godot/imported/`
It would be simple, but most of it is quick to rebuild, and some of it is
machine-specific (texture compression for the local GPU). It would multiply
storage and traffic. Rejected in favour of only what needs an outside tool.

### Retry loading on the receiver until it works
This hides the ordering problem instead of fixing it, and the errors still
reach the Output panel. Rejected.

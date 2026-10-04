# Getting the Add-on, Invite Links and Updates

How people get YHDE and a project, and how editors update the add-on. The
decision is [ADR 0013](adr/0013-addon-from-the-server.md).

## 1. Installing the Add-on

The usual way: download the add-on zip from the dashboard (Projects, Godot
add-on) and install it in Godot with AssetLib, Import..., pick the zip,
Install. There is no need to unzip it. Then enable YHDE under Project >
Project Settings > Plugins and sign in from the YHDE panel.

Each release's signed add-on zip is also on the repository's releases page
(https://github.com/YhdeDevelopmentOrganization/yhde/releases). The old
`yhde-godot` repository is archived.

## 2. Releasing a New Add-on

1. Staff upload the add-on zip on the admin page (Add-on, Upload zip), then
   press Release. An upload alone changes nothing: editors, invite links and
   the dashboard keep the released add-on until the new one is released.
2. When the server has a newer add-on than an editor's, the YHDE panel says
   "YHDE 0.4.3 is available" with Update. Updating downloads it, checks it,
   writes it over `addons/yhde` and offers Restart Godot now.

## 3. View Links (Invite Links)

A view link lets someone watch one project without an account, for example a
playtester: they open it in Godot and see every change live, but can't change
anything. A project lets in up to five viewers; a link holds its places until
it is turned off, which also cuts off everyone who used it
([teams.md](teams.md) §4). Links made on the admin page still let people
edit.

1. On a project's page, make a view link: how long it works (1 day to 30
   days, or until turned off) and how many people it lets in. It looks like
   `https://<server>/join/K7QM-3XRT-9WDA`.
2. Opening it shows the steps and starts the download of `<Game name>.zip`:
   an empty Godot project named after the game, with YHDE in `addons/yhde`,
   enabled, and the server address and an invite code filled in.
3. They unzip it, open it in Godot 4.7, and the game's files and settings
   arrive from the server.

A link only downloads. Each download makes its own invite code (labelled
"<link label> · download N"), so a link can expire or be used up while the
people who used it keep working, and each of them can be cut off alone.
Opening the link doesn't use it up: the page starts the download itself (a
meta refresh to `/join/<token>/download`) and only that request counts, so
link previews in chat apps never use a link.

The join page, `https://<server>/join`, also takes a typed invite code, and
gives only the add-on (`yhde-addon-<version>.zip`) without one.

## 4. On the Server

`AddonStore` (`server/src/YHDE.Server/Addon/`) keeps one package in
`Yhde:AddonPath` (by default `addon/` next to the blob folder; `/data/addon`,
a volume, in the server kit) with a manifest:

```json
{"version": "0.4.3", "sha256": "<of the package>", "size": 743452,
 "platforms": ["windows"], "uploaded": "..."}
```

An upload is accepted when the zip holds the add-on in some folder
(`addons/yhde/`, `yhde/`, or at its root: `plugin.cfg` next to
`yhde.gdextension`) with a `version="..."` in `plugin.cfg` and at least one
native core in `bin/`. File names must be relative, without `..`. It is stored
with only the add-on's files, under `addons/yhde/`. Nothing in it runs on the
server.

| Endpoint | Who | What |
|---|---|---|
| `GET /join` | anyone | The join page (no scripts, strict CSP). |
| `GET /join/{token}` | anyone | An invite link: the steps, and the download starts. Doesn't use the link. |
| `GET /join/{token}/download` | anyone | The starter project. Uses the link once and makes an invite code. A dead link (expired, used up, turned off, project archived) gets the page with an explanation. |
| `POST /join` `code=` | anyone | No code: the add-on zip. A valid invite code: the starter project. A wrong code: the page with an error. At most 20 tries per address per 10 minutes. |
| `GET /addon/manifest.json` | anyone | The current package's manifest (404 when there is none). |
| `GET /addon/yhde-addon.zip` | anyone | The package. |
| `POST /admin/api/addon` | staff | Upload (the zip, up to 128 MB). |

The add-on and the join page are public on purpose: the add-on is published
anyway, and a project is only reachable with an invite code, an invite link
or a sign-in. Typed codes go in the form body. Link tokens are in the URL,
which is what makes them one click; they are 60 random bits, stored as
hashes, and should be given a lifetime or a download limit.

The starter project has `project.godot` with the game's name,
`config/features` 4.7 and YHDE enabled; the add-on; and
`addons/yhde/join.cfg` with the server address (`wss://` and the host the
page was opened on) and the invite code. The zip's folder is the game's name
without the characters Windows refuses.

## 5. In the Editor

- Join file: on start, `main.gd` reads `res://addons/yhde/join.cfg`, keeps
  the address in the project metadata and the code outside the project (like
  a sign-in), deletes the file and offers to join. `addons/yhde` is never
  shared, so the file can't reach anyone else.
- Update check (`ui/updater.gd`): once connected, `GET /addon/manifest.json`
  on the same server. A newer version (compared as numbers) that includes this
  platform's native core is offered.
- Installing an update: download the package, compare its SHA-256 with the
  manifest, check that every file is under `addons/yhde/`, then write them
  over the add-on. Godot runs a copy of the native core (`~libyhde...`), so
  the library can be replaced while the editor runs; the new one loads on
  restart. A damaged or unexpected package changes nothing.

## 6. Making a Package

```bash
python client/package_addon.py [output folder]
```

Packs `client/godot/addons/yhde`, with every native core in `bin/`, into
`yhde-addon-<version>.zip`. Before a release, bump the version in
`plugin.cfg`, `kAddonVersion` in `yhde_session.cpp` and `ServerInfo.Version`
on the server together; a server test checks that they match. Only use native
libraries built by the "Build YHDE native core" workflow: a local build
contains the paths of the machine it was built on.

## See Also

- [projects.md](projects.md), [admin.md](admin.md), [security.md](security.md) §11,
  [setup_your_server.md](setup_your_server.md), [ADR 0013](adr/0013-addon-from-the-server.md)
- [ADR 0016](adr/0016-people-belong-to-projects.md): people belong to projects, not teams

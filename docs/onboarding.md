# Getting the Add-on, Invite Links and Updates

How people get YHDE and a project, and how editors update the add-on. The
decision is [ADR 0013](adr/0013-addon-from-the-server.md).

## 1. The Three Ways In

Every way ends in Godot with YHDE already in the folder and one button,
Join (`ui/first_run.gd`, §5). The website's Docs page says the same in the
same words (`site/pages/Docs.tsx`).

- **Invited.** Open the invite link, press Download, open the zip in Godot
  with Import, press Join (§3). With a code instead: "I was invited" on the
  Projects page, then the project's page offers the download.
- **Starting from nothing.** On the Projects page, "Start a project" (with
  the access code during the beta, asked in the same form). The project's
  page opens on "Get started": Download <Game> (the starter, with a ticket
  for this account, `POST /api/team/projects/{id}/starter`), open it and
  press Join, Invite people. Each step shows when it is done (`inGodot`
  from the dashboard; someone invited or a working link); the panel goes
  away when all are, and until then the page shows only the steps, People
  and the image.
- **A game they already have.** Install the add-on (below), sign in from
  the panel, "Share this game", then Invite people.

People without an account make one on the sign-in page Godot or a link
opens ("Make an account", same page); the confirmation email's link signs
them in and brings them back there ([authentication.md](authentication.md)
§1).

Installing the add-on by hand: download the zip from the dashboard
(Projects, Godot add-on) and install it in Godot with Asset Store,
Import..., pick the zip, Install. There is no need to unzip it. Then enable
YHDE under Project > Project Settings > Plugins.

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

## 3. Invite Links

An invite link lets someone into one project ([teams.md](teams.md) §4). The
owner picks "can edit" (a teammate: they sign in once in Godot and become a
member) or "can only watch" (a playtester: no account, they see every
change live but can't change anything; up to five per project). Links made
on the admin page still let people edit.

1. The owner makes a link under People on the project's page, or with
   Invite people in Godot: "Can edit" or "Can only watch", then Copy invite
   link. It is for one person and works for 7 days (the API takes other
   counts and lifetimes, up to 30 days or until turned off). It looks like
   `https://<server>/join/K7QM-3XRT-9WDA`. Working links are listed with
   the people, each with Turn off.
2. Opening it shows who invited them to which game (and its image, if it
   has one), whether they can edit or only watch, and three steps:
   download it, open it in Godot, press Join. Nothing downloads until they
   press "Download <Game>".
3. In Godot they press Import and pick `<Game>.zip` (the Project Manager
   unpacks it; no unzipping needed). The project is empty except for YHDE,
   which is on, with the server address and an invite code or ticket in it.
4. In the YHDE panel they press Join. The game's files and settings arrive
   from the server.

The page names the oldest Godot the released add-on runs on
(`compatibility_minimum` in its `yhde.gdextension`, `AddonStore.OldestGodot`).

A link only downloads. Each download makes its own invite code (labelled
"<link label> · download N") or ticket, so a link can expire or be used up
while the people who used it keep working, and each of them can be cut off
alone. Opening the link doesn't use it up; only the download does, so link
previews in chat apps never use a link.

A link that no longer works (ran out, used up, turned off, project archived)
says so and names the owner to ask for a new one. Under "Have a code?" the
page takes a typed code. The page `https://<server>/join` takes one too, and
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
| `GET /join/{token}` | anyone | An invite link: who invited, to what, edit or watch, and the steps. Doesn't use the link. |
| `GET /join/{token}/image` | anyone with a working link | The project's image, for that page. |
| `GET /join/{token}/download` | anyone | The starter project. Uses the link once and makes an invite code (watch-only link) or a join ticket (edit link, [teams.md](teams.md) §4). A dead link (expired, used up, turned off, project archived) gets the page with an explanation. |
| `POST /join` `code=` | anyone | No code: the add-on zip. A valid invite code: the starter project. A wrong code: the page with an error. At most 20 tries per address per 10 minutes. |
| `GET /addon/manifest.json` | anyone | The current package's manifest (404 when there is none). |
| `GET /addon/yhde-addon.zip` | anyone | The package. |
| `POST /admin/api/addon` | staff | Upload (the zip, up to 128 MB). |

The add-on and the join page are public on purpose: the add-on is published
anyway, and a project is only reachable with an invite code, an invite link
or a sign-in. Typed codes go in the form body. Link tokens are in the URL,
which is what makes them one click; they are 60 random bits, stored as
hashes, and should be given a lifetime or a download limit.

The starter project has `project.godot` with the game's name and YHDE
enabled, and no Godot version: any version written there makes every other
Godot warn "Opening will change it to Godot 4.x" (checked on 4.6 and
4.7.2); without one, Godot opens it quietly and writes in its own. Then the
add-on, and
`addons/yhde/join.cfg` with:

- `url`: the server address (`wss://` and the host the page was opened on);
- `code`: an invite code (watch-only links, the join page's code form), or
  `ticket`: a join ticket (edit links, a member's own download);
- `project` and `from`: the game's name and its owner's name, for the
  panel's first words. Add-ons before the first-run view ignore them.

The zip's folder is the game's name without the characters Windows refuses.
Every folder has its own entry in the zip, before its files: Godot's Project
Manager imports a zip directly (Import, pick the zip), but skips every file
in a folder the zip doesn't list. Before this (0.6.9 and earlier) importing
the zip that way left out the whole add-on.

## 5. In the Editor

- Join file: on start, `main.gd` reads `res://addons/yhde/join.cfg` and
  deletes it. The code or ticket, the game's name and the owner's name are
  kept outside the project, in the editor's `yhde_account.cfg` next to the
  sign-in (sections `join tickets` and `invites`, one entry per folder), not
  in the project's editor settings: Godot writes those only when it closes
  normally, and an invite must survive a crash before Join. `addons/yhde` is
  never shared, so the file can't reach anyone else. An add-on that finds no
  code (a ticket it doesn't read) deletes the file and shows the usual
  sign-in; the project is listed there once the account is in it.
- First-run view (`ui/first_run.gd`): until a folder has been live once, the
  panel shows one next step in place of its usual header, tabs and footer
  (messages and file cards still show below it), and comes to the front when
  Godot starts:

  | State | Shows | Button |
  |---|---|---|
  | From an invite | "Sam invited you to Skyhop" | Join Skyhop |
  | Waiting for the browser | "Finish signing in in your browser", the code | Open the browser again |
  | Signed out, no invite | "Sign in to YHDE" | Sign in |
  | Signed in, no invite | their projects; "Share this game" when the folder has files | (pick one) |
  | Joining or sharing | "Getting Skyhop" / "Sending Skyhop", a bar, "n of m files" | none |
  | Live | "You're in."; an owner alone in the project: "Invite people" | Invite people / OK |
  | A problem | one sentence: no native core, server unreachable, a refused ticket | Get YHDE again / Try again / Sign in with another account |

  Join with a ticket: if signed out, sign in first (the browser opens, then
  Join goes on by itself); send the ticket; connect this folder to the
  project. A starter folder is empty, so the right-folder question does not
  come up. Join with a code (watch-only) connects at once. A folder that was
  connected before never shows the view.
- Invite dialog (`ui/invite_dialog.gd`): "Can edit" or "Can only watch",
  Copy invite link (a link for one person, 7 days), the join code on request,
  and an email invitation. Opened from the first-run view, the People tab's
  "Invite people" (owners) and a project card's menu.
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

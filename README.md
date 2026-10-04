# YHDE

Real-time collaboration for the Godot editor, made by YHDE Development Organization. A whole
team works in the same Godot project at the same time: scene edits,
properties, scripts and files show up for everyone as they happen.

This repository holds the server, the website, the Godot add-on and its
native core, tests and design docs. The add-on, ready to install, is on the
[releases page](https://github.com/YhdeDevelopmentOrganization/yhde/releases).

## How It Works

Every edit in the editor (adding a node, moving it, changing a property,
importing a texture) becomes an operation. The server puts operations in
order, stores them in a log and sends them to everyone else in the project.
A project is its log: scene files are rebuilt from it and are never sent
around as files. History, undo, replay and conflict handling all come from
the log. See [docs/vision.md](docs/vision.md) and
[ADR 0001](docs/adr/0001-operation-log.md).

| Part | Built with |
|---|---|
| Editor add-on | Godot 4.7 (tested on 4.7.2), GDScript UI, C++ GDExtension core |
| Server | C# on .NET 10 |
| Database | PostgreSQL |
| Files | Content-addressed blob store on disk |
| Connection | WebSockets (WSS) for edits and presence, HTTPS for files and the website |
| Website | React, Vite, Tailwind |
| Hosting | One VPS with Docker Compose and Caddy |

The rules every change follows are in
[docs/engineering_rules.md](docs/engineering_rules.md).

## Repository Layout

```
client/
  gdextension/   C++ native core: sync engine, protocol, queue, cache
  godot/         Godot host project; addons/yhde is the add-on
  tests/         codec, resilience and two-editor end-to-end tests
server/
  src/           the server
  admin-ui/      website, dashboard, join page and admin page
  tests/         server tests
deploy/          server kit: Postgres, server, HTTPS, backups
docs/            design docs and ADRs
```

## Running It Locally

1. PostgreSQL with a `yhde` database and user (see
   `server/src/YHDE.Server/appsettings.json`).
2. The server: `dotnet run --project server/src/YHDE.Server --urls http://localhost:5080`.
   Migrations run on start. The Development profile runs without an access
   key; everywhere else the server needs `Yhde__AccessKey`.
3. The website: `npm --prefix server/admin-ui install`, then
   `npm --prefix server/admin-ui run build` (the server serves the build), or
   `run dev` for the Vite dev server.
4. The native core: build it with CMake, or take the `yhde-native-*`
   artifacts from the "Build YHDE native core" workflow and put them in
   `client/godot/addons/yhde/bin/`:
   ```
   cmake -S client/gdextension -B build -DCMAKE_BUILD_TYPE=Release
   cmake --build build --config Release --target yhde
   ```
5. The editor: copy `client/godot/addons/yhde` into a game project and enable
   YHDE under Project > Project Settings > Plugins. Set `YHDE_SERVER` to
   `ws://localhost:5080/ws` before starting Godot to use your local server,
   then sign in from the YHDE panel.

To run the server for real, `deploy/yhde install <domain>` sets up a VPS; see
[docs/setup_your_server.md](docs/setup_your_server.md) and
[docs/deployment.md](docs/deployment.md). Client builds and tests are in
[client/README.md](client/README.md).

## Status

Working today:

- Live editing of 2D, 3D and UI scenes, resources and project files, with
  undo handled by the server.
- Cursors, selections and drags of everyone in the project, follow mode and
  "bring everyone here".
- Several people typing in the same script.
- Chat, direct messages and comments pinned in scenes.
- Accounts on the website (email, GitHub, Google), teams with plans, roles
  and access per project, invite links, and signing in from Godot.
- An admin page for accounts, teams, projects, promo codes, storage,
  updates and the audit log.

During the beta, making a team needs an access code. Payments come later.
Next steps are in [docs/roadmap.md](docs/roadmap.md).

## Documentation

The design docs are in [docs/](docs/), starting with
[master_spec.md](docs/master_spec.md). Decisions are recorded in
[docs/adr/](docs/adr/README.md).

## Running Your Own Server

Anyone can run a YHDE server for their own team: it is the same server YHDE
runs, with its own accounts, projects and website. Setting one up takes about
30 minutes ([docs/setup_your_server.md](docs/setup_your_server.md)). A self-hosted
server shows its own name and legal pages, has no plans or prices, and holds
files that can run code (plugins, native libraries, `@tool` scripts) until each
person accepts them ([docs/security.md](docs/security.md) §11).

## Security

How to report a security problem: [SECURITY.md](SECURITY.md).

## License

The server, web pages, server kit and native core are licensed under the
[GNU AGPL v3](LICENSE) with additional terms: keep the "Powered by YHDE"
credit, don't present a modified version as YHDE's own, and the license grants
no rights to the YHDE name or logo. The add-on's scripts are MIT, so game
projects can carry them with just the copyright notice. Details: [NOTICE.md](NOTICE.md).

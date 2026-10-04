# Deployment

How the YHDE server runs, is configured, backed up and updated. The
step-by-step guide for a new server, written for someone who has never set one
up, is [setup_your_server.md](setup_your_server.md).

## 1. What Runs

The server kit in `deploy/` runs everything on one VPS (tested on Ubuntu 24.04
at Hetzner) with Docker Compose:

```
  internet
     |  80, 443
     v
  Caddy  (HTTPS certificates, WebSockets passed through)
     |
     v
  YHDE server (C#)  ----->  PostgreSQL
     |                       (log, accounts, teams, audit)
     v
  blob store (Docker volume "blobs")
                       backup container: daily dumps and blob copies
```

- Only Caddy's ports 80 and 443 are open to the internet.
- The server runs as a non-root user on a read-only filesystem.
- The server keeps nothing that matters in memory: everything is in
  PostgreSQL and the blob store, so it can be restarted at any time. Editors
  reconnect and send their unsent edits again ([reliability.md](reliability.md)).
- The server also serves the website, the dashboard, the join page and the
  admin page.

## 2. Setting Up

1. Create the server and allow only ports 22, 80 and 443 in (for example
   with a Hetzner Cloud Firewall).
2. Point a DNS name at it: an A record, and AAAA if you use IPv6.
3. As root, clone the repository to `/opt/yhde`:
   ```
   git clone https://github.com/YhdeDevelopmentOrganization/yhde.git /opt/yhde
   ```
4. Run `/opt/yhde/deploy/yhde install <domain>`. It installs Docker if needed,
   creates the database password, the access key and the admin password in
   `deploy/.env` (mode 600), builds and starts everything, and waits until
   `https://<domain>/health` answers.

## 3. Commands

| Command | Does |
|---|---|
| `yhde install <domain>` | Sets up or repairs everything. Safe to run again. |
| `yhde key` | Shows the server address, the admin page, the access key and the admin password. |
| `yhde status` | Containers, health and the latest backup. |
| `yhde logs [server\|caddy\|db\|backup]` | Follows a service's logs. |
| `yhde update` | Pulls the latest code, rebuilds and restarts. |
| `yhde backup` | Takes a backup now. |
| `yhde set KEY value` | Saves a setting (section 4) and restarts what needs it. |
| `yhde admin-password` | Makes a new admin password. |
| `yhde new-key` | Replaces the server access key. |
| `yhde test-branches N` | Makes N empty branches for a load test ([capacity.md](capacity.md)). |
| `yhde updater-tick` | Run by the timer (section 6). |

Run them as root on the server, for example
`ssh root@<server> "/opt/yhde/deploy/yhde status"`.

## 4. Settings

Settings and secrets live in `deploy/.env`, which is never committed. Change
them with `./yhde set KEY value`.

| Key | What |
|---|---|
| `PUBLIC_URL` | The website's address, e.g. `https://yhde.example.com`. Set by `install`. |
| `GITHUB_CLIENT_ID`, `GITHUB_CLIENT_SECRET` | Sign-in with GitHub. The OAuth app's callback is `<PUBLIC_URL>/auth/github/callback`. |
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | Sign-in with Google. Callback `<PUBLIC_URL>/auth/google/callback`. |
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM` | Email for confirmation, reset and invitation links. Without it, emails are only written to `./yhde logs server`. |
| `REDIRECT_FROM` | Other addresses that forward to the site, such as the bare domain and www (`example.com,www.example.com`). Each needs an A record pointing at the server. Only Caddy restarts. |
| `TEAMS_NEED_CODE` | `false` (the default): anyone signed in can make projects. `true` makes it need an access code ([teams.md](teams.md) §2). |

Other values in `.env` are set by `install`: the database password
(`DB_PASSWORD`), the access key (`ACCESS_KEY`), the admin password
(`ADMIN_PASSWORD`) and the largest allowed file (`MAX_ASSET_BYTES`, 2 GiB by
default).

Moving to a new domain: point its A record at the server and run
`./yhde install <new domain>`. The new domain becomes the main one, and the
earlier ones keep working so editors set up with them still connect.

## 5. The Access Key

Without `Yhde:AccessKey` the server refuses to start, unless
`Yhde:AllowAnonymous=true` is set on purpose (the local Development profile
does). The key opens every project and is for the operator only; people sign
in with their accounts or join through invite links
([network_protocol.md](network_protocol.md) §2). Never write it into a file
in the repository; `./yhde key` shows it.

## 6. Updates Approved on the Admin Page

Nothing updates by itself. The kit installs a systemd timer,
`yhde-updater.timer`, that runs `yhde updater-tick` every minute on the host
([ADR 0014](adr/0014-updates-approved-on-the-admin-page.md)):

1. Look. At most every 10 minutes, or right after Check now on the admin
   page, it runs `git fetch` and writes `deploy/updates/status.json`: the
   running commit, newer commits on the followed branch (subject, author,
   date), an update in progress and how the last one went.
2. Show. The server reads that folder (mounted at `/updates`,
   `Yhde:UpdatesDir`) and the admin page's Server tab lists the newer
   versions.
3. Approve. Update to this version makes the server write `updates/request`
   with the commit id. Only one of the listed commits is accepted.
4. Apply. The next tick checks that the commit is a newer version of the same
   branch, takes a database backup, fast-forwards, rebuilds and restarts, and
   waits for `/health`. If the build fails or the server doesn't come back
   healthy, it goes back to the previous commit and rebuilds that. The old
   containers keep running while the new image builds. The result is in
   `updates/last.json` (shown on the page), the details in
   `updates/update.log`.

The server never gets git or Docker access. It can only leave a request in a
folder, and the host decides what a request may do. The tick runs from a copy
of the script, since an update replaces the script, and holds a lock so ticks
never overlap. `./yhde update` still updates by hand.

Add-on releases are separate: they are uploaded on the admin page's Add-on
tab and released there ([onboarding.md](onboarding.md)).

## 7. Backups

- The `backup` container dumps the whole database (`pg_dump`, compressed)
  once a day and when it starts, into `deploy/backups/`, keeping 14 days
  (`KEEP_DAYS`).
- It also copies every new blob to `deploy/backups/blobs`. Blobs never
  change, so this is a complete incremental copy of the files.
- The backups folder is mounted read-only into the server, so the admin page
  can show the latest backups.
- Restoring means restoring the database dump and the blob copy. Everything
  else is rebuilt from the log.

Not done yet: the backups are on the same disk as the server. Copy them to
another machine (for example a Hetzner Storage Box) or turn on the provider's
server backups. There is no point-in-time recovery between dumps.

## 8. Files and Disk

File contents are stored in the `blobs` Docker volume (`/data/blobs` in the
server container). Blobs no operation refers to any more, after a project is
deleted, are removed by a sweep. Disk space is the first real limit
([capacity.md](capacity.md) §4); the admin page's Storage tab shows how much
is used.

## 9. What to Watch

The admin page shows most of it: people online, the server's health, disk
use, the latest backup and waiting updates. Also worth watching: CPU, and
`./yhde logs server` for repeated errors or refused sign-ins.

## See Also

- [setup_your_server.md](setup_your_server.md), [admin.md](admin.md),
  [capacity.md](capacity.md), [reliability.md](reliability.md),
  [security.md](security.md), [ADR 0014](adr/0014-updates-approved-on-the-admin-page.md)

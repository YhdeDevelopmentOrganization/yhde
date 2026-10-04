# Admin Page

The page staff use to run YHDE, at `https://<server>/admin`. Source:
`server/admin-ui/src/admin/` (React, shadcn/ui, Recharts) and
`server/src/YHDE.Server/Admin/`.

## 1. Signing In

- Staff sign in with their YHDE account: "Continue as ..." when already signed
  in on the website, or email and password.
- The account needs a staff role:
  - Admin: everything.
  - Support: sees everything, and may sign people out everywhere and resend a
    confirmation email. Nothing else.
- The server password (`ADMIN_PASSWORD`) is the emergency way in, and how the
  first account gets the admin role: Accounts, the account's menu, Make
  admin. Its use is logged. It only works with at least 12 characters;
  `./yhde key` shows it and `./yhde admin-password` makes a new one.

## 2. Tabs

| Tab | Shows and does |
|---|---|
| Overview | Online now, changes today and this week, people in the last 30 days, projects, space used. Changes per day for 30 days per project, who is online and where, and when people work (weekday by hour, in your time zone). |
| Projects | Every project with its team, files, size, changes and a 30-day chart. Make a project, empty or from a game zip; invite links (lifetime, download limit, who used them, turn off, delete); rename, archive, restore, move to a team, delete. |
| In the editor | Over the last hour, day, week, month, 3 months, year or all time (`GET /admin/api/people?range=`): active people, hours online or changes per bucket. Per person in that time: projects, changes, messages, comments, time online, last seen, add-on version; test accounts and invite-code guests marked. Deleted accounts don't show: deleting one clears its name from the connection history. |
| Accounts | Search; make someone admin or support; disable (signs them out at once); sign out everywhere; resend the confirmation; delete (type the email; an owner's projects must be deleted or moved first). Test accounts (section 4). |
| Owners | People who can make projects: plan, period, extra seats and storage, free seats (one more person in each of their projects, never charged); each project with its people (remove, or add an existing account straight in: they get an email), open invitations and viewers; codes used. |
| Promo codes | Promo and access codes (section 4). |
| Storage | Game files, database size, free disk, the largest file allowed; space per project and per kind of file; the biggest files; backups. |
| Server | Updates: the version running, newer versions, Update to this version, Check now, and how the last update went ([deployment.md](deployment.md) §6). Uptime, connections, CPU, memory, requests and errors for the last 24 hours. |
| Add-on | The released add-on, and an uploaded one waiting to be released or discarded ([onboarding.md](onboarding.md)). |
| Website | News posts and release notes (draft, publish, schedule), the announcement bar, maintenance mode (visitors get a "back soon" page; the admin page, invite downloads and editors keep working) and the early-access list (export as CSV, remove on request). |
| Audit log | Who did what: staff sign-ins and changes, codes used and tried, teams made with access codes, accounts, website posts, deleted projects. |

The page refreshes every 15 seconds. A part is redrawn only when its data
changed, and never while you're filling in a form in it.

Connections are recorded in `session_log` (person, project, add-on version,
start, last seen, end) for the statistics.

## 3. Projects

Deleting a project removes everything in it: its operation log, chat,
comments, access settings, invite codes and links. It is the one place rows
leave the log, so the project must be archived first, nobody may be
connected, and its exact name has to be typed. The audit log keeps a
`project.deleted` record with the name and counts. Files no other project
uses are freed afterwards; files younger than a day are kept in case an
upload is still running.

A project from a game zip: the zip holds a Godot project (the folder with
`project.godot`, at any depth). Every file becomes an ordinary
`RegisterAsset` operation, as if an editor had shared it. `.godot/`, the YHDE
add-on, hidden folders and names Windows can't store are left out, and the
page lists what was left out. Without a name, the game's name from
`project.godot` is used. Up to 16 GB per zip, each file up to the server's
largest allowed file.

Move to a team makes a project made on this page one of a team's, so its
people see it on their dashboard and in Godot.

## 4. Codes and Test Accounts

Promo codes. Only staff see them. A code has start and end dates, the plans
it works on, what it gives (free months, percent off, euros off, extra seats,
extra storage), for how many months, a use limit, "only teams made after the
code" and an on/off switch. Owners type codes on the Billing page
([teams.md](teams.md) §5). A code nobody has used can be deleted; a used one
can only be switched off.

Access codes. During the beta, making a team needs a code with "Lets someone
make a team" ticked. New access code makes one that gives nothing else and
works once; set a higher use limit for a group. It is typed on the "Make your
team" page, the team exists only if the code works, and each team made uses
it once. Invited people need no code. See [teams.md](teams.md) §2.

Test accounts. Accounts, New test account: a confirmed account on the reserved
`@test.yhde` domain (no email goes there), marked Test, optionally put in a
project. Its password is shown once. Remove all test accounts deletes those
that own no projects.

## 5. Security

- The session is a random token in an HttpOnly, `SameSite=Strict` cookie for
  `/admin`, valid for 12 hours.
- Five wrong server passwords from one address lock it out for 15 minutes.
- Every change needs the `X-YHDE-Admin` header, which another website can't
  send.
- The page and its script come from the server itself with a strict
  Content-Security-Policy (`default-src 'self'`, no framing). All text from
  the database is escaped.
- Invite codes and link addresses exist in plain text only in the response
  that makes them; the server keeps SHA-256 hashes.
- Every change is written to the audit log with who made it.

## 6. API

All under `/admin/api`, JSON. The main routes:

| Route | |
|---|---|
| `POST /login`, `POST /login/account`, `GET /login/site`, `POST /logout` | Sign in with the server password, an account, or the website session; sign out. |
| `GET /me` | Who is signed in and their role. |
| `GET /overview`, `GET /stats` | The Overview and chart data (stats cached 30 s). |
| `POST /projects`, `POST /projects/{id}`, `POST /projects/import?name=`, `POST /projects/{id}/delete`, `POST /projects/{id}/team` | Make, rename, archive, import, delete, move a project. |
| `GET/POST /projects/{id}/invites`, `POST /invites/{id}/revoke`, `POST /invites/{id}/delete` | Invite codes. |
| `POST /projects/{id}/links`, `POST /links/{id}/revoke`, `POST /links/{id}/delete` | Download links. |
| `GET /users`, `POST /users/{id}/role`, `.../disable`, `.../sign-out`, `.../resend-confirmation`, `.../delete` | Accounts. |
| `POST /test-accounts`, `POST /test-accounts/delete-all` | Test accounts. |
| `GET /teams`, `GET /teams/{id}` and changes | Owners and their plans. |
| `POST /projects/{id}/members {email}`, `POST /projects/{id}/members/{userId}/remove` | People in a project. |
| `GET /promos`, `POST /promos`, `POST /promos/{id}`, `POST /promos/{id}/delete` | Promo and access codes. |
| `GET /audit` | The audit log. |
| `GET /addon`, `POST /addon`, `POST /addon/release`, `POST /addon/discard` | The add-on. |
| `GET /server-update`, `POST /server-update`, `POST /server-update/check` | Updates. |
| `GET/POST /posts`, `POST /posts/{id}`, `POST /posts/{id}/delete` | News and release notes. |
| `GET/POST /site-settings` | Announcement bar and maintenance mode. |
| `GET /early-access`, `GET /early-access.csv`, `POST /early-access/remove` | The early-access list. |

The public site has `GET /api/site`, `GET /api/posts?kind=` and
`POST /api/early-access`.

## See Also

- [teams.md](teams.md), [projects.md](projects.md), [deployment.md](deployment.md),
  [security.md](security.md)
- [ADR 0016](adr/0016-people-belong-to-projects.md): people belong to projects, not teams

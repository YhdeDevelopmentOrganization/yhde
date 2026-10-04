# Owners, Projects and People

Who can make projects, who is in each project, plans, invitations, view
links and promo codes. Signing in is in [authentication.md](authentication.md).
Code: `server/src/YHDE.Server/Teams/`. (The file keeps its old name; there
are no teams any more.)

## 1. The Model

People belong to **projects**, not teams (migration 014).

- **Owners.** Someone who used an access code can make projects. Their plan,
  storage, promo codes and free seats are one `teams` row
  (`TeamStore.OwnedAsync`); every project they make points at it
  (`projects.team_id`). Projects made on the admin page belong to nobody
  until staff move them to an owner.
- **Members.** Everyone else in a project is there because its owner invited
  them (`project_members`, with `edit` or `view` access). Anyone can be in
  any number of projects, of any number of owners, and needs no access code
  for that. Joining one never takes anyone out of another.
- The owner decides everything about a project: who is in it, their access,
  view links, rename, image, archive, delete, and handing it over. Members
  open it, see who else is in it, and can leave. There are no admins.
- **Handing a project over.** The owner can make a member the owner (they
  must be able to make projects themselves, with room for one more project
  and its files). It moves under their plan; the old owner stays in it as a
  member who edits. The new owner gets an email.
- Removing someone, lowering their access or them leaving closes their open
  connections at once.

### Invitations

An invitation is to one project, by email address. It can be accepted two ways:

- on the person's dashboard (every page), signed in with that address once it
  is confirmed; accounts without a real inbox, such as test accounts, join
  this way;
- from the email's link, `/app#/invite/<secret>`. The secret is 256 random
  bits, kept only as a SHA-256 hash (`team_invites.token_hash`, migration
  013); opening the link proves the email arrived, so the person may accept
  signed in with any account. The page names the sender's account and
  address as they are on the site, so the person can check who sent it.
  Opened signed out, it comes back after signing in or making an account.

An invitation lasts 14 days from when it was last sent; then it holds no
place and can't be accepted. The project's People panel shows each with
Resend (a new link, 14 more days; the old link stops working) and Withdraw.
One that ran out stays listed for 30 days, so it can be resent.

### Join Codes

A project's owner can make a join code on its People panel: 8 characters
like `K7QM-2XRD` (`projects.join_code`, migration 015). Anyone signed in who
enters it under Join a project on their Projects page
(`POST /api/team/join`) becomes a member who edits, while the project has
room; no email is needed. Entering it on the `/join` page, or opening
`/app#/join/<code>`, leads to that form with the code filled in (after
signing in). New code replaces the old one; Turn off stops it. Tries are
limited per account and per address, like promo codes. Archived projects
take nobody in. The owner gets an email when someone joins this way.

### Email

No email goes to `.test` addresses. Otherwise:

| When | Who gets it |
|---|---|
| Someone is invited (or it is resent) | the invited address, with the accept link |
| An invitation is accepted or declined | the project's owner |
| A member leaves | the project's owner |
| Staff add someone straight into a project | that person |
| A project is handed over | the new owner |

## 2. Beta: Making Projects Needs an Access Code

Nothing is charged during the beta, but the site is public. So starting to
own projects (`POST /api/team`) needs an access code: a promo code with "Lets
someone make a team" ticked (`unlocks_teams`), made on the admin page.

- The owner row is made and the code is used in one transaction. A refused
  code leaves nothing behind, and a code's use limit caps how many owners it
  makes.
- Tries are limited per account and per address, and every try goes to the
  audit log.
- Being invited into a project needs no code.
- This goes away when payments start: `./yhde set TEAMS_NEED_CODE false`
  (`Yhde:TeamsNeedCode`).

## 3. Plans and Limits

The server enforces the plans in `TeamStore.cs` (`Plans`). The website shows
the same numbers from `server/admin-ui/src/world/plans.ts`, and
`server/admin-ui/src/world/plans.ts` has the prices. A plan's seats count the owner,
so the people each project may have besides its owner are seats − 1.

| | Projects | Storage (all projects) | People per project | Extra seats up to |
|---|---|---|---|---|
| Beta tester | 3 | 2 GB | 3 | 0 |
| Solo | any | 2 GB | 0 | 2 |
| Trio | any | 5 GB | 2 | 3 |
| Team | any | 15 GB | 5 | 6 |
| Studio | any | 40 GB | 11 | 12 |

- **Projects and storage: whichever comes first.** A beta tester can own 3
  projects (archived ones count: they keep their files) and 2 GB for all of
  them. Making or importing a project is refused when either is used up.
- **Storage is enforced as files arrive from Godot** (`StorageQuota`): a new
  file that would take the owner over is refused with a clear reason (the
  editor keeps it locally and says why); a changed file only once they are
  already over. The dashboard and the Godot panel warn from 80 % on.
- **People per project**: members plus invitations still open. Each extra
  seat (paid, from a promo code, or free from staff: `teams.free_seats`)
  adds one more person to every project of that owner.
- **Viewers per project: 5** (`Plans.ViewersPerProject`), from view links,
  separate from the people above (§4).
- Until 1.0.0 everyone is on **Beta tester** (`Plans.Open` is false while
  `ServerInfo.Version` is below 1.0); plan, seat and storage changes are
  refused. A beta owner takes any promo code.
- Moving to a smaller plan keeps everyone: missing seats become extra seats.
  It is refused if the owner's projects wouldn't fit even then.

## 4. View Links

A view link downloads one project, ready to open in Godot, for someone to
watch it being made: a playtester, a friend. They need no account and see
every change live, but can't change anything: the invite codes a view link
makes open the project read-only (`IProjectStore.CodeIsViewOnlyAsync`;
`AccessGate` gives them a read-only grant). Codes made on the admin page
still edit.

- The owner says how many people a link lets in (1 to the viewer places
  left) and for how long. A link holds its places until it is turned off:
  one per download so far and, while it works, one per download left
  (`TeamStore.ViewersAsync`).
- Turning a link off (or removing it) frees its places and turns off every
  code it made.

More about how the download works in [onboarding.md](onboarding.md) §3.

## 5. Access per Project

| In a project | May |
|---|---|
| Owner | Everything about it. Always edits. |
| Member, can edit | Works in it: edits, chats, comments. Can leave. |
| Member, can view | Sees everything, chats and comments; every change is refused. |
| Viewer (view link) | Watches in Godot. Every change is refused. |

A sign-in only opens projects the person owns or is a member of
(`EditorTokens`, from `TeamStore.ProjectsForAsync`), and view access refuses
operations and undo. The Godot panel says "View only" once instead of
warning about each refused change. More in [authority.md](authority.md).

## 6. Promo Codes

Staff draft codes on the admin page ([admin.md](admin.md) §4). An owner types
one on the Plan page. A code can give free months, a percentage or an amount
off, extra seats or extra storage, for a number of months; it can be limited
to plans, dates, new owners and a number of uses.

- Codes are never listed anywhere a customer can see them.
- Every wrong try gets the same answer, so nobody can find out which codes
  exist. Tries are limited per owner and per address.
- Extra seats and storage count at once. The other benefits are recorded now
  and applied when payments start.
- An owner can use each code once. The Plan page shows what they got, never
  the code.

## 7. Signing In From Godot

The panel signs in through the website ([authentication.md](authentication.md)
§4) and then lists every project the person can open
(`GET /api/editor/projects`: their own and the ones they were invited to, with
their role, the owner's name and the owner's storage). Clicking one connects
the folder to it. For the connected project the panel shows who is in it; its
owner can invite people there and withdraw invitations, and copy a view link.
Someone who can make projects can make one from the folder.

- The connection is the account: the server sets the member id and name
  from it.
- There is no server field. The add-on uses YHDE's server (`DEFAULT_SERVER`
  in `main.gd`); `YHDE_SERVER` overrides it for development.
- Add-on 0.4 still works for opening projects; its old team box is empty and
  its team invitations are answered with "do it on the project's page"
  (410).

## 8. The API

`TeamEndpoints.cs`, all under `/api`. Every POST needs the `X-YHDE` header
and this site's `Origin` ([security.md](security.md) §4).

- `GET /api/dashboard`: the person, their plan (if they can make projects)
  with usage, every project they can open with its people, limits and real
  statistics (files, bytes, changes, changes per day for 30 days, the latest
  activity with the editor's name), invitations and view links for the ones
  they own, invitations to them, and who is connected. Statistics are cached
  for 5 seconds per person.
- The owner's plan: `POST /api/team {code}` (start owning), `/team/plan`,
  `/team/seats`, `/team/storage`, `/team/promo`.
- A project, owner only: `/team/projects/{id}/rename|archive|delete|links|transfer`,
  `/team/projects/{id}/invites`, `.../invites/{inviteId}/resend|cancel`,
  `/team/projects/{id}/members/{userId}/remove|access`,
  `/team/projects/{id}/image` (PNG, JPEG or WebP body, up to 2 MB, told apart
  by its first bytes), `.../image/remove`, `/team/links/{id}/revoke|delete`.
- A project, anyone in it: `GET /team/projects/{id}/image` (the dashboard
  gives its address with a `?v=` stamp, so it can be cached; without one the
  website shows `public/media/project-default-wide.png`),
  `GET /team/projects/{id}/activity?who=` (the latest 200 changes, optionally
  by one person, and who made changes), `POST /team/projects/{id}/leave`
  (members).
- New projects (anyone who can make them): `/team/projects`,
  `/team/projects/import?name=` (zip body).
- Invitations: `/invitations/{id}/accept|decline`; the email's link:
  `GET /invitations/by-token/{token}` (no sign-in), `POST .../accept`
  (signed in), `POST .../decline`.

## 9. Your Data (GDPR)

- `GET /api/me/export` downloads everything stored about the account,
  including which projects they are in and as what.
- `POST /api/me/delete {email}` deletes the account, its sign-ins and its
  place in every project, and clears its name from the editor history. Someone
  who owns projects must hand them over or delete them first, so nothing is
  lost by accident. It is written to the audit log.
- Both need the website session; an editor sign-in can't do them.

## See Also

- [authentication.md](authentication.md), [authority.md](authority.md),
  [projects.md](projects.md), [onboarding.md](onboarding.md), [admin.md](admin.md)
- [ADR 0016](adr/0016-people-belong-to-projects.md): why people belong to projects

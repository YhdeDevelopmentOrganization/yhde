# Changelog

What changed in each YHDE release. The website's "What's new" page shows the
same history (`server/admin-ui/src/site/pages/Changelog.tsx`), plus release
notes published on the admin page.

## v0.6.5 (2026-10-10)

A stability release. Update the server and the add-on together: an add-on
older than 0.6.5 can still open projects and download files, but a signed-in
one must update before it can share new files.

### Godot add-on

- No more editor crash after editing a scene that is instanced in another
  open scene. Switching back to that scene's tab, or pressing Play, used
  nodes that had already been freed.
- One entry per teammate: a reconnect no longer leaves a second cursor and
  avatar behind.
- Changes the server could not save for a moment are sent again instead of
  being dropped, and re-syncs after a gap back off instead of all editors
  asking at once.
- Applying a big batch of changes no longer freezes the editor: each frame
  gets a time budget.
- Files that disappeared while the editor was closed (a branch switch, an
  unplugged drive) are only deleted for the team after you confirm, also in
  small projects.
- YHDE's record of your files is written safely and kept with a backup; a
  damaged record no longer makes every file look new.
- File names the server would refuse are reported in the panel instead of
  never syncing. Names that differ only in capitals (Player.gd and
  player.gd) are refused, because Windows and macOS treat them as one file.
- Code from teammates is checked by what a file is, not its name:
  compressed scenes and resources, other resource types (.material, .theme,
  .anim) and every C# spelling of [Tool] are held for your approval. A
  changed autoload in project.godot is pointed out.
- Your sign-in never travels over plain http:// or ws:// to another
  computer, a redirect can no longer sign you out, only the server's own
  pages are opened in the browser, and the sign-in file is readable by you
  only. A starter project that names an unfamiliar server asks first.
- Editor classes are never created from a teammate's change, and oversized
  messages are refused before they use much memory.

### Server and website

- One slow or vanished connection can no longer hold up a whole project:
  each editor has its own send queue, and silent connections are closed
  after 60 seconds.
- Changes committed while someone joins are no longer missed, and undo
  keeps the log's order.
- A malformed request or a short database problem is answered instead of
  closing the connection.
- Files are served and accepted only for the project they belong to, and
  view-only access cannot upload. Uploads count toward the owner's storage
  as they happen, and uploads stop before the disk runs full (the admin page
  warns first). Unused uploads are cleaned up.
- A project import checks the unpacked size and cleans up after itself if
  it fails.
- Limits on new connections per address, a lock around database migrations,
  and the old domain forwards to the site again.

## v0.6.0 (2026-10-04)

YHDE is open source now, and anyone can run their own server
(docs/setup_your_server.md). Everyone in a project should move to this
add-on together: an older add-on misses tiles and layers that a newer one
removes. Then update the server.

### Godot add-on

- TileSets sync properly: new sources, tiles, alternative tiles, collision
  polygons, terrains, custom data, navigation and occlusion all reach your
  teammates, and so do removed and moved tiles, removed layers and patterns.
  Painting on a TileMapLayer no longer bounces back from teammates.
- The old TileMap node syncs added and removed layers and keeps layer names.
- Removed curve points reach teammates.
- Files from teammates that can run code on your computer (plugins, native
  libraries, @tool scripts, build files) wait in the YHDE panel until you
  accept them. "Always trust this project" accepts from then on.
- Add-on updates are installed only when they are signed as a YHDE release,
  whichever server offers them, and never an older version.

### Server and website

- A server you run yourself shows your own name, contact and privacy and
  terms pages, has no plans, prices or limits beyond its disk, and says
  "Powered by YHDE". YHDE's own site sets OFFICIAL_SITE and is unchanged.
- New server settings: OFFICIAL_SITE, OPERATOR_NAME, OPERATOR_EMAIL,
  PRIVACY_URL, TERMS_URL.

## v0.5.0 (2026-09-28)

Update the server before handing out this add-on: it needs the new project
API. The server's database migrates itself (013 and 014) on start; add-on
0.4.3 keeps working with the new server.

### Godot add-on

- The panel shows the people in the project you're connected to. The
  project's owner invites people there and withdraws invitations.
- Your projects and the ones you were invited to are all listed.
- A warning when the project owner's storage is 80 % used, and when it's
  full, new files you add are refused with a clear reason.
- "Copy a view link" (was invite link) is there for projects you own.

### Website

- People belong to projects now, not teams. With a beta access code you can
  make up to three projects and 2 GB, whichever comes first, and invite three
  people into each. Anyone can be in any number of other people's projects,
  without a code. Existing teams became projects with the same people in
  them; nobody was removed.
- Beta tester plan: free, it is the only plan until YHDE 1.0; the paid plans
  and prices show from then on.
- Projects can have an image. Without one they show the YHDE "No image yet"
  picture.
- New Security and Cookies pages. The privacy policy and terms describe the
  beta: nothing is paid, what is stored, for how long, and who processes it.
- The GitHub link goes to the add-on. Discord, YouTube and X say they don't
  exist yet when clicked.
- Your data is never used to train AI: said plainly on the privacy page, in
  the terms, the FAQ and the plan card.
- Signed in, the site's header shows your avatar with Projects,
  Account and Sign out, and the Sign in and Start pages go straight to the
  dashboard. Sign out is in the dashboard on phones too.
- Privacy you can do yourself: "Download my data" now includes the messages
  and comments you wrote and your early-access sign-up; anyone can leave the
  early-access list from the privacy page; deleting an account removes its
  early-access sign-up too.
- Invitations are to a project, and can be accepted from the email's link or
  from the dashboard, where they show on every page (test accounts without a
  real inbox join this way). The link's page shows who sent it and from
  which address. They last 14 days, and can be resent or withdrawn.
- Each project has a People panel: invite, resend, withdraw, set who can
  edit or only view, remove someone, or hand the project to someone else.
  Members can leave a project themselves.
- Emails: the owner hears when an invitation is accepted or declined and
  when someone leaves; people added by staff and new owners are told too.
- View links (were invite links): up to five people per project can watch it
  in Godot without an account, but not change anything. Turning a link off
  cuts off everyone who used it.
- Warnings before limits: the dashboard and the Godot panel say when 80 % of
  your storage is used; when it's full, new files from Godot are refused with
  a clear reason.
- Activity on a project's page can show one person's changes.
- The New project button hides while the new project form is open.
- The account avatar in the header is a plain round avatar.
- The front page explains "separate copies" without a Git merge conflict.

### Admin page

- In the editor: charts per hour, day, week, month, 3 months, year or all
  time, of active people, hours online or changes. Deleted accounts no
  longer show there.
- Owners (was Teams): each owner's projects and who is in them. Staff can add
  an existing account straight into a project, and give free seats (one more
  person in each of the owner's projects).
- Test accounts can be put straight into a project.

## v0.4.3 (2026-09-27)

### New

- The YHDE logo in Godot: on the panel's tab and in the top bar, where it
  also opens the panel. It keeps its colors in light and dark editor themes.

### Server

- Early access: making a team needs an access code, made on the admin page
  (Promo codes, New access code). Invited people join without one. This is
  for the beta only; it goes away when payments start.

## v0.4.2 (2026-09-27)

### New

- Roles: team admins manage projects and people; the owner keeps the plan.
- Access per project: can edit, can view, or no access. View-only people can
  look, chat and comment; the server keeps their changes out.
- Choose which notifications pop up in Godot (the panel's menu: Notify me
  about): direct messages, mentions, comments, "bring everyone here", sync
  info. Warnings and errors always show.
- Installing is one file: in Godot, AssetLib, Import…, pick the zip.

### Server

- Test accounts, made on the admin page (Accounts, New test account).
- Fixed: on a server without a key (development), an editor sign-in was
  treated as the open server instead of the account.

## v0.4.1 (2026-09-27)

### Fixed

- Turning YHDE on no longer disturbs your docks or the 3D view. The panel
  asked Godot for about 1,900 px of height (wrapped text in a dock tab that
  isn't in front is measured at almost no width), so Godot squeezed the other
  docks and stretched the view. It now scrolls inside its dock and needs
  about the same room as Godot's own docks.

### New

- Run your team from Godot: new project from this folder (its files go up),
  copy an invite link, see who is in the team and who is here, invite by
  email, cancel invitations. A Team tab while connected.
- Removed from a team? Your open connection closes at once.

### Server

- Admin page: staff sign-in with your account, Accounts, Teams, Promo codes
  and Audit log screens, and moving projects to teams.
- "Have a code?" on the Billing page.

## v0.4.0 (2026-09-27)

Update the server first (`./yhde update`): this add-on signs in through
the server's new sign-in pages.

### New

- Sign in to YHDE from Godot: press "Sign in with your browser" in the YHDE
  panel, allow it on the website, done. No passwords in Godot.
- Your team's projects as cards in the YHDE panel, with who is in each one.
  Click one to connect; a new, empty project folder gets the game's files.
- No server address or codes to type: YHDE connects to its own server.
- Your name in Godot is your account's name, so nobody can pose as someone
  else. Reopening Godot reconnects on its own.
- Invite links still work: a starter project offers to join with its invite.

### Server

- Sign-in for editors (a code you allow on the website), the project list for
  Godot, and connections that open exactly your team's projects.
- Admin: staff accounts (admin and support), users, teams, audit log and promo
  codes on the server (the admin page screens come next).
- The guide describes the new way in; the dashboard links the Godot add-on.

## v0.3.1 (2026-09-27)

### Fixed

- The editor no longer stutters with YHDE on. Moving, panning and dragging in
  your own editor were jagged, even offline, because YHDE looked through the
  whole editor interface four times a second to place teammates' avatars. It
  now finds those places once. Measured in Godot 4.7.2: frames over 25 ms
  while dragging went from 8 in 240 to 1, the same as without YHDE.

### Server

- Accounts and teams: sign up with email, GitHub or Google; make a team or
  join one you're invited to by email; the dashboard shows your real
  projects, statistics and who is connected.
- Plans Solo, Trio, Team and Studio, extra seats and storage, enforced by the
  server. Nothing is charged during the beta.
- Security: rate limits on every website action and per address, names that
  can't impersonate YHDE or carry links, safer sign-in redirects, security
  headers, invite links and codes can be deleted.
- Your data (GDPR): download it, or delete your account, on the Account page.
- The new website: home page, docs, news, status, privacy and terms, the new
  logo, search engine and link-preview support.

Update the server first (`./yhde update`); this add-on works with both.

## v0.3.0 (2026-09-26)

Invite links and starter projects, projects from a zip, add-on updates from
the YHDE panel, live shaders, "bring everyone here", and the new website,
dashboard and admin page.

## v0.2.0 (2026-09-25)

Projects with invite codes, the admin page, live script editing, project chat
and comments.

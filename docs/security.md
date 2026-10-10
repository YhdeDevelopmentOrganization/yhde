# Security

The threats YHDE plans for and what stops each one. Who may do what is in
[authority.md](authority.md); signing in is in
[authentication.md](authentication.md).

## 1. The Stance

- Clients are never trusted. Every input is checked on the server.
- The server decides. A client can't do anything the server doesn't allow
  and check ([ADR 0002](adr/0002-server-authoritative.md)).
- The native core is C++ for speed. It is not a security measure.
- Any client can be cracked. That must not matter: a modified client gets no
  extra rights and can't damage a project, because the server checks
  everything again.

## 2. Threats

| Threat | What stops it | More |
|---|---|---|
| A modified client | The server checks access and validity of every operation. | [operation_system.md](operation_system.md) |
| A faked identity in a message | Who someone is comes from their sign-in, never from the payload. | [authentication.md](authentication.md) |
| Doing more than allowed | Team role and project access checked on every operation and request. | [authority.md](authority.md) |
| Eavesdropping | TLS on every connection. | Section 3 |
| A stolen sign-in | Tokens are random, stored only as hashes, can be ended one by one or all at once. | Section 4 |
| Bad or huge input | Validation and size limits at the gateway and the API. | Section 5 |
| Flooding | Rate limits per address, per account and per session. | Section 6 |
| Changing history | Append-only log with a hash chain. | Section 7 |
| Poisoned files | Files are stored by hash and every transfer is checked. | [assets.md](assets.md) |
| A hostile server or teammate sending code | Files that can run in the editor wait until the person accepts them. | Section 11 |
| A fake add-on update | Updates must carry YHDE's release signature, whoever hands them out. | Section 11 |
| Losing data | Transactions, stored before broadcast, daily backups. | [reliability.md](reliability.md) |

## 3. Connections

- Everything goes over TLS on a live server: WSS for editing, HTTPS for
  files, the website and sign-in. Caddy gets and renews the certificates
  ([deployment.md](deployment.md)).
- Caddy adds `Strict-Transport-Security`, `X-Frame-Options: DENY`,
  `X-Content-Type-Options: nosniff` and a `Permissions-Policy`, and hides the
  `Server` header. The website's pages set a strict
  `Content-Security-Policy`.
- The add-on warns when someone uses a plain `ws://` address, because the
  secret would travel unencrypted.

## 4. Sign-ins

Details in [authentication.md](authentication.md).

- Passwords are hashed with Argon2id (19 MiB, 2 passes, a random salt each).
  Unknown emails take as long to check as real ones, so response times don't
  reveal which accounts exist.
- Website sessions and editor sign-ins are random tokens. The database keeps
  only their SHA-256 hash.
- Every session can be ended from the account page, and the admin page can
  sign someone out everywhere. Ending an editor sign-in closes that editor's
  connection at once.
- Every POST to the API needs the `X-YHDE` header and this site's `Origin`, so
  other sites can't submit forms for a signed-in person.
- `POST /api/early-access/remove` takes an address off the early-access list
  and answers the same whether or not it was there, so the list can't be
  probed.
- Project images are only PNG, JPEG or WebP, checked by their first bytes
  (never SVG, which can carry scripts), at most 2 MB. They are served with
  `nosniff` and a sandboxing `Content-Security-Policy`, only to people who
  can open the project.

## 5. Input

- Every frame is checked at the gateway: envelope, known type, size.
- Every operation is checked by the server: known type, fields present, sizes
  within limits, target present.
- `actor_id`, `seq`, `session_id` and `created_at` are always set by the
  server. Anything the client sends for them is ignored.
- Anything not clearly valid is refused.

## 6. Rate Limits

- Every address gets at most 300 requests a minute to `/api`, `/auth`,
  `/join`, `/admin/api` and `/early-access` (`Program.cs`). Editors' file
  routes and the WebSocket are not counted there.
- Each action also has its own limit: sign-in per address and per email,
  sign-up, password resets, join codes, early access, promo and access codes
  (per team or account and per address), and on the dashboard reads, changes,
  new projects, new links, invitations (per owner, and per invited address so
  nobody can be flooded with email) and account deletion.
- Presence is limited to 30 updates a second per session.
- `Limiter` only removes entries whose time window has passed, and refuses
  new keys while its table is full. So nobody can wipe the counters by trying
  many random keys.

## 7. History and Audit

- The log is only ever added to, and each operation's signature includes the
  one before it ([database.md](database.md) §3). Changing an old operation
  breaks the chain from there on.
- Files are stored by hash and checked on every transfer.
- The audit log records sign-ins to the admin page and every staff change,
  codes used and refused, teams made with access codes, accounts, website
  posts and deleted projects ([admin.md](admin.md)). Together with the
  `actor_id` on every operation, it shows who did or tried what.
- Chat and comments are never hard-deleted. Editing or removing a comment
  writes the old text to the audit log ([social.md](social.md)).

## 8. Names and Addresses

- People, teams, projects and link labels lose control characters, invisible
  and direction-changing characters and extra spaces, and are cut on whole
  characters (`Accounts/NameRules.cs`).
- Names of people and teams reach other people's inboxes, so they may not
  contain web or email addresses or look official: YHDE, Admin, Support,
  Staff, Moderator and so on, also when spelled with look-alike digits or
  Cyrillic letters. An editor with such a name shows as "Guest".
- Chat and other text lose direction overrides.
- After signing in, the site only returns to paths on itself: no second slash,
  backslash, space or control character.

## 9. Invite Links and the Server Key

- An invite link's code opens one project. Codes are 80 random bits and
  stored only as SHA-256 hashes ([projects.md](projects.md)). Codes go in the
  join form's body, never in URLs, and the join page runs no scripts.
- The server access key opens every project and stays with the operator.
- A folder's first connection to a project that doesn't match it waits for
  the person to confirm, so two games can't get mixed.

## 10. The Admin Page

- Staff sign in with their own accounts; the role is Admin or Support.
  Support can look and help people (sign them out, resend a confirmation) but
  change nothing else. The server password is only an emergency way in, and
  its use is logged.
- The admin session is an HttpOnly, SameSite cookie, and every change needs
  an extra header. Five wrong server passwords from one address lock it out
  for 15 minutes ([admin.md](admin.md)).

## 11. Code From Others, Add-on and Server Updates

Anyone can run a YHDE server, so the add-on does not trust the server it
connects to with running code on the person's computer.

Shared files that can run code inside the editor are held, not written,
until the person accepts them in the YHDE panel (`AssetSync`):

- Native code and build files: `.gdextension`, `.dll`, `.so`, `.dylib`,
  `.exe`, scripts like `.bat`/`.sh`/`.ps1`, MSBuild files (`.csproj`,
  `.props`, `.targets`, `.sln`) and anything inside a `.framework` or `.app`.
- Scripts that run in the editor: GDScript with `@tool` and C# with
  `[Tool]` (in any spelling the compiler accepts, such as `[ Tool ]` or
  `[Godot.Tool]`), and tokenized GDScript (`.gdc`), which cannot be read as
  text.
- Resources with a `@tool` script built in. A file counts as a resource by
  its content, not its name: anything that starts with `RSRC` or `[gd_` is
  searched, whatever its extension (`.material`, `.theme`, `.anim` ...),
  because Godot loads a resource by its header. Compressed binary resources
  (`RSCC`) hide their content, so they are always held.
- `project.godot` is applied as it comes. When it changes the autoloads, the
  panel says so, because their scripts run when the game is played.
- Bytes already in the project as code (a move, a copy) are not held; a
  harmless file renamed into code (`notes.txt` to `lib.dll`) is.
- While held, the file is neither written nor treated as a local change, so
  holding never deletes or reverts it for anyone. Accepting writes it with
  its `.uid`/`.import`, so its uid matches the team's.
- "Always trust this project" (or the Advanced setting) accepts from then on.
- Limits: this guards against surprise native code and editor scripts, not a
  sandbox. A plain script called by an accepted `@tool` script runs too.

The join page and add-on updates ([onboarding.md](onboarding.md)) hand out
the add-on, uploaded by staff on the admin page
([ADR 0013](adr/0013-addon-from-the-server.md)).

- An add-on update is only offered, never installed without a click. The
  package's SHA-256 must match the server's manifest and every file must be
  under `addons/yhde/`, or nothing is written.
- The checksum comes from the same server, so it only catches damage. The
  package must also carry `addons/yhde/release.sig`, an RSA signature over
  its version and every file's SHA-256, made with YHDE's release key
  (`client/package_addon.py --sign`) and checked against `RELEASE_KEYS` in
  `updater.gd`. Any server can hand out releases; none can make one. An
  update that is not newer than the installed add-on is refused too, so a
  server can't push back an old release with a fixed bug.
- The release key's private half never goes on a server or into a
  repository. A fork that builds its own add-on puts its own key in
  `RELEASE_KEYS`.
- The first copy of the add-on (the join page's starter project) cannot check
  itself: get it from YHDE's releases, or from a server you trust.
- A staged add-on reaches nobody until it is released.
- The server can't update itself. The admin approves an update, and the
  server kit's timer on the host applies it: a backup first, and a rollback if
  the new version doesn't come up ([deployment.md](deployment.md)).

## 12. Signing Builds

Signing the native libraries and checking them in the editor would make it
harder to pass around a modified build. It is not done yet, and it would
never replace the server's checks.

## 13. Rules

1. Every client input is checked and allowed on the server.
2. Fields the server sets are never taken from the client.
3. Every connection on a live server uses TLS.
4. History is append-only and changes to it show.
5. Anything not clearly valid is refused.

## See Also

- [authentication.md](authentication.md), [authority.md](authority.md),
  [social.md](social.md), [projects.md](projects.md), [admin.md](admin.md),
  [network_protocol.md](network_protocol.md), [reliability.md](reliability.md)

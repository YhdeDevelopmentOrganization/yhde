# ADR 0015: Accounts on the Website; the Editor Signs In Through It

- Status: Built
- Date: 2026-09-26
- See also: [authentication.md](../authentication.md), [security.md](../security.md),
  [projects.md](../projects.md), [ADR 0011](0011-projects-and-invite-codes.md),
  [ADR 0013](0013-addon-from-the-server.md)

## Situation

YHDE is sold as a hosted subscription: a team owner pays, teammates join free.
Until now there were no accounts. Editors connect with a shared server key or a
project invite code, the admin page has one password, and nothing on the
website knows who a person is. Selling, per-team projects, storage limits and
revoking one person all need real identities.

[authentication.md](../authentication.md) planned website-based sign-in
(email/password and OAuth) with the editor receiving tokens from the web flow.
It assumed signed JWT access tokens.

The whole product runs on one small server (a Hetzner CX23) that is far from
busy, and must stay cheap: no paid identity service.

## Decision

1. Accounts live in our PostgreSQL, on the same server as the sync
   server and website. The website and its API are served from the same
   origin as everything else; no separate hosting (Vercel was considered and
   rejected: its free plan forbids commercial use, and a second origin makes
   cookies and CSRF harder).
2. Sign-in methods: email + password, GitHub, Google. OAuth runs on the
   server (authorization code flow with PKCE and a one-time `state`); provider
   secrets never leave the server. An OAuth identity is linked to an existing
   account only when both sides have the same verified email.
3. Passwords are hashed with Argon2id (19 MiB, 2 passes, 1 lane:
   OWASP's recommendation) with a random salt per password, and never logged
   or returned. Hashing runs at most four at a time so logins cannot exhaust
   memory.
4. Website sessions are random 256-bit tokens in an `HttpOnly`, `Secure`,
   `SameSite=Lax` cookie. The database keeps only their SHA-256, with expiry,
   last use and device, so every session can be listed and ended. Requests
   that change something also need a custom header (`X-YHDE`) and a matching
   `Origin`, which other sites cannot send.
5. Opaque tokens instead of JWTs. Access and refresh tokens are random
   and stored hashed, like sessions. This replaces the JWT plan in
   authentication.md §3: revocation takes effect at once, there are no signing
   keys to manage or leak, and the only cost is one indexed lookup when an
   editor connects, which this server does rarely.
6. The editor signs in through the website with the device authorization
   flow (RFC 8628, as GitHub CLI does): the YHDE panel shows a short code and
   opens the browser; the person signs in on the website and approves; the
   editor receives a refresh token (kept outside the project folder) and
   short-lived access tokens. The server key and invite codes keep working
   during the move.
7. Guessing is limited: sign-in, sign-up, reset and verification are rate
   limited per address and per account; answers never reveal whether an
   email has an account.
8. Email (verification, reset) goes through any SMTP service (a free tier
   is enough); without one configured, links are written to the server log so
   the operator can still bootstrap.

## Why

- One server, one origin, one database: the cheapest setup and the one with
  the fewest places for a security mistake.
- Argon2id is the current recommendation for password storage; PBKDF2 would
  work but costs attackers far less on GPUs.
- Hashed opaque tokens give instant revocation, which matters more here than
  saving a database lookup per connection.
- The device flow keeps passwords and OAuth out of the Godot editor entirely,
  and works the same for every sign-in method.

## Consequences

- Gains: real identities for billing, teams, quotas and per-person
  revocation; no identity-service bills.
- Gains: the admin password can later become an "operator" role on an
  account.
- Cost: we own account security (resets, abuse, email
  deliverability). Two-factor sign-in is not in the first version.
- Cost: a production domain is required for OAuth and email; the
  domain is one setting (`Yhde:PublicUrl`), so moving from the temporary one
  is a configuration change.

## Turned Down

- A hosted identity provider (Auth0, Clerk, Supabase Auth). Faster to
  start, but a monthly bill as the product grows and another party holding
  every customer's identity.
- ASP.NET Core Identity. Solid, but brings its own schema, UI assumptions
  and EF Core; the project uses Dapper and hand-written migrations.
- JWT access tokens (the original plan). Stateless checks are not needed
  at this scale, and a stolen token could not be revoked before it expired.

## Changes Since

The editor gets one editor token that lasts 90 days, not a refresh token with
short-lived access tokens. It is stored hashed and can be ended from the
account page, which closes the editor's connection at once. The admin
password became an emergency way in next to staff accounts with Admin and
Support roles ([admin.md](../admin.md)).

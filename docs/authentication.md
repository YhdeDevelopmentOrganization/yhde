# Authentication

How people prove who they are, on the website and in Godot. What they may do
after that is in [authority.md](authority.md) and [teams.md](teams.md). The
decision is [ADR 0015](adr/0015-accounts-and-sign-in.md). Code:
`server/src/YHDE.Server/Accounts/`.

## 1. Accounts

Accounts live on the website. A person signs up with:

- email and password, or
- GitHub or Google (OAuth, on the website only).

One account can have a password and linked GitHub and Google logins at the
same time; they are kept in `oauth_links` and can be unlinked on the account
page, as long as one way to sign in is left.

With email and password, the address has to be confirmed through a link (valid
for 2 days) before invitations show up. A forgotten password is reset through
a link valid for 1 hour. Passwords are hashed with Argon2id
([security.md](security.md) §4). New accounts start empty: no
projects.

## 2. Website Sessions

Signing in on the website gives an HttpOnly cookie with a random token. The
server stores only its SHA-256 hash in `user_sessions` (kind `web`). A session
lasts 30 days.

The account page lists every session, website and editor, with the device and
when it was last used, and each can be signed out on its own.

## 3. OAuth

`/auth/{provider}` sends the person to GitHub or Google with a random `state`
(valid for 10 minutes) and comes back to `/auth/{provider}/callback`. Only a
verified email from the provider is used. The provider's secrets stay on the
server; Godot never sees them. After signing in, the site only returns to
paths on itself ([security.md](security.md) §8).

## 4. Signing In From Godot

The editor signs in through the website, so no password goes through Godot
([DeviceEndpoints.cs](../server/src/YHDE.Server/Accounts/DeviceEndpoints.cs)):

```
1. Godot:    POST /api/device/start          -> a secret device code for
                                                 Godot and a short user code
                                                 (XXXX-XXXX) to show
2. Godot:    opens the website at /app#/device?code=XXXX-XXXX
3. Browser:  the signed-in person checks the code and presses Allow
             (POST /api/device/approve)
4. Godot:    POST /api/device/poll every few seconds
                                             -> once allowed: an editor token,
                                                handed out only once
```

- Codes are valid for 10 minutes. Starting, polling, approving and looking up
  codes are rate limited.
- The editor token is a random token stored as a hash (`user_sessions`, kind
  `editor`) and lasts 90 days.
- Godot keeps it in the editor's own settings folder, never in the project.
- It is sent as `Authorization: Bearer <token>` on the WebSocket, on file
  requests and on `/api/editor/*` and the dashboard API.
- `GET /api/editor/projects` gives the person and the projects they can
  open (their own and the ones they were invited to), with their access to each.
- `POST /api/editor/sign-out` ends it. So does signing it out on the account
  page, which also closes that editor's connection with code 4401.

The editor token can do what the dashboard can, except exporting or deleting
the account, which only the website session may do.

## 5. Who the Server Thinks You Are

- The gateway sets `actor_id` and the display name from the sign-in. Names
  and ids in `Hello` or in payloads are ignored for signed-in connections.
- Signing in proves who someone is. What they may do is decided separately,
  on every operation ([authority.md](authority.md)).

## 6. Rules

1. Identity only comes from a secret the server checked, never from a
   payload.
2. The server stores only hashes of passwords and tokens.
3. OAuth secrets and flows stay on the server and the website.
4. Every session can be ended, and ending it takes effect at once.

## See Also

- [teams.md](teams.md), [authority.md](authority.md), [security.md](security.md),
  [network_protocol.md](network_protocol.md) §2, [ADR 0015](adr/0015-accounts-and-sign-in.md)

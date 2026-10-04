# Security

## Reporting a weakness

Email support@frostinteractive.fi with "Security" in the subject: what you
found, which version (add-on, server or both) and how to repeat it. Please
don't open a public issue for it. We answer within a few working days, tell
you when it's fixed, and credit you in the release notes if you like.

This address is for weaknesses in the YHDE software. For a problem with a
particular self-hosted server (your account or data on it), contact the people
who run that server: their details are on its contact page.

## Supported versions

Fixes go into the newest release. A server and its add-on should run the same
minor version (the add-on says so when they differ).

## What YHDE protects against

The threat model, including servers that are not YHDE's own, is in
[docs/security.md](docs/security.md). The main protections:

- The server checks every action; a modified editor gets no extra rights.
- Files from others that can run code in the editor (plugins, native
  libraries, `@tool` scripts) wait until each person accepts them.
- Add-on updates are installed only when signed with YHDE's release key,
  whichever server hands them out.

## For people who run a server

- Keep the server updated (`./yhde update`); the admin page shows when a new
  version is out.
- Leave `OFFICIAL_SITE` off: it is only for YHDE's own servers. Set your own
  name, contact and legal pages with the `OPERATOR_*` settings
  ([docs/setup_your_server.md](docs/setup_your_server.md)).
- The add-on's release signing key never goes on a server.

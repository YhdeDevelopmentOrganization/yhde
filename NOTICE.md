# Licensing

YHDE: real-time collaboration for the Godot editor.
Copyright (C) 2026 YHDE Development Organization.

| Part | Folder | License |
|---|---|---|
| Server, web pages, server kit, native core, tests, tools | everything not listed below | GNU AGPL v3 ([LICENSE](LICENSE)), with the additional terms below |
| Add-on scripts and configuration (GDScript, `plugin.cfg`, icons) | `client/godot/addons/yhde/` except `bin/` | MIT ([LICENSE.txt](client/godot/addons/yhde/LICENSE.txt)) |
| Add-on native libraries (built from `client/gdextension/`) | `client/godot/addons/yhde/bin/` | GNU AGPL v3 with the additional terms below |
| Third-party code | as listed | their own licenses ([THIRD_PARTY_NOTICES.txt](client/godot/addons/yhde/THIRD_PARTY_NOTICES.txt), and each package's own manifest for the server and web pages) |

The add-on's scripts are MIT so a game project can carry them, keeping only
the copyright notice. The add-on's native core and the server are AGPL: anyone
may run, change and share them, and anyone who runs a changed server for
others must offer its source to those users (AGPL section 13).

The native core is editor-only: it is never part of an exported game, so the
AGPL does not reach a game made with YHDE.

## Additional terms (GNU AGPL v3, section 7)

These terms apply to every part licensed under the GNU AGPL v3 above.

1. **Attribution (section 7(b)).** The web pages of a server built from this
   software, or from a modified version of it, must keep a visible
   "Powered by YHDE" credit that links to https://yhde.frostinteractive.fi (or
   to YHDE's main site, wherever it moves), on the pages where the server's
   own name and contact details are shown (as `server/admin-ui` does in its
   footer and home page). This credit is part of the Appropriate Legal Notices.
   The copyright notices in the source files must be kept as well.

2. **Marking modified versions (section 7(c)).** A modified version must not
   claim to be the original or YHDE's official service, and a server run from
   a modified version must not be presented as one run by YHDE Development
   Organization. Changing the server's own name, contact details and legal
   pages (the `OPERATOR_*` settings) is expected and is not misrepresentation.

3. **Trademarks (section 7(e)).** This license grants no right to use the name
   "YHDE", the YHDE logo or other YHDE marks, except to say truthfully that a
   work is built from or powered by YHDE. A fork that is distributed or run as
   a service under its own branding must use a different name and logo.

## Source

The complete source of every released add-on and server is in this
repository, at the tag of the release's version.

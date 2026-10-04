# Setting Up a YHDE Server, Step by Step

No server experience needed. Plan on about 30 minutes. You will copy and
paste a few commands. Text in `<angle brackets>` is something you replace
with your own value.

## What you need

- A Hetzner account (https://console.hetzner.cloud).
- A domain name you control (for example `mygame.com`, bought at Namecheap,
  Cloudflare, Hetzner or similar). The server gets its own address like
  `yhde.mygame.com`.
- Your computer's terminal:
  - **Windows:** open "Windows PowerShell" or "Terminal" from the Start menu.
  - **Mac:** open "Terminal" (Cmd+Space, type Terminal).

---

## Step 1: Make an SSH key (your password for the server)

In the terminal on **your computer**, run:

```
ssh-keygen -t ed25519
```

Press **Enter** three times (accept the location and leave the passphrase
empty, or set one if you like). Then show the key:

```
cat ~/.ssh/id_ed25519.pub
```

On Windows PowerShell use: `type $env:USERPROFILE\.ssh\id_ed25519.pub`

Copy the whole line it prints (it starts with `ssh-ed25519`).

## Step 2: Create the server on Hetzner

1. Go to https://console.hetzner.cloud -> create a **Project** (any name) -> open it.
2. Click **Add Server**:
   - **Location:** the one closest to your team.
   - **Image:** **Ubuntu 24.04**.
   - **Type:** Shared vCPU, **CX23** (2 CPUs, 4 GB RAM) is plenty to start.
   - **SSH keys:** click *Add SSH key*, paste the line from Step 1.
   - **Firewalls:** click *Create Firewall* and add **inbound** rules for
     TCP ports **22**, **80** and **443** (and UDP 443). Nothing else.
   - Click **Create & Buy now**.
3. When it's ready, copy the server's **IPv4 address** (like `157.90.12.34`).

## Step 3: Point your domain at the server

Where you bought the domain, open its **DNS settings** and add a record:

| Type | Name | Value |
|---|---|---|
| A | `yhde` | `<your server IPv4>` |

This makes `yhde.<yourdomain>` point at your server. It can take a few
minutes (rarely an hour) to take effect.

## Step 4: Log in to the server

On your computer:

```
ssh root@<your server IPv4>
```

Type `yes` if it asks about the fingerprint. You're now "inside" the server
(the prompt changes to something like `root@ubuntu`).

## Step 5: Download YHDE onto the server

```
apt-get update && apt-get install -y git
git clone https://github.com/YhdeDevelopmentOrganization/yhde.git /opt/yhde
```

## Step 6: Install and start everything (one command)

```
/opt/yhde/deploy/yhde install yhde.<yourdomain>
```

It installs what it needs, creates passwords, gets an HTTPS certificate and
starts the server. The first time takes a few minutes. At the end it prints
the website, the admin page with its password, and the server key.

**Keep the admin password and the server key to yourself.** Forgot them? Log
in again and run `/opt/yhde/deploy/yhde key`.

If it says the domain doesn't point here yet, wait 10 minutes and run the same
command again. It's safe to repeat.

## Step 7: Make yourself an admin

1. Open `https://yhde.<yourdomain>`, sign up and confirm your email.
2. Open `https://yhde.<yourdomain>/admin` and sign in with the admin password
   from Step 6.
3. On **Accounts**, find yourself and make yourself **Admin**. From now on
   sign in to the admin page with your own account; the password is only for
   emergencies.

To send emails (confirmations, invitations) and to allow sign-in with GitHub
or Google, add their settings with `./yhde set` (see
[deployment.md](deployment.md) §4). Until email is set up, the emails are only
written to `/opt/yhde/deploy/yhde logs server`.

### Say who runs the server

Your server's pages show who runs it, and its privacy, terms, cookies and
contact pages are yours, not YHDE's. Set your name, a contact address and
(if you have them) links to your own privacy policy and terms:

```
./yhde set OPERATOR_NAME "Pixel Studio"
./yhde set OPERATOR_EMAIL admin@pixelstudio.example
./yhde set PRIVACY_URL https://pixelstudio.example/privacy
./yhde set TERMS_URL https://pixelstudio.example/terms
```

A self-hosted server has no plans or prices: everyone can make projects
without limits beyond your server's disk (`./yhde set TEAMS_NEED_CODE true` makes
project owners need an access code from your admin page). Its pages carry a
"Powered by YHDE" credit, and search engines are asked not to list them.

`OFFICIAL_SITE` is only `true` on YHDE's own servers: it turns on YHDE's
website, plans and legal pages.

## Step 8: Put the YHDE add-on on the server (once per version)

1. Get the add-on zip, `yhde-addon-<version>.zip`, from YHDE's releases.
   Editors only install add-on updates signed with YHDE's release key, so a
   zip you build yourself works as a first install but not as an update
   (security.md §11).
2. On the admin page, open **Add-on**, click **Upload zip**, choose it, then
   click **Release**.

## Step 9: Let people in

During the beta, making a team needs an access code:

1. On the admin page, open **Promo codes** and click **New access code**. Set
   how many teams it may make, then send the code to the person.
2. They sign up on the website, make their team with the code, pick a plan
   and create or import a project.
3. They install the add-on in Godot (**AssetLib**, **Import...**, the zip),
   enable it and sign in from the YHDE panel.
4. They invite teammates by email from the dashboard or the panel. Invited
   people need no code.

Everyone in the same project edits together; different teams stay separate.
When you release a newer add-on, everyone's YHDE panel offers the update.

## Everyday commands (on the server, after `ssh root@<ip>`)

| Command | What it does |
|---|---|
| `/opt/yhde/deploy/yhde status` | Is it running? When was the last backup? |
| `/opt/yhde/deploy/yhde update` | Install the newest version by hand (usually you use **Server** on the admin page instead). |
| `/opt/yhde/deploy/yhde key` | Show the addresses, the admin password and the server key again. |
| `/opt/yhde/deploy/yhde admin-password` | Make a new admin password. |
| `/opt/yhde/deploy/yhde logs` | Show what the server is doing (Ctrl+C to stop). |
| `/opt/yhde/deploy/yhde backup` | Make a backup right now. |

Backups are made daily into `/opt/yhde/deploy/backups`. Also turn on
**Backups** for the server in the Hetzner console (a few euros a month) so a
copy exists outside the server.

## If something goes wrong

- **Godot can't connect:** run `yhde status` on the server, and check that the
  firewall allows ports 80 and 443.
- **Godot says the sign-in expired:** sign in again from the YHDE panel.
- **Install stops at "Waiting for https://...":** the domain doesn't point at
  the server yet (Step 3). Wait and run the install again.

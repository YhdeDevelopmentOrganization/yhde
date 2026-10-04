# Capacity

How many editors one server holds, what limits it, and how to measure it.
Measured on 2026-09-25 with `server/tools/YHDE.LoadTest` (section 5): first on
the production server, then in a lab on 2 pinned cores with the server,
PostgreSQL and Caddy on their own cores and the simulated editors on others.

## 1. The Production Server

Hetzner CX23 (2 shared vCPU, 4 GB RAM, 40 GB local disk) in Helsinki, driven
over the internet from a PC in Finland with `YHDE.LoadTest --ramp`: teams of 5,
typical use (cursor 5 times a second, an edit every 5 seconds).

| Editors online | Cursor p95 | Edit saved p95 | Disconnects | Feels |
|---:|---:|---:|---:|---|
| 2 (network only) | 5 ms | 13 ms | 0 | |
| 100 | 6 ms | 12 ms | 0 | smooth |
| 300 | 10 ms | 16 ms | 0 | smooth |
| 500 | 13 ms | 24 ms | 0 | smooth |
| 650 | 16 ms | 29 ms | 0 | smooth |
| 800 | 22 ms | 40 ms | 0 | smooth |
| 1 000 | 60 ms | 118 ms | 0 | noticeable |
| 1 250 | 159 ms | 377 ms | 0 | too slow |

"p95" means 95 % of messages arrived faster. The PC stayed under 10 % CPU, so
the server was the limit. Its cores are about half as fast as the lab
machine's (a Python loop: 2.65 s against 1.38 s; AES-GCM 3.7 against 15 GB/s;
disk syncs 2 550 against 3 700 a second), yet the results match the lab: the
server spends its time on many small messages, not on raw computation.

Planning number: about 700 editors online at once per CX23, which leaves room
for bursts and backups.

## 2. Lab Results

Each simulated editor does what the add-on does: presence updates (cursor,
selection, view) and stored edits, while a teammate measures how long each
takes to arrive.

Typical use (cursor 5 times a second, an edit every 5 seconds), teams of 5,
each team in its own project:

| Editors online | Server CPU (of 2 cores) | Cursor p95 | Edit saved p95 | Disconnects |
|---:|---:|---:|---:|---:|
| 500 | 44 % | 3 ms | 8 ms | 0 |
| 1 000 | 74 % | 13 ms | 26 ms | 0 |
| 1 500 | 92 % | 43 ms | 80 ms | 0 |

Heavy use (cursor 15 times a second and an edit every second, all the time),
teams of 5:

| Editors online | Server CPU | Cursor p95 | Edit saved p95 | Disconnects |
|---:|---:|---:|---:|---:|
| 250 | 57 % | 4 ms | 10 ms | 0 |
| 500 | 90 % | 20 ms | 42 ms | 0 |
| 600 | 91 % | 50 ms | 94 ms | 0 |
| 750 | 97 % | 557 ms | 968 ms | 0 (slow, still working) |

One big team in one project. Every cursor goes to everyone, so the cost grows
with the square of the team size:

| Team size | Use | Server CPU | Cursor p95 | Edit saved p95 |
|---:|---|---:|---:|---:|
| 100 | typical | 46 % | 3 ms | 9 ms |
| 200 | typical | 81 % | 10 ms | 22 ms |
| 130 | heavy | 85 % | 14 ms | 44 ms |
| 200 | heavy | 85 % | 61 ms | 80 ms |

Anything under about 100 ms feels instant.

Memory: with the server, PostgreSQL and Caddy capped at 2 GB together (a Linux
memory cgroup), 1 000 typical editors ran with cursor p95 17 ms and edit p95
34 ms, and peak use was 517 MB. RAM is not a limit on a 4 GB server.

Warm-up: the first minute after a restart is slower if a thousand editors
connect in the same second (code warm-up, new TLS sessions). Real arrivals are
spread out and don't notice it.

## 3. What Limits It

- CPU first: TLS, framing and validation of many small messages. More cores
  help about linearly; on Hetzner it is a rescale in the console.
- Then disk space (section 4).
- Database writes are not the limit any more. Each branch has one writer that
  commits whatever is waiting in one transaction (`BranchCommitter`), so one
  disk sync covers many edits. Even a disk several times slower than the lab's
  would not be the limit.
- Bandwidth is small because only changes are sent: at 1 000 typical editors
  the server sends about 50 Mbit/s.

What keeps it cheap: only changes travel, a drag is one operation, presence
may drop updates and is limited to 30 a second, a cursor update is encoded
once for all its recipients, and files are stored once per hash.

To check a server against the lab machine (higher is better):

| Check | Command | Lab machine |
|---|---|---|
| Disk syncs | `cd /opt/yhde/deploy && docker compose exec db pg_test_fsync -s 2 -f /var/lib/postgresql/data/fsync.tmp` | fdatasync about 3 700 ops/s |
| Encryption | `openssl speed -seconds 3 -evp aes-128-gcm` | 16384 bytes about 14 978 000k |

## 4. Disk Is the Real Limit

Every version of every file is kept (history and undo need it), and the daily
backup keeps another copy on the same disk. On the 40 GB disk, after the
system and containers, there is room for about 15 GB of file history. A small
game uses hundreds of MB; one with many textures, sounds or 3D models can use
tens of GB.

So CPU per editor is cheap and storage per project is the cost that grows.
That is why plans have storage limits ([teams.md](teams.md)). When the disk
fills up, attach a Hetzner Volume (or object storage) for blobs and backups.

## 5. Planning

Keep about 30 % spare for spikes and backups:

- about 1 000 editors online at once with normal use,
- about 500 if everyone works flat out at the same time (unlikely),
- one team of up to about 150 to 200 in a single project.

Online at once is not the number of customers: most people aren't in the
editor all day, and time zones spread the peaks. Watch the peak number of
people online on the admin page for a few weeks to know the ratio.

## 6. Running the Load Test

Build `server/tools/YHDE.LoadTest` with the .NET 10 SDK, or publish one file:
`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true`.

1. Give the test its own branches, so its edits never touch a real project
   (they stay in the log, a few MB per run):
   ```
   ssh root@<server> "/opt/yhde/deploy/yhde test-branches 400" > branches.txt
   ```
2. Ramp up until teammates would notice, and print a table:
   ```
   YHDE.LoadTest --url wss://<server>/ws --key <access-key> --branches-file branches.txt --ramp
   ```
   It measures the network alone first (2 editors), then 20, 50, 100 and more
   editors in teams of 5, and marks each step smooth, noticeable or too slow
   against that baseline. For a single run: `--teams N --team-size N
   --presence-hz F --ops-per-sec F --duration S --json FILE`.

- The server is busy for the few minutes the ramp takes. Editing still works,
  more slowly.
- Run it from a machine with a good connection. If the "this PC cpu" column
  gets high, or the upload is slow (about 20 Mbit/s up for 1 000 editors), the
  limit is the test machine, not the server.
- To watch the server meanwhile: `ssh root@<server> "docker stats --no-stream"`.

## 7. What Changed to Get Here

The first run fell over at 500 editors (heavy use) and at 160 in one team:
every edit had its own transaction and database connection, and bursts used up
PostgreSQL's connections, which disconnected editors. Now:

- One writer per branch commits everything waiting in one transaction
  (`Operations/BranchCommitter.cs`), then broadcasts in log order.
- The database pool stays below PostgreSQL's connection limit and waits
  instead of failing.
- A cursor update is encoded once for all recipients.
- Per-edit logging moved to Debug, and receive buffers are smaller.

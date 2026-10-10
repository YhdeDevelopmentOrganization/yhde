import { useEffect, useState } from "react"
import { longDate } from "@/lib/dates"
import { cn } from "@/lib/utils"
import { STAGE } from "@/world/version"
import { PageTop, Prose } from "../parts"
import { livePosts, type Post } from "../api"
import { PostImage, PostText } from "../PostText"

type Group = { tag: "new" | "improved" | "fixed"; items: string[] }
type Release = { version: string; date: string; title: string; groups: Group[] }

// Written from the project history; newest first. Release notes published on
// the admin page come before these and replace a version they share.
const HISTORY: Release[] = [
  {
    version: "0.6.5",
    date: "2026-10-10",
    title: "Stability: no more instance crashes or stalled projects",
    groups: [
      {
        tag: "fixed",
        items: [
          "The editor no longer crashes after you edit a scene that is instanced in another open scene.",
          "One slow or lost connection can no longer hold up everyone in a project, and silent connections are closed after a minute.",
          "A teammate who reconnects no longer leaves a second cursor and avatar behind.",
          "Changes made while someone joins, and undos, arrive in order and are never missed.",
          "Big batches of changes no longer freeze the editor, and files deleted while the editor was closed are only removed for everyone after you confirm.",
          "File names that cannot sync are reported in the YHDE panel, and names that differ only in capitals are refused.",
        ],
      },
      {
        tag: "improved",
        items: [
          "Code from teammates is checked by what each file is, not its name, before anything can run in your editor.",
          "Your sign-in never travels unencrypted to another computer, and redirects can no longer sign you out.",
          "Files are only shared within their own project, and uploads count toward storage as they happen.",
        ],
      },
    ],
  },
  {
    version: "0.6.0",
    date: "2026-10-04",
    title: "Open source, and TileSets that sync",
    groups: [
      {
        tag: "new",
        items: [
          "YHDE is open source: anyone can run their own server for their team. It shows its own name and legal pages and has no plans or limits beyond its disk.",
          "Files from teammates that can run code on your computer (plugins, native libraries, @tool scripts) wait in the YHDE panel until you accept them.",
          "Add-on updates are installed only when they are signed as a YHDE release, whichever server offers them.",
        ],
      },
      {
        tag: "fixed",
        items: [
          "TileSets sync properly: new sources, tiles, collision polygons, terrains and custom data reach your teammates, and so do removed and moved tiles, removed layers and patterns.",
          "Painting on a TileMapLayer no longer bounces back from teammates, and the old TileMap node keeps its layers in sync.",
          "Removed curve points reach your teammates.",
        ],
      },
    ],
  },
  {
    version: "0.5.0",
    date: "2026-09-28",
    title: "People belong to projects",
    groups: [
      {
        tag: "new",
        items: [
          "No more teams: you make up to three projects and invite three people into each. Anyone can be in as many projects as they like.",
          "Invitations to a project arrive by email with a link, and wait on your dashboard. They last 14 days and can be resent or withdrawn.",
          "Each project has a People panel: who can edit or only view, removing someone, handing the project over. Members can leave on their own.",
          "View links: up to five people per project watch it live in Godot, without an account, but can't change anything.",
          "See one person's changes in a project's activity.",
        ],
      },
      {
        tag: "improved",
        items: [
          "Warnings from 80 % of your storage, on the website and in Godot. When it's full, new files are refused with a clear reason.",
          "The owner gets an email when an invitation is accepted or declined, and when someone leaves.",
        ],
      },
    ],
  },
  {
    version: "0.4.3",
    date: "2026-09-27",
    title: "The YHDE logo in Godot",
    groups: [
      {
        tag: "new",
        items: [
          "The YHDE logo on the panel's tab and in Godot's top bar, in light and dark editor themes.",
          "Early access: making a team needs an access code from us for now. Invited teammates join without one.",
        ],
      },
    ],
  },
  {
    version: "0.4.2",
    date: "2026-09-27",
    title: "Roles and access per project",
    groups: [
      {
        tag: "new",
        items: [
          "Team admins: let someone manage projects and people while you keep the plan.",
          "Choose who can edit, only view, or not open each project.",
          "Pick which notifications pop up in Godot.",
          "Install YHDE in Godot straight from the downloaded file: AssetLib, Import.",
        ],
      },
    ],
  },
  {
    version: "0.4.1",
    date: "2026-09-27",
    title: "Your team, from Godot",
    groups: [
      { tag: "fixed", items: ["Turning YHDE on no longer moves your docks around or stretches the 3D view."] },
      {
        tag: "new",
        items: [
          "Make a new project from the folder you're in: its files go up to your team.",
          "Copy an invite link, invite people by email and see who's here, right in the YHDE panel.",
          "Have a code? Enter it on the Billing page.",
        ],
      },
    ],
  },
  {
    version: "0.4.0",
    date: "2026-09-27",
    title: "Sign in from Godot",
    groups: [
      {
        tag: "new",
        items: [
          "Sign in to YHDE right in Godot: press Sign in, allow it in your browser, and your team's projects show up in the YHDE panel.",
          "Click a project to connect. A new, empty project folder gets the game's files by itself.",
          "No server address or codes to type any more.",
          "Your name in Godot is your account's name, so everyone knows who is who.",
        ],
      },
      { tag: "improved", items: ["Reopening Godot reconnects on its own.", "Invite links still work: the project they download offers to join with its invite."] },
    ],
  },
  {
    version: "0.3.1",
    date: "2026-09-27",
    title: "Smooth editor, accounts and teams",
    groups: [
      {
        tag: "fixed",
        items: ["No more stutter in your own editor with YHDE on: moving, panning and dragging are as smooth as without it."],
      },
      {
        tag: "new",
        items: [
          "Accounts: sign up with email, GitHub or Google, make a team, and invite people by email.",
          "The dashboard shows your real projects, changes per day, storage and who is connected.",
          "Plans for every size: Solo, Trio, Team and Studio, with extra seats as you grow.",
          "Download your data or delete your account whenever you want.",
        ],
      },
      { tag: "improved", items: ["Invite links and codes can be deleted, not just turned off.", "Stronger protection against spam and people pretending to be YHDE."] },
    ],
  },
  {
    version: "0.3.0",
    date: "2026-09-26",
    title: "Invite links, starter projects and a new look",
    groups: [
      {
        tag: "new",
        items: [
          "Invite links: send one link and your teammate downloads your game as a ready Godot project, with YHDE and their invite inside.",
          "Start a project from a zip of your existing Godot game.",
          "The YHDE panel tells you when a new add-on version is out and updates itself.",
          "Shaders update live for everyone while you edit them.",
          "Bring everyone here: one click and your teammates follow your view.",
          "A new website, dashboard and server admin, in one look.",
        ],
      },
      { tag: "improved", items: ["Server updates can be approved from the admin page.", "Clearer statistics: who is online, changes per day and storage per project."] },
    ],
  },
  {
    version: "0.2.0",
    date: "2026-09-25",
    title: "Projects, chat and live scripts",
    groups: [
      {
        tag: "new",
        items: ["Projects with invite codes, so every team has its own space.", "Type in the same script together, live.", "Project chat, and comments pinned to the things you mean.", "Cursors and comments in the 2D view."],
      },
      { tag: "fixed", items: ["The admin page keeps working when the backups folder can't be read."] },
    ],
  },
  {
    version: "0.1.0",
    date: "2026-09-24",
    title: "The first live build",
    groups: [
      {
        tag: "new",
        items: [
          "Live scene editing: create, delete, move, rename and reorder nodes, and every Inspector property, in 2D, 3D and UI scenes.",
          "See each other's cursors and selections, and follow a teammate's view.",
          "Undo and redo per person, handled by the server.",
          "Reused (instanced) scenes update everywhere at once.",
          "Change Type and Make Scene Root work live.",
          "Keep working offline; changes are sent when you reconnect.",
        ],
      },
    ],
  },
]

const TEXT = { title: "What's", accent: "new", lead: "Every YHDE release, newest first. Your YHDE panel in Godot offers each update when it's out.", latest: "Latest", tags: { new: "New", improved: "Improved", fixed: "Fixed" } }

const TAG_LOOK = { new: "bg-tint-blue text-sky", improved: "bg-tint-green text-good", fixed: "bg-tint-rust text-[#ffb49c]" }

function Badge({ version, latest, label }: { version: string; latest: boolean; label: string }) {
  return (
    <>
      <span className={cn("rounded-full px-3 py-1 text-sm font-bold", latest ? "bg-sky text-sky-ink" : "bg-card-2 text-text")}>
        {STAGE} {version.replace(/^v/, "")}
      </span>
      {latest ? <span className="text-sm font-semibold text-sky">{label}</span> : null}
    </>
  )
}

export function Changelog() {
  const t = TEXT
  const [posts, setPosts] = useState<Post[]>([])
  useEffect(() => {
    livePosts("release").then(setPosts, () => {})
  }, [])
  const versions = new Set(posts.map((p) => p.version.replace(/^v/, "")))
  const history = HISTORY.filter((r) => !versions.has(r.version))

  return (
    <>
      <PageTop title={t.title} accent={t.accent} lead={t.lead} />
      <div className="mx-auto max-w-4xl px-5 pb-16 sm:px-7">
        <ol className="grid gap-5">
          {posts.map((p, i) => (
            <li key={p.id} className="rounded-[1.5rem] bg-card p-7 sm:p-8">
              <div className="flex flex-wrap items-center gap-3">
                <Badge version={p.version} latest={i === 0} label={t.latest} />
                <time className="text-sm text-dim sm:ml-auto" dateTime={p.publishAt ?? p.created}>
                  {longDate(p.publishAt ?? p.created)}
                </time>
              </div>
              <h2 className="mt-4 text-2xl font-bold text-text">{p.title}</h2>
              {p.summary ? <p className="mt-2 text-dim">{p.summary}</p> : null}
              {p.cover ? <PostImage src={p.cover} alt="" className="mt-5 [&_img]:aspect-[2/1] [&_img]:object-cover" /> : null}
              <Prose className="mt-2 text-[0.9375rem]">
                <PostText body={p.body} />
              </Prose>
            </li>
          ))}
          {history.map((r, i) => (
            <li key={r.version} className="rounded-[1.5rem] bg-card p-7 sm:p-8">
              <div className="flex flex-wrap items-center gap-3">
                <Badge version={r.version} latest={posts.length === 0 && i === 0} label={t.latest} />
                <time className="text-sm text-dim sm:ml-auto" dateTime={r.date}>
                  {longDate(r.date)}
                </time>
              </div>
              <h2 className="mt-4 text-2xl font-bold text-text">{r.title}</h2>
              <div className="mt-5 grid gap-5">
                {r.groups.map((g) => (
                  <div key={g.tag}>
                    <span className={cn("rounded-full px-2.5 py-0.5 text-xs font-bold", TAG_LOOK[g.tag])}>{t.tags[g.tag]}</span>
                    <ul className="mt-3 grid gap-2 pl-5 text-[0.9375rem] leading-relaxed text-text/85 [&_li]:list-disc [&_li]:marker:text-dim">
                      {g.items.map((item) => (
                        <li key={item}>{item}</li>
                      ))}
                    </ul>
                  </div>
                ))}
              </div>
            </li>
          ))}
        </ol>
      </div>
    </>
  )
}

import { useRef, useState, type ReactNode } from "react"
import { AlertTriangle, ChevronRight, Download, Plus, Upload, X } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Progress } from "@/components/ui/progress"
import { ago, bytes, num } from "@/admin/format"
import { Bars, EmptyState, LiveTag, Panel, PeerStack } from "@/world/parts"
import { FAILED, JOIN_KEY, PageHead, call, go, run } from "../App"
import { COMPANY } from "@/site/company"
import { OFFICIAL, SITE, operatorName } from "@/world/site"
import { api, planUsage, projectImage, useAppState, type MyPlan, type Project, type State } from "../store"

export function ProjectsPage() {
  const s = useAppState()
  const [creating, setCreating] = useState(false)
  const mine = s.projects.filter((p) => p.role === "owner")
  const shared = s.projects.filter((p) => p.role === "member")
  const live = mine.filter((p) => !p.archived)
  const archived = mine.filter((p) => p.archived)
  const usage = s.plan ? planUsage(s.plan) : null
  const canMake = !!s.plan && !usage?.projectsFull && !usage?.storageFull

  // Nothing yet: the two ways in, and nothing else.
  if (!s.projects.length)
    return (
      <>
        <PageHead title="Projects" />
        <div className="grid gap-5 lg:grid-cols-2">
          <Panel title="Start a project">
            <StartForm />
          </Panel>
          <Panel title="I was invited">
            <InvitedForm />
          </Panel>
        </div>
      </>
    )

  return (
    <>
      <PageHead
        title="Projects"
        sub={
          s.plan
            ? `${s.plan.projects}${s.plan.maxProjects !== null ? ` of ${s.plan.maxProjects}` : ""} yours${shared.length ? ` · ${shared.length} shared with you` : ""}`
            : `${shared.length} shared with you`
        }
        action={
          <>
            <Button variant="outline" asChild>
              <a href="/addon/yhde-addon.zip" download>
                <Download /> Godot add-on
              </a>
            </Button>
            {canMake && !creating ? (
              <Button onClick={() => setCreating(true)}>
                <Plus /> New project
              </Button>
            ) : null}
          </>
        }
      />
      {s.plan ? <Limits plan={s.plan} /> : null}
      {creating && canMake ? <NewProject onDone={() => setCreating(false)} /> : null}

      {mine.length ? (
        <section className="grid gap-3">
          {live.length ? live.map((p) => <ProjectRow key={p.id} p={p} s={s} />) : (
            <EmptyState title="No active projects">Restore an archived project below, or make a new one.</EmptyState>
          )}
        </section>
      ) : null}

      {shared.length ? (
        <section className={`${mine.length ? "mt-10 " : ""}grid gap-3`}>
          {mine.length ? <h2 className="label-dim">Shared with you</h2> : null}
          {shared.map((p) => (
            <ProjectRow key={p.id} p={p} s={s} />
          ))}
        </section>
      ) : null}

      {archived.length ? (
        <details className="group mt-10">
          <summary className="label-dim flex w-fit cursor-pointer list-none items-center gap-2 hover:text-sky [&::-webkit-details-marker]:hidden">
            <ChevronRight className="size-3.5 transition-transform group-open:rotate-90" /> Archived ({archived.length})
          </summary>
          <div className="mt-4 grid gap-3 opacity-80">
            {archived.map((p) => (
              <ProjectRow key={p.id} p={p} s={s} />
            ))}
          </div>
        </details>
      ) : null}

      <div className="mt-10 grid gap-3">
        {!s.plan ? (
          <More title="Start your own project">
            <StartForm />
          </More>
        ) : null}
        <More title="Have an invite link or code?" open={hasJoinCode()}>
          <InvitedForm />
        </More>
      </div>
    </>
  )
}

function More({ title, open, children }: { title: string; open?: boolean; children: ReactNode }) {
  return (
    <details className="group" open={open}>
      <summary className="label-dim flex w-fit cursor-pointer list-none items-center gap-2 hover:text-sky [&::-webkit-details-marker]:hidden">
        <ChevronRight className="size-3.5 transition-transform group-open:rotate-90" /> {title}
      </summary>
      <div className="mt-4 max-w-xl rounded-[1.25rem] bg-card p-5">{children}</div>
    </details>
  )
}

// Making a project from nothing. Someone who can't make projects yet types
// the access code here too (during the beta it comes from us).
function StartForm() {
  const s = useAppState()
  const [name, setName] = useState("")
  const [code, setCode] = useState("")
  const [busy, setBusy] = useState(false)
  const needsCode = !s.plan && s.teamsNeedCode !== false
  const usage = s.plan ? planUsage(s.plan) : null
  const full = !!usage && (usage.projectsFull || usage.storageFull)
  const ask = OFFICIAL ? COMPANY.email : SITE.operator?.email
  return (
    <form
      className="grid gap-4"
      onSubmit={async (e) => {
        e.preventDefault()
        setBusy(true)
        if (!s.plan && !(await run(() => api.startOwning(code)))) {
          setBusy(false)
          return
        }
        const id = await call(() => api.createProject(name))
        setBusy(false)
        if (id !== FAILED) go(`/projects/${id}`)
      }}
    >
      <p className="text-sm text-dim">Name your game. You get a folder to open in Godot, with YHDE already in it.</p>
      <div className="grid gap-2">
        <Label htmlFor="start-name" className="label-dim">
          Name
        </Label>
        <Input id="start-name" className="h-10" placeholder="Skyward" maxLength={80} value={name} onChange={(e) => setName(e.target.value)} disabled={busy} />
      </div>
      {needsCode ? (
        <div className="grid gap-2">
          <Label htmlFor="start-code" className="label-dim">
            Access code
          </Label>
          <Input
            id="start-code"
            className="h-10 font-mono uppercase"
            value={code}
            maxLength={40}
            autoComplete="off"
            spellCheck={false}
            onChange={(e) => setCode(e.target.value)}
            disabled={busy}
          />
          <p className="text-xs text-dim">
            {OFFICIAL ? "During the beta, starting a project needs a code from us." : `Starting a project on this server needs a code from ${operatorName()}.`}
            {ask ? (
              <>
                {" "}
                Ask for one at{" "}
                <a className="text-sky underline-offset-4 hover:underline" href={`mailto:${ask}`}>
                  {ask}
                </a>
                .
              </>
            ) : null}
          </p>
        </div>
      ) : null}
      {full ? <p className="text-xs text-text">You can't start another project now: archive and delete one first.</p> : null}
      <Button type="submit" className="w-fit" disabled={busy || full || !name.trim() || (needsCode && !code.trim())}>
        {busy ? "Starting…" : "Start a project"}
      </Button>
      <p className="text-xs text-dim">
        Have a game already?{" "}
        <a className="text-sky underline-offset-4 hover:underline" href="/docs#share-a-game">
          Share it from Godot
        </a>
        .
      </p>
    </form>
  )
}

function hasJoinCode() {
  try {
    return !!localStorage.getItem(JOIN_KEY)
  } catch {
    return false
  }
}

// An invite link (pasted) opens its page; a project's code (typed) joins it.
// A code from a link (/app#/join/<code>) arrives filled in.
function InvitedForm() {
  const [value, setValue] = useState(() => {
    try {
      const c = localStorage.getItem(JOIN_KEY) ?? ""
      localStorage.removeItem(JOIN_KEY)
      return c
    } catch {
      return ""
    }
  })
  const [busy, setBusy] = useState(false)
  return (
    <form
      className="grid gap-4"
      onSubmit={async (e) => {
        e.preventDefault()
        const v = value.trim()
        // A link's token (or the whole link): its page on this site.
        const token = /(?:^|\/join\/)([A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4})\/?$/i.exec(v)?.[1]
        if (token) {
          location.href = `/join/${token.toUpperCase()}`
          return
        }
        setBusy(true)
        const r = await call(() => api.joinWithCode(v), "You're in.")
        setBusy(false)
        if (r !== FAILED) {
          setValue("")
          go(`/projects/${r.id}`)
        }
      }}
    >
      <p className="text-sm text-dim">Paste the invite link, or type the code you were given.</p>
      <div className="flex gap-2">
        <Input
          aria-label="Invite link or code"
          className="h-10"
          placeholder="Link or code"
          value={value}
          autoComplete="off"
          spellCheck={false}
          onChange={(e) => setValue(e.target.value)}
          disabled={busy}
        />
        <Button type="submit" className="h-10" disabled={busy || !value.trim()}>
          {busy ? "Joining…" : "Continue"}
        </Button>
      </div>
    </form>
  )
}

// Warnings before a limit stops something: storage from 80 %, and the
// last project slot.
function Limits({ plan }: { plan: MyPlan }) {
  const u = planUsage(plan)
  const notes: string[] = []
  if (u.storageFull) notes.push(`Your storage is full. New files from Godot are refused until you delete files or a project.`)
  else if (u.storageWarning)
    notes.push(`${Math.round(u.storageShare * 100)} % of your storage is used. When it's full, new files from Godot are refused.`)
  if (u.projectsFull) notes.push(`You have ${plan.projects} of ${plan.maxProjects} projects. Archive and delete one to make another.`)
  if (!notes.length) return null
  return (
    <div role="status" className={`mb-6 flex gap-3 rounded-2xl px-5 py-4 text-sm text-text ${u.storageFull ? "bg-tint-rust" : "bg-tint-blue"}`}>
      <AlertTriangle className={`mt-0.5 size-4 shrink-0 ${u.storageFull ? "text-bad" : "text-sky"}`} aria-hidden="true" />
      <div className="grid gap-1">
        {notes.map((n) => (
          <p key={n}>{n}</p>
        ))}
      </div>
    </div>
  )
}

function ProjectRow({ p, s }: { p: Project; s: State }) {
  const here = s.presence.filter((x) => x.projectId === p.id)
  const names = here.map((x) => x.name)
  const last = p.activity[0]
  return (
    <a
      href={`#/projects/${p.id}`}
      className="group grid gap-x-8 gap-y-4 rounded-[1.25rem] bg-card px-6 py-5 transition-colors hover:bg-card-2 lg:grid-cols-[minmax(0,1.2fr)_minmax(0,1fr)_auto] lg:items-center"
    >
      <div className="flex min-w-0 items-center gap-4">
        <img src={projectImage(p)} alt="" className="aspect-video w-24 shrink-0 rounded-lg bg-card-2 object-cover" loading="lazy" />
        <div className="min-w-0">
          <div className="flex items-center gap-3">
            <h2 className="truncate text-base font-semibold text-text group-hover:text-sky">{p.name}</h2>
            {p.archived ? <span className="label-dim">Archived</span> : here.length ? <LiveTag>{here.length} in the scene</LiveTag> : null}
          </div>
          <p className="mt-1.5 truncate text-xs text-dim">
            {p.role === "member" && p.owner ? <>{p.owner.name}'s · {p.myAccess === "view" ? "you can view" : "you can edit"} · </> : null}
            {last ? (
              <>
                <span className="text-text">{last.who}</span> · {last.text} · {ago(last.at)}
              </>
            ) : (
              "Nothing yet"
            )}
          </p>
        </div>
      </div>
      <div className="grid min-w-0 gap-1.5 overflow-hidden">
        <Bars values={p.daily} height={28} label={`Changes per day in ${p.name}, last 30 days`} />
        <span className="label-dim">30 days · {num(p.daily.reduce((a, b) => a + b, 0))} changes</span>
      </div>
      <div className="flex items-center gap-6">
        {names.length ? <PeerStack names={names} /> : null}
        <span className="text-right text-xs text-dim tabular-nums">
          {num(p.files)} files
          <br />
          {bytes(p.bytes)}
        </span>
        <ChevronRight className="size-4 text-dim transition-transform group-hover:translate-x-0.5 group-hover:text-sky" />
      </div>
    </a>
  )
}

function NewProject({ onDone }: { onDone: () => void }) {
  const [name, setName] = useState("")
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  return (
    <Panel
      className="mb-8 border-sky/50"
      title="New project"
      action={
        <Button variant="ghost" size="icon-sm" aria-label="Close" onClick={onDone}>
          <X />
        </Button>
      }
    >
      <form
        className="grid gap-5 md:grid-cols-[1fr_1fr_auto] md:items-end"
        onSubmit={async (e) => {
          e.preventDefault()
          setBusy(true)
          const id = await call(() => api.createProject(name, file ?? undefined), "Project created")
          setBusy(false)
          if (id !== FAILED) {
            onDone()
            go(`/projects/${id}`)
          }
        }}
      >
        <div className="grid gap-2">
          <Label htmlFor="np-name" className="label-dim">
            Name
          </Label>
          <Input id="np-name" className="h-10" placeholder={file ? "From the zip (optional)" : "Skyward"} maxLength={80} value={name} onChange={(e) => setName(e.target.value)} disabled={busy} autoFocus />
        </div>
        <div className="grid gap-2">
          <span className="label-dim">Start from your game (optional)</span>
          <input ref={input} type="file" accept=".zip,application/zip" hidden onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
          <Button type="button" variant="outline" className="h-10 justify-start" onClick={() => input.current?.click()} disabled={busy}>
            <Upload /> <span className="truncate normal-case tracking-normal">{file ? `${file.name} · ${bytes(file.size)}` : "Choose a zip of the Godot project"}</span>
          </Button>
        </div>
        <Button type="submit" className="h-10" disabled={busy || (!name.trim() && !file)}>
          {busy ? (file ? "Adding files…" : "Creating…") : "Create project"}
        </Button>
        {busy && file ? <Progress value={70} className="md:col-span-3" /> : null}
      </form>
      <p className="mt-4 text-[0.6875rem] text-dim">Your files go to the server once; after that, everyone in the project edits the same live copy.</p>
    </Panel>
  )
}

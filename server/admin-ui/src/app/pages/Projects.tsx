import { useRef, useState } from "react"
import { AlertTriangle, ChevronRight, Download, Plus, Upload, X } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Progress } from "@/components/ui/progress"
import { ago, bytes, num } from "@/admin/format"
import { Bars, EmptyState, LiveTag, Panel, PeerStack } from "@/world/parts"
import { FAILED, JOIN_KEY, PageHead, call, go } from "../App"
import { StartOwning } from "../StartOwning"
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
  // The form shows when asked for, and always while there are none yet;
  // the button only while it's hidden.
  const formOpen = canMake && (creating || !mine.length)

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
            {canMake && !formOpen ? (
              <Button onClick={() => setCreating(true)}>
                <Plus /> New project
              </Button>
            ) : null}
          </>
        }
      />
      <JoinForm />
      {s.plan ? <Limits plan={s.plan} /> : null}
      {formOpen ? <NewProject onDone={() => setCreating(false)} canClose={mine.length > 0} /> : null}

      {s.plan ? (
        <section className="grid gap-3">
          {live.length ? live.map((p) => <ProjectRow key={p.id} p={p} s={s} />) : mine.length ? (
            <EmptyState title="No active projects">Restore an archived project below, or make a new one.</EmptyState>
          ) : null}
        </section>
      ) : null}

      {shared.length ? (
        <section className="mt-10 grid gap-3">
          <h2 className="label-dim">Shared with you</h2>
          {shared.map((p) => (
            <ProjectRow key={p.id} p={p} s={s} />
          ))}
        </section>
      ) : null}

      {!s.plan ? (
        <div className="mt-10 grid gap-5">
          {!shared.length ? (
            <EmptyState title="No projects yet">
              When someone invites you into their project, it shows up here. You can also make your own below.
            </EmptyState>
          ) : null}
          {!s.user.emailVerified ? (
            <p className="rounded-2xl bg-card px-5 py-4 text-sm text-dim">
              Invited to a project? Confirm your email address first (we sent you a link), or open the link in the invitation email.
            </p>
          ) : null}
          <StartOwning />
        </div>
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
    </>
  )
}

// Joining someone's project with its join code. A code from a link
// (/app#/join/<code>) arrives filled in.
function JoinForm() {
  const [code, setCode] = useState(() => {
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
      className="mb-6 flex flex-wrap items-center gap-x-3 gap-y-2 rounded-2xl bg-card px-5 py-4"
      onSubmit={async (e) => {
        e.preventDefault()
        setBusy(true)
        const r = await call(() => api.joinWithCode(code), "You're in. Open it in Godot from the YHDE panel.")
        setBusy(false)
        if (r !== FAILED) {
          setCode("")
          go(`/projects/${r.id}`)
        }
      }}
    >
      <Label htmlFor="join-code" className="text-sm font-semibold text-text">
        Join a project
      </Label>
      <Input
        id="join-code"
        className="h-9 w-40 font-mono uppercase"
        placeholder="K7QM-2XRD"
        value={code}
        maxLength={12}
        autoComplete="off"
        spellCheck={false}
        onChange={(e) => setCode(e.target.value)}
      />
      <Button type="submit" className="h-9" disabled={busy || !code.trim()}>
        {busy ? "Joining…" : "Join"}
      </Button>
      <span className="text-xs text-dim">Enter the join code the project's owner gave you.</span>
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

function NewProject({ onDone, canClose }: { onDone: () => void; canClose: boolean }) {
  const [name, setName] = useState("")
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  return (
    <Panel
      className="mb-8 border-sky/50"
      title="New project"
      action={
        canClose ? (
          <Button variant="ghost" size="icon-sm" aria-label="Close" onClick={onDone}>
            <X />
          </Button>
        ) : null
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

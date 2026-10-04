import { useCallback, useEffect, useState } from "react"
import { OFFICIAL } from "@/world/site"
import { Minus, Plus, Search } from "lucide-react"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { BETA, PLANS, planById } from "@/world/plans"
import { api, type Me } from "../api"
import { ago, bytes } from "../format"
import { Empty, Section } from "../parts"
import { act } from "./Users"

// Owners (teams.md): people who can make projects, their plan and the
// people in each of their projects. Staff give free seats (more people in
// every project) and add accounts straight into a project.
export type TeamRow = {
  id: string
  name: string
  ownerId: string
  owner: string
  ownerEmail: string
  plan: string
  period: "month" | "year"
  extraSeats: number
  extraStorage: number
  bonusSeats: number
  bonusStorageGb: number
  members: number // different people across their projects
  invites: number
  projects: number
  created: string
  freeSeats: number
}

type Detail = {
  team: { id: string; name: string; plan: string; period: "month" | "year"; extraSeats: number; extraStorage: number; bonusSeats: number; bonusStorageGb: number; freeSeats: number; created: string }
  owner: { name: string; email: string } | null
  peoplePerProject: number
  maxProjects: number | null
  viewersPerProject: number
  storageBytes: number
  usedBytes: number
  projects: {
    id: string
    name: string
    archived: boolean
    files: number
    bytes: number
    members: { userId: string; name: string; email: string; access: "edit" | "view"; joined: string; lastSeen: string | null }[]
    invites: { id: string; email: string; sent: string }[]
    viewers: number
  }[]
  promos: { text: string; redeemedAt: string; until: string | null }[]
}

export function TeamsPage({ me }: { me: Me }) {
  const [q, setQ] = useState("")
  const [rows, setRows] = useState<TeamRow[] | null>(null)
  const [open, setOpen] = useState<string | null>(null)

  const load = useCallback(async (query: string) => {
    try {
      setRows(await api<TeamRow[]>("GET", "/teams?q=" + encodeURIComponent(query)))
    } catch (e) {
      toast.error((e as Error).message)
    }
  }, [])
  useEffect(() => {
    const t = setTimeout(() => load(q), 250)
    return () => clearTimeout(t)
  }, [q, load])

  return (
    <div className="grid gap-6">
      <Section
        title="People who make projects"
        description={rows ? `${rows.length} with an access code. Nothing is charged during the beta.` : "Loading…"}
        action={
          <div className="relative w-64 max-w-full">
            <Search className="absolute top-1/2 left-3 size-4 -translate-y-1/2 text-dim" aria-hidden="true" />
            <Input className="h-9 pl-9" placeholder="Name or email" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search owners" />
          </div>
        }
      >
        {rows && rows.length === 0 ? (
          <Empty>Nobody yet.</Empty>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Owner</TableHead>
                <TableHead>Plan</TableHead>
                <TableHead className="text-right">Projects</TableHead>
                <TableHead className="text-right">People in them</TableHead>
                <TableHead>Since</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(rows ?? []).map((t) => (
                <TableRow key={t.id} className="cursor-pointer" onClick={() => setOpen(t.id)}>
                  <TableCell className="font-medium">
                    {t.owner}
                    <span className="block text-xs font-normal text-muted-foreground">{t.ownerEmail}</span>
                  </TableCell>
                  <TableCell>
                    {planById(t.plan)?.name ?? t.plan}
                    {t.bonusSeats || t.bonusStorageGb ? <Badge variant="secondary" className="ml-2">Code</Badge> : null}
                    {t.freeSeats ? <Badge variant="secondary" className="ml-2">+{t.freeSeats} free</Badge> : null}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">{t.projects}</TableCell>
                  <TableCell className="text-right tabular-nums">
                    {t.members}
                    {t.invites ? <span className="text-muted-foreground"> + {t.invites} invited</span> : null}
                  </TableCell>
                  <TableCell className="text-muted-foreground">{ago(t.created)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Section>
      <OwnerDialog id={open} admin={me.role === "admin"} onClose={() => setOpen(null)} onChanged={() => load(q)} />
    </div>
  )
}

function OwnerDialog({ id, admin, onClose, onChanged }: { id: string | null; admin: boolean; onClose: () => void; onChanged: () => void }) {
  const [d, setD] = useState<Detail | null>(null)
  const [form, setForm] = useState({ plan: "beta", period: "month", extraSeats: 0, extraStorage: 0, freeSeats: 0 })

  const load = useCallback(async () => {
    if (!id) return
    try {
      const x = await api<Detail>("GET", `/teams/${id}`)
      setD(x)
      setForm({ plan: x.team.plan, period: x.team.period, extraSeats: x.team.extraSeats, extraStorage: x.team.extraStorage, freeSeats: x.team.freeSeats ?? 0 })
    } catch (e) {
      toast.error((e as Error).message)
    }
  }, [id])
  useEffect(() => {
    setD(null)
    load()
  }, [load])
  const changed = () => (load(), onChanged())

  const plan = planById(form.plan)
  return (
    <Dialog open={!!id} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-h-[90svh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{d?.owner?.name ?? "Owner"}</DialogTitle>
          <DialogDescription>
            {d
              ? `${d.owner?.email ?? ""} · ${d.projects.length}${d.maxProjects !== null ? ` of ${d.maxProjects}` : ""} projects · ${bytes(d.usedBytes)} of ${bytes(d.storageBytes)} · since ${ago(d.team.created)}`
              : "Loading…"}
          </DialogDescription>
        </DialogHeader>
        {d ? (
          <div className="grid gap-6">
            {/* A self-hosted server has one plan without limits: nothing to change. */}
            <section className={OFFICIAL ? "grid gap-3" : "hidden"}>
              <h3 className="text-sm font-semibold">Plan</h3>
              <div className="grid gap-3 sm:grid-cols-2">
                <div className="grid gap-1.5">
                  <Label>Plan</Label>
                  <Select value={form.plan} disabled={!admin} onValueChange={(v) => setForm({ ...form, plan: v, extraSeats: Math.min(form.extraSeats, planById(v)?.maxExtraSeats ?? 0) })}>
                    <SelectTrigger className="w-full">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {[BETA, ...PLANS].map((p) => (
                        <SelectItem key={p.id} value={p.id}>
                          {p.name} · {p.seats - 1} per project · {p.storageGb} GB
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </div>
                <div className="grid gap-1.5">
                  <Label>Billing period</Label>
                  <Select value={form.period} disabled={!admin} onValueChange={(v) => setForm({ ...form, period: v })}>
                    <SelectTrigger className="w-full">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="month">Monthly</SelectItem>
                      <SelectItem value="year">Yearly</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
                {plan?.maxExtraSeats ? (
                  <div className="grid gap-1.5">
                    <Label htmlFor="t-seats">Extra seats, paid (max {plan.maxExtraSeats})</Label>
                    <Input id="t-seats" type="number" min={0} max={plan.maxExtraSeats} value={form.extraSeats} disabled={!admin} onChange={(e) => setForm({ ...form, extraSeats: Number(e.target.value) || 0 })} />
                  </div>
                ) : null}
                {form.plan !== "beta" ? (
                  <div className="grid gap-1.5">
                    <Label htmlFor="t-storage">Extra storage (×10 GB)</Label>
                    <Input id="t-storage" type="number" min={0} max={20} value={form.extraStorage} disabled={!admin} onChange={(e) => setForm({ ...form, extraStorage: Number(e.target.value) || 0 })} />
                  </div>
                ) : null}
              </div>
              <div className="flex flex-wrap items-center gap-3 rounded-xl border border-dashed p-3">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium">Free seats</p>
                  <p className="text-xs text-muted-foreground">
                    One more person in each of their projects per seat, never charged. Now {d.peoplePerProject} people per project besides the owner.
                  </p>
                </div>
                <div className="flex items-center gap-1">
                  <Button variant="outline" size="icon-sm" aria-label="One free seat fewer" disabled={!admin || form.freeSeats <= 0} onClick={() => setForm({ ...form, freeSeats: form.freeSeats - 1 })}>
                    <Minus />
                  </Button>
                  <span className="w-8 text-center text-sm tabular-nums" aria-live="polite">
                    {form.freeSeats}
                  </span>
                  <Button variant="outline" size="icon-sm" aria-label="One free seat more" disabled={!admin || form.freeSeats >= 100} onClick={() => setForm({ ...form, freeSeats: form.freeSeats + 1 })}>
                    <Plus />
                  </Button>
                </div>
              </div>
              {d.team.bonusSeats || d.team.bonusStorageGb ? (
                <p className="text-xs text-muted-foreground">
                  From codes: +{d.team.bonusSeats} seats, +{d.team.bonusStorageGb} GB.
                </p>
              ) : null}
              {admin ? (
                <Button className="w-fit" onClick={() => act(() => api("POST", `/teams/${d.team.id}`, form), "Saved").then((ok) => ok && changed())}>
                  Save
                </Button>
              ) : null}
            </section>

            <section className="grid gap-4">
              <h3 className="text-sm font-semibold">Projects</h3>
              {d.projects.length ? (
                d.projects.map((p) => <ProjectPeople key={p.id} p={p} limit={d.peoplePerProject} viewersLimit={d.viewersPerProject} admin={admin} onChanged={changed} />)
              ) : (
                <p className="text-sm text-muted-foreground">No projects yet.</p>
              )}
            </section>

            {d.promos.length ? (
              <section className="grid gap-2">
                <h3 className="text-sm font-semibold">Codes used</h3>
                {d.promos.map((p, i) => (
                  <p key={i} className="text-sm">
                    {p.text} <span className="text-muted-foreground">· {ago(p.redeemedAt)}{p.until ? ` · until ${new Date(p.until).toLocaleDateString()}` : ""}</span>
                  </p>
                ))}
              </section>
            ) : null}
          </div>
        ) : null}
      </DialogContent>
    </Dialog>
  )
}

function ProjectPeople({ p, limit, viewersLimit, admin, onChanged }: { p: Detail["projects"][number]; limit: number; viewersLimit: number; admin: boolean; onChanged: () => void }) {
  const [email, setEmail] = useState("")
  return (
    <div className="grid gap-2 rounded-xl bg-card-2/50 p-3">
      <div className="flex items-baseline gap-3 text-sm">
        <span className="min-w-0 flex-1 truncate font-medium">
          {p.name} {p.archived ? <Badge variant="outline">Archived</Badge> : null}
        </span>
        <span className="text-xs text-muted-foreground tabular-nums">
          {p.members.length + p.invites.length} / {limit} people · {p.viewers} / {viewersLimit} viewers · {p.files} files · {bytes(p.bytes)}
        </span>
      </div>
      {p.members.map((m) => (
        <div key={m.userId} className="flex items-center gap-3 text-sm">
          <span className="min-w-0 flex-1 truncate">
            {m.name} <span className="text-muted-foreground">· {m.email}{m.access === "view" ? " · view only" : ""}</span>
          </span>
          <span className="text-xs text-muted-foreground">joined {ago(m.joined)}</span>
          {admin ? (
            <Button variant="ghost" size="sm" onClick={() => act(() => api("POST", `/projects/${p.id}/members/${m.userId}/remove`), `${m.name} removed`).then((ok) => ok && onChanged())}>
              Remove
            </Button>
          ) : null}
        </div>
      ))}
      {p.invites.map((i) => (
        <div key={i.id} className="text-sm text-muted-foreground">
          Invited: {i.email} · {ago(i.sent)}
        </div>
      ))}
      {admin ? (
        <form
          className="flex gap-2"
          onSubmit={async (e) => {
            e.preventDefault()
            if (await act(() => api("POST", `/projects/${p.id}/members`, { email }), `Added to ${p.name}`)) {
              setEmail("")
              onChanged()
            }
          }}
        >
          <Input className="h-8" type="email" placeholder="Add an account by its email" aria-label={`Add an account to ${p.name}`} value={email} onChange={(e) => setEmail(e.target.value)} />
          <Button type="submit" size="sm" variant="outline" disabled={!email.trim()}>
            Add
          </Button>
        </form>
      ) : null}
    </div>
  )
}

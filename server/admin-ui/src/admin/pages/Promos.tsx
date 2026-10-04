import { useCallback, useEffect, useState } from "react"
import { KeyRound, Plus, Shuffle } from "lucide-react"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { PLANS } from "@/world/plans"
import { api, type Me } from "../api"
import { Empty, Section } from "../parts"
import { act } from "./Users"

type Kind = "nothing" | "free_months" | "percent_off" | "amount_off" | "extra_seats" | "extra_storage"
type Promo = {
  id: string
  code: string
  note: string
  active: boolean
  startsAt: string | null
  endsAt: string | null
  plans: string[] | null
  newTeamsOnly: boolean
  kind: Kind
  amount: number
  months: number | null
  maxRedemptions: number | null
  created: string
  redemptions: number
  unlocksTeams: boolean
  text: string
}

const KINDS: { value: Kind; label: string; amount: string }[] = [
  { value: "nothing", label: "Nothing else", amount: "" },
  { value: "free_months", label: "Free months", amount: "" },
  { value: "percent_off", label: "Percent off", amount: "Percent" },
  { value: "amount_off", label: "Euros off each payment", amount: "Euros" },
  { value: "extra_seats", label: "Extra seats", amount: "Seats" },
  { value: "extra_storage", label: "Extra storage", amount: "GB" },
]

type Form = {
  code: string
  note: string
  active: boolean
  starts: string
  ends: string
  plans: string[]
  newTeamsOnly: boolean
  kind: Kind
  amount: string
  months: string
  max: string
  unlocksTeams: boolean
}

const EMPTY: Form = { code: "", note: "", active: true, starts: "", ends: "", plans: [], newTeamsOnly: false, kind: "free_months", amount: "", months: "3", max: "", unlocksTeams: false }
// An access code: lets one person make a team, and nothing else.
const ACCESS: Form = { ...EMPTY, kind: "nothing", months: "", max: "1", unlocksTeams: true }

// Codes are made to be typed: capitals and digits without look-alikes.
function randomCode() {
  const a = "ABCDEFGHJKMNPQRSTVWXYZ23456789"
  const pick = () => a[crypto.getRandomValues(new Uint32Array(1))[0] % a.length]
  return Array.from({ length: 8 }, pick).join("").replace(/^(.{4})/, "$1-")
}

const day = (iso: string | null) => (iso ? iso.slice(0, 10) : "")

export function PromosPage({ me }: { me: Me }) {
  const [rows, setRows] = useState<Promo[] | null>(null)
  const [editing, setEditing] = useState<{ id: string | null; form: Form } | null>(null)
  const admin = me.role === "admin"

  const load = useCallback(async () => {
    try {
      setRows(await api<Promo[]>("GET", "/promos"))
    } catch (e) {
      toast.error((e as Error).message)
    }
  }, [])
  useEffect(() => {
    load()
  }, [load])

  const edit = (p: Promo) =>
    setEditing({
      id: p.id,
      form: {
        code: p.code,
        note: p.note,
        active: p.active,
        starts: day(p.startsAt),
        ends: day(p.endsAt),
        plans: p.plans ?? [],
        newTeamsOnly: p.newTeamsOnly,
        kind: p.kind,
        amount: p.amount ? String(p.amount) : "",
        months: p.months ? String(p.months) : "",
        max: p.maxRedemptions ? String(p.maxRedemptions) : "",
        unlocksTeams: p.unlocksTeams,
      },
    })

  return (
    <div className="grid gap-6">
      <Section
        title="Codes"
        description="Only staff see these. Making a team needs a code that unlocks teams (an access code); a team owner can also type a code on their Billing page. Nothing lists codes to customers."
        action={
          admin ? (
            <div className="flex flex-wrap gap-2">
              <Button variant="outline" onClick={() => setEditing({ id: null, form: { ...ACCESS, code: randomCode() } })}>
                <KeyRound /> New access code
              </Button>
              <Button onClick={() => setEditing({ id: null, form: { ...EMPTY, code: randomCode() } })}>
                <Plus /> New code
              </Button>
            </div>
          ) : null
        }
      >
        {rows && rows.length === 0 ? (
          <Empty>No codes yet.</Empty>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Code</TableHead>
                <TableHead>Gives</TableHead>
                <TableHead className="text-right">Used</TableHead>
                <TableHead>Valid</TableHead>
                <TableHead>Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(rows ?? []).map((p) => (
                <TableRow key={p.id} className={admin ? "cursor-pointer" : undefined} onClick={() => admin && edit(p)}>
                  <TableCell>
                    <span className="font-mono font-semibold">{p.code}</span>
                    {p.note ? <span className="block text-xs text-muted-foreground">{p.note}</span> : null}
                  </TableCell>
                  <TableCell>
                    {p.text}
                    {p.unlocksTeams ? <span className="block text-xs text-muted-foreground">Unlocks making a team</span> : null}
                    {p.newTeamsOnly ? <span className="block text-xs text-muted-foreground">New teams only</span> : null}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {p.redemptions}
                    {p.maxRedemptions ? ` / ${p.maxRedemptions}` : ""}
                  </TableCell>
                  <TableCell className="text-muted-foreground">
                    {p.startsAt || p.endsAt ? `${p.startsAt ? day(p.startsAt) : "now"} to ${p.endsAt ? day(p.endsAt) : "no end"}` : "Always"}
                  </TableCell>
                  <TableCell>{p.active ? <Badge variant="secondary">On</Badge> : <Badge variant="outline">Off</Badge>}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Section>
      {editing ? <PromoDialog key={editing.id ?? "new"} id={editing.id} initial={editing.form} onClose={() => setEditing(null)} onSaved={load} /> : null}
    </div>
  )
}

function PromoDialog({ id, initial, onClose, onSaved }: { id: string | null; initial: Form; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState<Form>(initial)
  const kind = KINDS.find((k) => k.value === f.kind)!
  const set = (patch: Partial<Form>) => setF({ ...f, ...patch })
  const save = async () => {
    const body = {
      code: f.code,
      note: f.note,
      active: f.active,
      startsAt: f.starts ? new Date(f.starts + "T00:00:00").toISOString() : null,
      endsAt: f.ends ? new Date(f.ends + "T23:59:59").toISOString() : null,
      plans: f.plans.length ? f.plans : null,
      newTeamsOnly: f.newTeamsOnly,
      kind: f.kind,
      amount: Number(f.amount) || 0,
      months: f.months ? Number(f.months) : null,
      maxRedemptions: f.max ? Number(f.max) : null,
      unlocksTeams: f.unlocksTeams,
    }
    if (await act(() => api("POST", id ? `/promos/${id}` : "/promos", body), id ? "Code saved" : `Code ${f.code.toUpperCase()} made`)) {
      onSaved()
      onClose()
    }
  }
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-h-[90svh] overflow-y-auto sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>{id ? "Edit code" : f.unlocksTeams && f.kind === "nothing" ? "New access code" : "New promo code"}</DialogTitle>
          <DialogDescription>{f.unlocksTeams ? "Whoever has this code can make a team: give it only to people you let in. Set how many teams it may make under Uses at most. " : ""}Nothing is charged during the beta: the benefit is recorded now and applied when payments start. Extra seats and storage count at once.</DialogDescription>
        </DialogHeader>
        <div className="grid gap-4">
          <div className="grid gap-1.5">
            <Label htmlFor="p-code">Code</Label>
            <div className="flex gap-2">
              <Input id="p-code" className="font-mono uppercase" value={f.code} maxLength={40} onChange={(e) => set({ code: e.target.value })} />
              <Button type="button" variant="outline" size="icon" aria-label="Random code" onClick={() => set({ code: randomCode() })}>
                <Shuffle />
              </Button>
            </div>
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="p-note">Note (only staff see it)</Label>
            <Input id="p-note" value={f.note} placeholder="e.g. Game jam 2026 winners" onChange={(e) => set({ note: e.target.value })} />
          </div>
          <div className="grid gap-4 sm:grid-cols-[1.4fr_1fr_1fr]">
            <div className="grid gap-1.5">
              <Label>Gives</Label>
              <Select value={f.kind} onValueChange={(v) => set({ kind: v as Kind })}>
                <SelectTrigger className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {KINDS.map((k) => (
                    <SelectItem key={k.value} value={k.value}>
                      {k.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            {kind.amount ? (
              <div className="grid gap-1.5">
                <Label htmlFor="p-amount">{kind.amount}</Label>
                <Input id="p-amount" type="number" min={0} step={f.kind === "amount_off" ? "0.01" : "1"} value={f.amount} onChange={(e) => set({ amount: e.target.value })} />
              </div>
            ) : null}
            {f.kind === "nothing" ? null : (
              <div className="grid gap-1.5">
                <Label htmlFor="p-months">{f.kind === "free_months" ? "Months free" : "For months"}</Label>
                <Input id="p-months" type="number" min={1} max={120} placeholder={f.kind === "free_months" ? "" : "Always"} value={f.months} onChange={(e) => set({ months: e.target.value })} />
              </div>
            )}
          </div>
          <div className="grid gap-1.5">
            <Label>Plans it works on (none ticked: all)</Label>
            <div className="flex flex-wrap gap-2">
              {PLANS.map((p) => {
                const on = f.plans.includes(p.id)
                return (
                  <Button key={p.id} type="button" size="sm" variant={on ? "default" : "outline"} aria-pressed={on} onClick={() => set({ plans: on ? f.plans.filter((x) => x !== p.id) : [...f.plans, p.id] })}>
                    {p.name}
                  </Button>
                )
              })}
            </div>
          </div>
          <div className="grid gap-4 sm:grid-cols-3">
            <div className="grid gap-1.5">
              <Label htmlFor="p-starts">Starts</Label>
              <Input id="p-starts" type="date" value={f.starts} onChange={(e) => set({ starts: e.target.value })} />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="p-ends">Ends</Label>
              <Input id="p-ends" type="date" value={f.ends} onChange={(e) => set({ ends: e.target.value })} />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="p-max">Uses at most</Label>
              <Input id="p-max" type="number" min={1} placeholder="No limit" value={f.max} onChange={(e) => set({ max: e.target.value })} />
            </div>
          </div>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={f.unlocksTeams} onChange={(e) => set({ unlocksTeams: e.target.checked })} /> Lets someone make a team (typed on
            "Make your team"; each team made uses it once)
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={f.newTeamsOnly} onChange={(e) => set({ newTeamsOnly: e.target.checked })} /> Only teams made after the code
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={f.active} onChange={(e) => set({ active: e.target.checked })} /> Switched on
          </label>
        </div>
        <DialogFooter className="gap-2">
          {id ? (
            <Button
              variant="ghost"
              className="mr-auto text-bad"
              onClick={async () => {
                if (await act(() => api("POST", `/promos/${id}/delete`), "Code deleted")) {
                  onSaved()
                  onClose()
                }
              }}
            >
              Delete
            </Button>
          ) : null}
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={save}>{id ? "Save" : "Make code"}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}


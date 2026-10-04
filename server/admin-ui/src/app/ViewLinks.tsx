import { useState } from "react"
import { Copy, Eye, Minus, Plus } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { until } from "@/admin/format"
import { Panel } from "@/world/parts"
import { FAILED, call, run } from "./App"
import { api, type Link, type Project } from "./store"

// View links (onboarding.md): a link that downloads the project ready to
// open in Godot, for someone to watch it being made (a playtester, a
// friend). They see everything live but can't change anything, and need no
// account. A project lets in up to five viewers; a link holds its places
// until it is turned off, which also cuts off everyone who used it.

export function linkState(l: Link) {
  if (l.revoked) return "Turned off"
  if (l.expires && new Date(l.expires).getTime() < Date.now()) return "Expired"
  if (l.maxUses && l.uses >= l.maxUses) return "Used up"
  return "Active"
}

// Viewer places a link holds, as the server counts them (TeamStore.ViewersAsync).
export function linkViewers(l: Link) {
  if (l.revoked) return 0
  const live = !l.expires || new Date(l.expires).getTime() > Date.now()
  return l.uses + (live && l.maxUses ? Math.max(0, l.maxUses - l.uses) : 0)
}

export function ViewLinks({ p }: { p: Project }) {
  const free = Math.max(0, p.viewersLimit - p.viewers)
  const [label, setLabel] = useState("")
  const [hours, setHours] = useState("168")
  const [people, setPeople] = useState(1)
  const [url, setUrl] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const count = Math.min(Math.max(1, people), Math.max(1, free))

  return (
    <Panel title="View links" meta={`${p.viewers} of ${p.viewersLimit} viewers`}>
      <p className="mb-4 text-[0.6875rem] leading-relaxed text-dim">
        For someone to watch {p.name} being made: the link downloads it ready to open in Godot. They see every change live but can't change
        anything, and need no account.
      </p>
      {url ? (
        <div className="grid gap-3">
          <p className="text-xs text-text">
            Send this link to {count === 1 ? "the person" : `the ${count} people`}. Copy it now: for safety it isn't shown again.
          </p>
          <div className="flex gap-2">
            <Input readOnly value={url} onFocus={(e) => e.target.select()} className="h-9 text-[0.6875rem]" />
            <Button
              variant="outline"
              size="icon"
              aria-label="Copy link"
              onClick={() =>
                navigator.clipboard?.writeText(url).then(
                  () => toast.success("Link copied"),
                  () => toast.error("Could not copy. Select the text and copy it."),
                )
              }
            >
              <Copy />
            </Button>
          </div>
          <Button variant="ghost" size="sm" className="w-fit px-0" onClick={() => setUrl(null)}>
            Make another link
          </Button>
        </div>
      ) : p.archived ? (
        <p className="text-xs text-dim">The project is archived. Restore it to make links.</p>
      ) : free <= 0 ? (
        <p className="text-xs text-text">All {p.viewersLimit} viewer places are held by links. Turn one off to make room.</p>
      ) : (
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault()
            setBusy(true)
            const res = await call(() => api.createLink(p.id, { label, hours: Number(hours) || null, maxUses: count }))
            setBusy(false)
            if (res !== FAILED) {
              setUrl(res)
              setLabel("")
              setPeople(1)
            }
          }}
        >
          <div className="grid gap-2">
            <Label htmlFor={`vl-for-${p.id}`} className="label-dim">
              Who it's for
            </Label>
            <Input id={`vl-for-${p.id}`} className="h-9" placeholder="Name, so you know later (optional)" maxLength={80} value={label} onChange={(e) => setLabel(e.target.value)} />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div className="grid gap-2">
              <span className="label-dim">Viewers</span>
              <div className="flex h-9 items-center rounded-lg bg-card-2">
                <Button type="button" variant="ghost" size="icon-sm" aria-label="One fewer" disabled={count <= 1} onClick={() => setPeople(count - 1)}>
                  <Minus />
                </Button>
                <span className="flex-1 text-center text-sm text-text tabular-nums" aria-live="polite">
                  {count}
                </span>
                <Button type="button" variant="ghost" size="icon-sm" aria-label="One more" disabled={count >= free} onClick={() => setPeople(count + 1)}>
                  <Plus />
                </Button>
              </div>
            </div>
            <div className="grid gap-2">
              <Label className="label-dim">Works for</Label>
              <Select value={hours} onValueChange={setHours}>
                <SelectTrigger className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="24">1 day</SelectItem>
                  <SelectItem value="168">7 days</SelectItem>
                  <SelectItem value="720">30 days</SelectItem>
                  <SelectItem value="0">Until turned off</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>
          <Button type="submit" disabled={busy}>
            <Eye /> {busy ? "Making link…" : `Make a link for ${count} ${count === 1 ? "viewer" : "viewers"}`}
          </Button>
        </form>
      )}

      {p.links.length ? (
        <div className="mt-5 border-t border-line pt-4">
          <p className="label-dim mb-2">Links</p>
          <ul className="grid gap-1.5">
            {p.links.map((x) => {
              const state = linkState(x)
              const held = linkViewers(x)
              return (
                <li key={x.id} className="flex items-center gap-3 text-xs">
                  <span className="min-w-0 flex-1 truncate text-text">{x.label || "Unnamed link"}</span>
                  <span className="shrink-0 text-dim tabular-nums">
                    {state === "Active" ? `${x.uses}/${x.maxUses ?? "∞"} used · ${x.expires ? until(x.expires) : "no end"}` : state}
                    {held ? ` · holds ${held}` : ""}
                  </span>
                  {x.revoked ? (
                    <Button variant="ghost" size="xs" onClick={() => run(() => api.deleteLink(x.id), "Link removed")}>
                      Remove
                    </Button>
                  ) : (
                    <Button variant="ghost" size="xs" onClick={() => run(() => api.revokeLink(x.id), held ? `Link off: ${held} viewer ${held === 1 ? "place" : "places"} free again` : "Link turned off")}>
                      Turn off
                    </Button>
                  )}
                </li>
              )
            })}
          </ul>
        </div>
      ) : null}
    </Panel>
  )
}

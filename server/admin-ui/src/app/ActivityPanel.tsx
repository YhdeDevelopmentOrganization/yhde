import { useEffect, useState } from "react"
import { ago } from "@/admin/format"
import { cn } from "@/lib/utils"
import { peerColor } from "@/world/people"
import { Panel } from "@/world/parts"
import { api, describe, type Activity, type Project } from "./store"

type Person = { id: string; name: string; changes: number }

// Who changed what in a project, newest first, for everyone or one person
// (GET /api/team/projects/{id}/activity?who=). The latest 200 changes.
export function ActivityPanel({ p, me }: { p: Project; me: string }) {
  const [who, setWho] = useState<string | null>(null)
  const [data, setData] = useState<{ key: string; people: Person[]; rows: Activity[] } | null>(null)
  const key = `${p.id}:${who ?? ""}`
  // New changes arrive with the dashboard's refresh; the list follows them.
  const latest = p.activity[0]?.id

  useEffect(() => {
    let alive = true
    api.activity(p.id, who).then(
      (r) => alive && setData({ key, people: r.people, rows: r.activity.map((a) => ({ ...a, who: a.who || "Someone", text: describe(a.kind, a.path) })) }),
      () => alive && setData({ key, people: [], rows: [] }),
    )
    return () => {
      alive = false
    }
  }, [p.id, who, key, latest])

  // Until the full list arrives, the dashboard's latest changes.
  const rows = data?.key === key ? data.rows : who ? [] : p.activity
  const people = data?.people ?? []

  return (
    <Panel title="Activity" meta={who ? `${people.find((x) => x.id === who)?.name ?? "One person"}'s changes` : "Newest first"} bodyClassName="p-0">
      {people.length > 1 ? (
        <div role="group" aria-label="Show changes by" className="flex flex-wrap gap-1.5 border-b border-line px-4 py-3">
          <Chip on={!who} onClick={() => setWho(null)}>
            Everyone
          </Chip>
          {people.map((x) => (
            <Chip key={x.id} on={who === x.id} onClick={() => setWho(x.id)} color={peerColor(x.name, me)}>
              {x.name} <span className="text-dim tabular-nums">{x.changes}</span>
            </Chip>
          ))}
        </div>
      ) : null}
      {rows.length ? (
        <ol className="max-h-[26rem] overflow-y-auto">
          {rows.map((a) => (
            <li
              key={a.id}
              className="grid grid-cols-[4.5rem_1fr] gap-x-4 gap-y-0.5 border-b border-line px-4 py-2.5 text-xs last:border-b-0 sm:grid-cols-[4.5rem_8rem_1fr]"
            >
              <span className="text-dim tabular-nums">{ago(a.at).replace(" ago", "")}</span>
              <span className="truncate" style={{ color: peerColor(a.who, me) }}>
                {a.who}
              </span>
              <span className="col-span-2 min-w-0 truncate text-text sm:col-span-1">{a.text}</span>
            </li>
          ))}
        </ol>
      ) : (
        <p className="px-4 py-6 text-xs text-dim">{who ? "No changes by them yet." : "Edits show up here as people make them."}</p>
      )}
    </Panel>
  )
}

function Chip({ on, onClick, color, children }: { on: boolean; onClick: () => void; color?: string; children: React.ReactNode }) {
  return (
    <button
      type="button"
      aria-pressed={on}
      onClick={onClick}
      className={cn(
        "inline-flex cursor-pointer items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold transition-colors",
        on ? "bg-card-2 text-text ring-1 ring-sky/60" : "bg-card-2/50 text-dim hover:text-text",
      )}
    >
      {color ? <span className="size-2 rounded-full" style={{ background: color }} aria-hidden="true" /> : null}
      {children}
    </button>
  )
}

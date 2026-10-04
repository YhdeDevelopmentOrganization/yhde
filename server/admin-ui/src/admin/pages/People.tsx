import { useEffect, useState } from "react"
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { ChartContainer, ChartTooltip, ChartTooltipContent, type ChartConfig } from "@/components/ui/chart"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { cn } from "@/lib/utils"
import { api } from "../api"
import { ago, duration, num } from "../format"
import { Empty, Section } from "../parts"
import { Peer } from "@/world/parts"

// In the editor (AdminStats.PeopleAsync): who used YHDE in Godot over a
// range, and each person's numbers in it. Deleted accounts are not counted.
type Range = "hour" | "day" | "week" | "month" | "quarter" | "year" | "all"
type Metric = "people" | "hours" | "changes"
type PeopleData = {
  range: Range
  unit: "5min" | "hour" | "6h" | "day" | "week" | "month"
  from: string
  buckets: { at: string; people: number; hours: number; changes: number }[]
  list: {
    id: string
    name: string
    kind: "account" | "test" | "guest"
    sessions: number
    seconds: number
    lastSeen: string
    version: string
    online: boolean
    changes: number
    chat: number
    comments: number
    projects: string[]
  }[]
}

const RANGES: { id: Range; label: string; long: string }[] = [
  { id: "hour", label: "Hour", long: "Last hour, per 5 minutes" },
  { id: "day", label: "Day", long: "Last 24 hours, per hour" },
  { id: "week", label: "Week", long: "Last 7 days, per 6 hours" },
  { id: "month", label: "Month", long: "Last 30 days, per day" },
  { id: "quarter", label: "3 months", long: "Last 3 months, per week" },
  { id: "year", label: "Year", long: "Last 12 months, per week" },
  { id: "all", label: "All time", long: "Since the first connection, per month" },
]
const METRICS: { id: Metric; label: string; title: string }[] = [
  { id: "people", label: "People", title: "Active people" },
  { id: "hours", label: "Hours online", title: "Hours in the editor" },
  { id: "changes", label: "Changes", title: "Changes made" },
]

// A bucket's start as a short label, and in full for the tooltip.
function tick(at: string, unit: PeopleData["unit"]) {
  const d = new Date(at)
  if (unit === "5min" || unit === "hour") return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
  if (unit === "6h") return d.toLocaleDateString([], { weekday: "short" }) + " " + d.toLocaleTimeString([], { hour: "2-digit" })
  if (unit === "month") return d.toLocaleDateString([], { month: "short", year: "2-digit" })
  return d.toLocaleDateString([], { day: "numeric", month: "short" })
}
function full(at: string, unit: PeopleData["unit"]) {
  const d = new Date(at)
  if (unit === "month") return d.toLocaleDateString([], { month: "long", year: "numeric" })
  if (unit === "week") return "Week of " + d.toLocaleDateString([], { day: "numeric", month: "short", year: "numeric" })
  if (unit === "day") return d.toLocaleDateString([], { weekday: "short", day: "numeric", month: "short" })
  return d.toLocaleString([], { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })
}

export function PeoplePage() {
  const [range, setRange] = useState<Range>("month")
  const [metric, setMetric] = useState<Metric>("people")
  const [data, setData] = useState<PeopleData | null>(null)

  useEffect(() => {
    let alive = true
    const load = () =>
      api<PeopleData>("GET", `/people?range=${range}`).then(
        (d) => alive && setData(d),
        (e: Error) => alive && toast.error(e.message),
      )
    load()
    // The short ranges move while you watch.
    const t = setInterval(() => !document.hidden && load(), range === "hour" || range === "day" ? 30000 : 120000)
    return () => {
      alive = false
      clearInterval(t)
    }
  }, [range])

  const shown = data?.range === range ? data : null
  const m = METRICS.find((x) => x.id === metric)!
  const config: ChartConfig = { [metric]: { label: m.label, color: "var(--accent)" } }
  const total =
    shown && metric !== "people" ? shown.buckets.reduce((n, b) => n + b[metric], 0) : shown ? shown.list.length : 0

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <Toggle items={RANGES} value={range} onChange={setRange} label="Time range" />
        <Toggle items={METRICS} value={metric} onChange={setMetric} label="What to chart" className="ml-auto" />
      </div>

      <Section
        title={m.title}
        description={`${RANGES.find((r) => r.id === range)!.long} · ${
          metric === "hours" ? `${num(Math.round(total))} h in total` : metric === "changes" ? `${num(total)} in total` : `${num(total)} different ${total === 1 ? "person" : "people"}`
        }`}
      >
        {!shown ? (
          <div className="h-56 animate-pulse rounded-xl bg-card-2/50" aria-busy="true" />
        ) : shown.buckets.some((b) => b[metric]) ? (
          <ChartContainer config={config} className="h-56 w-full">
            <BarChart data={shown.buckets}>
              <CartesianGrid vertical={false} />
              <XAxis dataKey="at" tickLine={false} axisLine={false} tickMargin={8} minTickGap={24} tickFormatter={(v) => tick(String(v), shown.unit)} />
              <YAxis tickLine={false} axisLine={false} width={36} allowDecimals={metric === "hours"} />
              <ChartTooltip content={<ChartTooltipContent labelFormatter={(_, p) => full(String(p?.[0]?.payload?.at ?? ""), shown.unit)} />} />
              <Bar dataKey={metric} fill={`var(--color-${metric})`} radius={[6, 6, 2, 2]} />
            </BarChart>
          </ChartContainer>
        ) : (
          <Empty>Nobody was in the editor in this time.</Empty>
        )}
      </Section>

      <Section title="Who" description={`In this time: ${RANGES.find((r) => r.id === range)!.label.toLowerCase()}`}>
        {shown?.list.length ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Projects</TableHead>
                <TableHead className="text-right">Changes</TableHead>
                <TableHead className="text-right">Chat</TableHead>
                <TableHead className="text-right">Comments</TableHead>
                <TableHead className="text-right">Time online</TableHead>
                <TableHead>Last seen</TableHead>
                <TableHead>Add-on</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {shown.list.map((p) => (
                <TableRow key={p.id}>
                  <TableCell className="font-medium">
                    <span className="inline-flex items-center gap-2.5">
                      <Peer name={p.name} size="sm" /> {p.name}
                      {p.kind !== "account" ? <Badge variant="outline">{p.kind === "test" ? "Test" : "Guest"}</Badge> : null}
                    </span>
                  </TableCell>
                  <TableCell className="text-muted-foreground">{p.projects.join(", ") || "-"}</TableCell>
                  <TableCell className="text-right tabular-nums">{num(p.changes)}</TableCell>
                  <TableCell className="text-right tabular-nums">{num(p.chat)}</TableCell>
                  <TableCell className="text-right tabular-nums">{num(p.comments)}</TableCell>
                  <TableCell className="text-right tabular-nums">{duration(p.seconds)}</TableCell>
                  <TableCell>{p.online ? <Badge variant="secondary">Online</Badge> : <span className="text-muted-foreground">{ago(p.lastSeen)}</span>}</TableCell>
                  <TableCell className="text-muted-foreground">{p.version || "-"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : (
          <Empty>{shown ? "Nobody in this time." : "Loading…"}</Empty>
        )}
      </Section>
    </div>
  )
}

function Toggle<T extends string>({
  items,
  value,
  onChange,
  label,
  className,
}: {
  items: { id: T; label: string }[]
  value: T
  onChange: (v: T) => void
  label: string
  className?: string
}) {
  return (
    <div role="group" aria-label={label} className={cn("flex flex-wrap gap-1 rounded-xl bg-card p-1", className)}>
      {items.map((i) => (
        <button
          key={i.id}
          type="button"
          aria-pressed={value === i.id}
          onClick={() => onChange(i.id)}
          className={cn(
            "cursor-pointer rounded-lg px-3 py-1.5 text-xs font-semibold transition-colors",
            value === i.id ? "bg-card-2 text-text" : "text-dim hover:text-text",
          )}
        >
          {i.label}
        </button>
      ))}
    </div>
  )
}

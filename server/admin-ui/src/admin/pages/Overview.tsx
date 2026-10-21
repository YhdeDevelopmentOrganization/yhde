import { useMemo } from "react"
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, ChartTooltipContent, type ChartConfig } from "@/components/ui/chart"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { Data } from "../App"
import type { Activity, Project } from "../api"
import { bytes, compact, last30Days, num, seriesColor, shortDay } from "../format"
import { Empty, Section, Stat } from "../parts"
import { usePolled } from "../usePolled"
import { Peer } from "@/world/parts"

export function OverviewPage({ data }: { data: Data }) {
  const { overview } = data
  const activity = usePolled<Activity>("/stats/activity", 60000)
  const liveProjects = overview.projects.filter((p) => !p.archived).length
  const wait = <Skeleton className="mt-1 h-7 w-20 rounded-lg" />
  const imported = (n: number) => (n ? `+ ${num(n)} imported` : undefined)

  return (
    <div className="grid gap-6">
      <div className="grid grid-cols-2 gap-4 md:grid-cols-5">
        <Stat label="Online now" value={overview.online.length} tone="green" />
        <Stat label="Changes today" value={activity ? num(activity.totals.today) : wait} hint={activity ? imported(activity.totals.importedToday) : undefined} />
        <Stat label="Changes, 7 days" value={activity ? num(activity.totals.week) : wait} hint={activity ? imported(activity.totals.importedWeek) : undefined} />
        <Stat label="People, 30 days" value={activity ? activity.people : wait} />
        <Stat label="Projects" value={liveProjects} hint={`${bytes(overview.storage.bytes)} stored`} />
      </div>
      {activity ? <ChangesChart activity={activity} projects={overview.projects} /> : <Skeleton className="h-80 rounded-2xl bg-card" />}
      <div className="grid gap-6 lg:grid-cols-2">
        <Section title="Online now">
          {overview.online.length ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Project</TableHead>
                  <TableHead>Scene</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {overview.online.map((o, i) => (
                  <TableRow key={i}>
                    <TableCell className="font-medium">
                      <span className="inline-flex items-center gap-2.5">
                        <Peer name={o.name} size="sm" /> {o.name}
                      </span>
                    </TableCell>
                    <TableCell>{o.project}</TableCell>
                    <TableCell className="text-muted-foreground">{o.scene ? o.scene.replace("res://", "") : o.tool || "-"}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : (
            <Empty>Nobody is online.</Empty>
          )}
        </Section>
        {activity ? <HoursChart activity={activity} /> : <Skeleton className="h-72 rounded-2xl bg-card" />}
      </div>
    </div>
  )
}

// The busiest projects get their own colour; the rest are added up as "Other",
// so the legend stays short and every colour means one project. ("zother"
// sorts last: Recharts orders the legend by key.)
const SHOWN_PROJECTS = 5

// Project names for the chart; two projects with the same name are told
// apart by when they were made.
function projectNames(projects: Project[]) {
  const count = new Map<string, number>()
  for (const p of projects) count.set(p.name, (count.get(p.name) ?? 0) + 1)
  const names = new Map<string, string>()
  for (const p of projects)
    names.set(p.id, (count.get(p.name) ?? 0) > 1 ? `${p.name} (${new Date(p.created).toLocaleDateString([], { day: "numeric", month: "short" })})` : p.name)
  return names
}

function ChangesChart({ activity, projects }: { activity: Activity; projects: Project[] }) {
  const { rows, config, keys } = useMemo(() => {
    const totals = new Map<string, number>()
    for (const d of activity.daily) totals.set(d.project, (totals.get(d.project) ?? 0) + d.n)
    const ranked = [...totals.entries()].sort((a, b) => b[1] - a[1]).map(([id]) => id)
    const shown = ranked.length > SHOWN_PROJECTS + 1 ? ranked.slice(0, SHOWN_PROJECTS) : ranked
    const rest = new Set(ranked.filter((id) => !shown.includes(id)))
    const names = projectNames(projects)
    const key = (id: string) => (rest.has(id) ? "zother" : "p" + id.replaceAll("-", ""))
    const config: ChartConfig = {}
    shown.forEach((id, i) => {
      config[key(id)] = { label: names.get(id) ?? "Deleted project", color: seriesColor(i) }
    })
    if (rest.size) config.zother = { label: `${rest.size} other projects`, color: "var(--muted-foreground)" }
    const byDay = new Map<string, Record<string, string | number>>(last30Days().map((day) => [day, { day }]))
    for (const d of activity.daily) {
      const row = byDay.get(d.day.slice(0, 10))
      if (row) row[key(d.project)] = ((row[key(d.project)] as number) || 0) + d.n
    }
    return { rows: [...byDay.values()], config, keys: Object.keys(config) }
  }, [activity, projects])

  return (
    <Section title="Changes" description="Made in the editor, last 30 days">
      {keys.length ? (
        <ChartContainer config={config} className="h-72 w-full">
          <BarChart data={rows}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="day" tickLine={false} axisLine={false} tickMargin={8} minTickGap={24} tickFormatter={shortDay} />
            <YAxis tickLine={false} axisLine={false} width={44} allowDecimals={false} tickFormatter={compact} />
            <ChartTooltip content={<ChartTooltipContent labelFormatter={(v) => shortDay(String(v))} />} />
            {keys.length > 1 ? <ChartLegend content={<ChartLegendContent />} /> : null}
            {keys.map((k, i) => (
              <Bar key={k} dataKey={k} stackId="a" fill={`var(--color-${k})`} radius={i === keys.length - 1 ? [6, 6, 2, 2] : 0} isAnimationActive={false} />
            ))}
          </BarChart>
        </ChartContainer>
      ) : (
        <Empty>No changes in the last 30 days.</Empty>
      )}
    </Section>
  )
}

// Changes per hour of the day, in this browser's time (the server counts in UTC).
function HoursChart({ activity }: { activity: Activity }) {
  const hours = useMemo(() => {
    const offset = -new Date().getTimezoneOffset() / 60
    const hours = Array.from({ length: 24 }, (_, h) => ({ hour: h, changes: 0 }))
    for (const c of activity.heat) hours[(((c.hour + Math.round(offset)) % 24) + 24) % 24].changes += c.n
    return hours
  }, [activity])
  const config: ChartConfig = { changes: { label: "Changes", color: "var(--accent)" } }
  const any = hours.some((h) => h.changes)
  return (
    <Section title="Busiest hours" description="Last 30 days, your time">
      {any ? (
        <ChartContainer config={config} className="h-56 w-full">
          <BarChart data={hours}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="hour" tickLine={false} axisLine={false} tickMargin={8} interval={2} tickFormatter={(h) => `${String(h).padStart(2, "0")}:00`} />
            <YAxis tickLine={false} axisLine={false} width={44} allowDecimals={false} tickFormatter={compact} />
            <ChartTooltip content={<ChartTooltipContent labelFormatter={(_, p) => `${p?.[0]?.payload.hour}:00-${p?.[0]?.payload.hour + 1}:00`} />} />
            <Bar dataKey="changes" fill="var(--color-changes)" radius={[6, 6, 2, 2]} isAnimationActive={false} />
          </BarChart>
        </ChartContainer>
      ) : (
        <Empty>No changes in the last 30 days.</Empty>
      )}
    </Section>
  )
}

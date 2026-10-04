import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, ChartTooltipContent, type ChartConfig } from "@/components/ui/chart"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { Data } from "../App"
import { bytes, last30Days, num, seriesColor, shortDay } from "../format"
import { Empty, Section, Stat } from "../parts"
import { Peer } from "@/world/parts"

export function OverviewPage({ data }: { data: Data }) {
  const { overview, stats } = data
  const activePeople = stats.people.list.filter((p) => Date.now() - new Date(p.lastSeen).getTime() < 30 * 86400000).length
  const liveProjects = overview.projects.filter((p) => !p.archived).length

  return (
    <div className="grid gap-6">
      <div className="grid grid-cols-2 gap-4 md:grid-cols-5">
        <Stat label="Online now" value={overview.online.length} tone="green" />
        <Stat label="Changes today" value={num(stats.activity.totals.today)} />
        <Stat label="Changes, 7 days" value={num(stats.activity.totals.week)} />
        <Stat label="People, 30 days" value={activePeople} />
        <Stat label="Projects" value={liveProjects} hint={`${bytes(stats.storage.stored.bytes)} stored`} />
      </div>
      <ChangesChart data={data} />
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
        <HoursChart data={data} />
      </div>
    </div>
  )
}

function ChangesChart({ data }: { data: Data }) {
  const { overview, stats } = data
  const days = last30Days()
  const ids = [...new Set(stats.activity.daily.map((d) => d.project))]
  const key = (id: string) => "p" + id.replaceAll("-", "")
  const config: ChartConfig = {}
  ids.forEach((id, i) => {
    config[key(id)] = { label: overview.projects.find((p) => p.id === id)?.name ?? "Deleted project", color: seriesColor(i) }
  })
  const rows = days.map((day) => {
    const row: Record<string, string | number> = { day }
    for (const d of stats.activity.daily) if (d.day.slice(0, 10) === day) row[key(d.project)] = ((row[key(d.project)] as number) || 0) + d.n
    return row
  })

  return (
    <Section title="Changes" description="Last 30 days">
      {ids.length ? (
        <ChartContainer config={config} className="h-64 w-full">
          <BarChart data={rows}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="day" tickLine={false} axisLine={false} tickMargin={8} minTickGap={24} tickFormatter={shortDay} />
            <YAxis tickLine={false} axisLine={false} width={40} allowDecimals={false} />
            <ChartTooltip content={<ChartTooltipContent labelFormatter={(v) => shortDay(String(v))} />} />
            {ids.length > 1 ? <ChartLegend content={<ChartLegendContent />} /> : null}
            {ids.map((id, i) => (
              <Bar key={id} dataKey={key(id)} stackId="a" fill={`var(--color-${key(id)})`} radius={i === ids.length - 1 ? [6, 6, 2, 2] : 0} />
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
function HoursChart({ data }: { data: Data }) {
  const offset = -new Date().getTimezoneOffset() / 60
  const hours = Array.from({ length: 24 }, (_, h) => ({ hour: h, changes: 0 }))
  for (const c of data.stats.activity.heat) hours[(((c.hour + Math.round(offset)) % 24) + 24) % 24].changes += c.n
  const config: ChartConfig = { changes: { label: "Changes", color: "var(--accent)" } }
  const any = hours.some((h) => h.changes)
  return (
    <Section title="Busiest hours" description="Last 30 days, your time">
      {any ? (
        <ChartContainer config={config} className="h-56 w-full">
          <BarChart data={hours}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="hour" tickLine={false} axisLine={false} tickMargin={8} interval={2} tickFormatter={(h) => `${h}:00`} />
            <YAxis tickLine={false} axisLine={false} width={40} allowDecimals={false} />
            <ChartTooltip content={<ChartTooltipContent labelFormatter={(_, p) => `${p?.[0]?.payload.hour}:00-${p?.[0]?.payload.hour + 1}:00`} />} />
            <Bar dataKey="changes" fill="var(--color-changes)" radius={[6, 6, 2, 2]} />
          </BarChart>
        </ChartContainer>
      ) : (
        <Empty>No changes in the last 30 days.</Empty>
      )}
    </Section>
  )
}

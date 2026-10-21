import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { ChartContainer, ChartTooltip, ChartTooltipContent, type ChartConfig } from "@/components/ui/chart"
import { Progress } from "@/components/ui/progress"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Skeleton } from "@/components/ui/skeleton"
import type { Data } from "../App"
import type { Storage } from "../api"
import { ago, bytes, num } from "../format"
import { Empty, Section, Stat } from "../parts"
import { usePolled } from "../usePolled"

export function StoragePage({ data }: { data: Data }) {
  const storage = usePolled<Storage>("/stats/storage", 300000)
  const disk = data.health.disk
  const name = (id: string) => data.overview.projects.find((p) => p.id === id)?.name ?? "Deleted project"
  const used = disk.total ? (disk.total - disk.free) / disk.total : 0
  const backups = data.overview.backups

  // Uploads stop below minFree (BlobStore): warn well before that.
  const low = disk.total > 0 && disk.free < disk.minFree * 3
  const wait = <Skeleton className="mt-1 h-7 w-20 rounded-lg" />

  return (
    <div className="grid gap-6">
      {low ? (
        <p role="alert" className="rounded-lg bg-tint-rust px-4 py-3 text-sm text-text">
          The disk is nearly full: {bytes(disk.free)} free. Editors can't share new files once less than{" "}
          {bytes(disk.minFree)} is free. Delete unused projects or give the server more disk.
        </p>
      ) : null}
      <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Stat label="Game files" value={storage ? bytes(storage.stored.bytes) : wait} hint={storage ? `${num(storage.stored.files)} stored files, old versions included` : undefined} />
        <Stat label="Database" value={storage ? bytes(storage.database) : wait} />
        <Stat
          label="Disk free"
          value={disk.total ? bytes(disk.free) : "-"}
          hint={disk.total ? <Progress value={used * 100} className="mt-1" /> : undefined}
        />
        <Stat label="Largest file allowed" value={bytes(data.overview.storage.maxFileBytes)} />
      </div>
      {storage ? (
        <>
      <div className="grid gap-6 lg:grid-cols-2">
        <SizeChart
          title="By project"
          description="Files in each project now"
          rows={topProjects(storage.byProject, name)}
        />
        <SizeChart title="By file type" description="Files in use now, each counted once" rows={storage.byKind.map((k) => ({ name: k.kind, bytes: k.bytes, files: k.files }))} />
      </div>
      <Section title="Largest files">
        {storage.biggest.length ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>File</TableHead>
                <TableHead>Project</TableHead>
                <TableHead className="text-right">Size</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {storage.biggest.map((f, i) => (
                <TableRow key={i}>
                  <TableCell className="font-mono text-xs">{f.path.replace("res://", "")}</TableCell>
                  <TableCell className="text-muted-foreground">{name(f.project)}</TableCell>
                  <TableCell className="text-right tabular-nums">{bytes(f.size)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : (
          <Empty>No files yet.</Empty>
        )}
      </Section>
        </>
      ) : (
        <Skeleton className="h-96 rounded-2xl bg-card" />
      )}
      <Section title="Backups">
        {!backups.configured ? (
          <Empty>Backups are not visible to the server.</Empty>
        ) : backups.files.length ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>File</TableHead>
                <TableHead className="text-right">Size</TableHead>
                <TableHead>Taken</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {backups.files.map((f) => (
                <TableRow key={f.name}>
                  <TableCell className="font-mono text-xs">{f.name}</TableCell>
                  <TableCell className="text-right tabular-nums">{bytes(f.bytes)}</TableCell>
                  <TableCell className="text-muted-foreground">{ago(f.at)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : (
          <Empty>No backups yet.</Empty>
        )}
      </Section>
    </div>
  )
}

// The biggest projects by name, the rest added up in one row.
function topProjects(list: Storage["byProject"], name: (id: string) => string, shown = 12) {
  const sorted = [...list].sort((a, b) => b.bytes - a.bytes)
  const rows = sorted.slice(0, shown).map((p) => ({ name: name(p.project), bytes: p.bytes, files: p.files }))
  const rest = sorted.slice(shown)
  if (rest.length)
    rows.push({ name: `${rest.length} others`, bytes: rest.reduce((s, p) => s + p.bytes, 0), files: rest.reduce((s, p) => s + p.files, 0) })
  return rows
}

function SizeChart({ title, description, rows }: { title: string; description?: string; rows: { name: string; bytes: number; files: number }[] }) {
  const config: ChartConfig = { bytes: { label: "Size", color: "var(--accent)" } }
  return (
    <Section title={title} description={description}>
      {rows.length ? (
        <ChartContainer config={config} className="w-full" style={{ height: Math.max(120, rows.length * 36 + 24) }}>
          <BarChart data={rows} layout="vertical" margin={{ left: 8, right: 16 }}>
            <CartesianGrid horizontal={false} />
            <XAxis type="number" tickLine={false} axisLine={false} tickFormatter={(v) => bytes(Number(v))} />
            <YAxis type="category" dataKey="name" tickLine={false} axisLine={false} width={120} />
            <ChartTooltip
              content={
                <ChartTooltipContent
                  formatter={(value, _name, item) => (
                    <span className="tabular-nums">
                      {bytes(Number(value))} · {num(item.payload.files)} files
                    </span>
                  )}
                />
              }
            />
            <Bar dataKey="bytes" fill="var(--color-bytes)" radius={[2, 6, 6, 2]} isAnimationActive={false} />
          </BarChart>
        </ChartContainer>
      ) : (
        <Empty>No files yet.</Empty>
      )}
    </Section>
  )
}

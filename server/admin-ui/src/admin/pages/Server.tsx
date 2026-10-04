import { useState } from "react"
import { RefreshCw } from "lucide-react"
import { toast } from "sonner"
import { Area, AreaChart, Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { ChartContainer, ChartTooltip, ChartTooltipContent, type ChartConfig } from "@/components/ui/chart"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { Data } from "../App"
import { api, type Commit } from "../api"
import { ago, dateTime, duration, num, shortSha } from "../format"
import { Empty, Section, Stat } from "../parts"

const KIND_NAMES: Record<string, string> = {
  ChangeProperty: "Property changes",
  CreateNode: "Nodes added",
  DeleteNode: "Nodes deleted",
  MoveNode: "Nodes moved",
  RenameNode: "Nodes renamed",
  ReorderNode: "Nodes reordered",
  ChangeNodeType: "Node types changed",
  SetSceneRoot: "Scene roots changed",
  AddResource: "Resources added",
  RemoveResource: "Resources removed",
  ChangeResourceProperty: "Resource changes",
  EditText: "Script typing",
  RegisterAsset: "Files added",
  UpdateAsset: "Files changed",
  MoveAsset: "Files moved",
  DeleteAsset: "Files deleted",
}

export function ServerPage({ data, refresh }: { data: Data; refresh: () => Promise<void> }) {
  const h = data.stats.health
  return (
    <div className="grid gap-6">
      <UpdatesSection data={data} refresh={refresh} />
      <div className="grid grid-cols-2 gap-4 md:grid-cols-5">
        <Stat label="Uptime" value={duration((Date.now() - new Date(h.started).getTime()) / 1000)} />
        <Stat label="Connections" value={h.connections} />
        <Stat label="CPU" value={`${h.cpu}%`} hint={`${h.cores} cores`} />
        <Stat label="Memory" value={`${Math.round(h.memoryMb)} MB`} />
        <Stat label="Server errors" value={num(h.errors)} hint={`of ${num(h.requests)} requests`} />
      </div>
      <div className="grid gap-6 lg:grid-cols-2">
        <TimeChart title="Connections" data={data} field="connections" color="var(--accent)" />
        <TimeChart title="CPU" data={data} field="cpu" color="var(--accent)" unit="%" />
        <TimeChart title="Memory" data={data} field="memoryMb" color="var(--accent)" unit=" MB" />
        <TimeChart title="Requests" data={data} field="requests" color="var(--accent)" description="Per minute" />
      </div>
      <KindsChart data={data} />
    </div>
  )
}

function UpdatesSection({ data, refresh }: { data: Data; refresh: () => Promise<void> }) {
  const u = data.updates
  const [confirm, setConfirm] = useState<Commit | null>(null)
  const st = u.status
  const busy = !!st?.updating || !!u.requested

  if (!u.configured) {
    return (
      <Section title="Updates">
        <p className="text-sm text-muted-foreground">
          Run <code className="rounded bg-muted px-1.5 py-0.5 text-foreground">cd /opt/yhde &amp;&amp; git pull --ff-only &amp;&amp; deploy/yhde update</code> once on the server
          to manage updates here.
        </p>
      </Section>
    )
  }

  return (
    <Section
      title="Updates"
      description={st?.current ? `Running ${st.current.subject} (${shortSha(st.current.sha)}) · checked ${ago(st.checkedAt)}` : "Waiting for the update checker"}
      action={
        <Button
          variant="outline"
          size="sm"
          disabled={u.checkRequested}
          onClick={async () => {
            try {
              await api("POST", "/server-update/check")
              toast.success("Checking for updates")
              await refresh()
            } catch (e) {
              toast.error((e as Error).message)
            }
          }}
        >
          <RefreshCw /> {u.checkRequested ? "Checking…" : "Check now"}
        </Button>
      }
    >
      <div className="grid gap-4">
        {st?.updating ? (
          <Alert>
            <AlertTitle>Updating to {shortSha(st.updating.target)}</AlertTitle>
            <AlertDescription>Started {ago(st.updating.startedAt)}. The server restarts; sign in again when it is back.</AlertDescription>
          </Alert>
        ) : u.requested ? (
          <Alert>
            <AlertTitle>Update to {shortSha(u.requested)} requested</AlertTitle>
            <AlertDescription>It starts within a minute.</AlertDescription>
          </Alert>
        ) : st?.last ? (
          st.last.ok ? (
            <Alert>
              <AlertTitle>Updated to {shortSha(st.last.to)}</AlertTitle>
              <AlertDescription>{ago(st.last.at)}</AlertDescription>
            </Alert>
          ) : (
            <Alert variant="destructive">
              <AlertTitle>Update failed, still on {shortSha(st.last.from)}</AlertTitle>
              <AlertDescription>
                {st.last.message} ({ago(st.last.at)})
              </AlertDescription>
            </Alert>
          )
        ) : null}
        {st?.fetchError ? (
          <Alert variant="destructive">
            <AlertTitle>Could not reach GitHub</AlertTitle>
            <AlertDescription>{st.fetchError}</AlertDescription>
          </Alert>
        ) : null}
        {st && st.available.length ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Version</TableHead>
                <TableHead>Author</TableHead>
                <TableHead>Date</TableHead>
                <TableHead className="w-0" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {st.available.map((c, i) => (
                <TableRow key={c.sha}>
                  <TableCell>
                    <div className="font-medium">{c.subject}</div>
                    <div className="font-mono text-xs text-muted-foreground">
                      {shortSha(c.sha)} {i === 0 ? <Badge variant="secondary" className="ml-1">Latest</Badge> : null}
                    </div>
                  </TableCell>
                  <TableCell className="text-muted-foreground">{c.author}</TableCell>
                  <TableCell className="text-muted-foreground">{dateTime(c.date)}</TableCell>
                  <TableCell>
                    <Button size="sm" variant={i === 0 ? "default" : "outline"} disabled={busy} onClick={() => setConfirm(c)}>
                      Update
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : st ? (
          <Empty>Up to date.</Empty>
        ) : null}
      </div>
      <AlertDialog open={!!confirm} onOpenChange={(o) => !o && setConfirm(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Update the server?</AlertDialogTitle>
            <AlertDialogDescription>
              To “{confirm?.subject}”. A backup is taken first. Everyone is disconnected for a minute or two and reconnects automatically. If the new
              version fails to start, the server goes back to the current one.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={async () => {
                try {
                  await api("POST", "/server-update", { commit: confirm!.sha })
                  toast.success("Update requested")
                  await refresh()
                } catch (e) {
                  toast.error((e as Error).message)
                }
              }}
            >
              Update
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Section>
  )
}

type Field = "connections" | "cpu" | "memoryMb" | "requests"

function TimeChart({ title, description, data, field, color, unit = "" }: { title: string; description?: string; data: Data; field: Field; color: string; unit?: string }) {
  const rows = data.stats.health.samples.map((s) => ({ t: new Date(s.at).getTime(), v: s[field] }))
  const config: ChartConfig = { v: { label: title, color } }
  const time = (t: number) => new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
  return (
    <Section title={title} description={description ?? "Last 24 hours"}>
      {rows.length > 1 ? (
        <ChartContainer config={config} className="h-48 w-full">
          <AreaChart data={rows}>
            <CartesianGrid vertical={false} />
            <XAxis dataKey="t" type="number" scale="time" domain={["dataMin", "dataMax"]} tickLine={false} axisLine={false} tickMargin={8} minTickGap={40} tickFormatter={time} />
            <YAxis tickLine={false} axisLine={false} width={48} allowDecimals={field === "cpu"} tickFormatter={(v) => `${v}${unit}`} />
            <ChartTooltip
              content={
                <ChartTooltipContent
                  labelFormatter={(_, p) => time(p?.[0]?.payload.t)}
                  formatter={(v) => (
                    <span className="tabular-nums">
                      {typeof v === "number" ? Math.round(v * 10) / 10 : String(v)}
                      {unit}
                    </span>
                  )}
                />
              }
            />
            <Area dataKey="v" type="stepAfter" stroke="var(--color-v)" fill="var(--color-v)" fillOpacity={0.12} strokeWidth={1.5} isAnimationActive={false} />
          </AreaChart>
        </ChartContainer>
      ) : (
        <Empty>No data yet.</Empty>
      )}
    </Section>
  )
}

function KindsChart({ data }: { data: Data }) {
  const rows = data.stats.activity.kinds.map((k) => ({ name: KIND_NAMES[k.type] ?? k.type, n: k.n }))
  const config: ChartConfig = { n: { label: "Changes", color: "var(--accent)" } }
  return (
    <Section title="Kinds of changes" description="Last 30 days">
      {rows.length ? (
        <ChartContainer config={config} className="w-full" style={{ height: Math.max(120, rows.length * 32 + 24) }}>
          <BarChart data={rows} layout="vertical" margin={{ left: 8, right: 16 }}>
            <CartesianGrid horizontal={false} />
            <XAxis type="number" tickLine={false} axisLine={false} allowDecimals={false} />
            <YAxis type="category" dataKey="name" tickLine={false} axisLine={false} width={140} />
            <ChartTooltip content={<ChartTooltipContent />} />
            <Bar dataKey="n" fill="var(--color-n)" radius={[2, 6, 6, 2]} />
          </BarChart>
        </ChartContainer>
      ) : (
        <Empty>No changes in the last 30 days.</Empty>
      )}
    </Section>
  )
}

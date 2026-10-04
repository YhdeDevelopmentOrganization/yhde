import { useEffect, useRef, useState } from "react"
import { CheckCircle2, RefreshCw, XCircle } from "lucide-react"
import { Button } from "@/components/ui/button"
import { cn } from "@/lib/utils"
import { PageTop } from "../parts"

// Asks the server's health check from this browser and shows the answer and
// how long it took. Nothing here is made up: it is this visit's own checks.
type Check = { at: number; ok: boolean; ms: number }
const EVERY = 20_000

async function ping(): Promise<Check> {
  const start = performance.now()
  try {
    const res = await fetch("/health", { cache: "no-store" })
    const ok = res.ok && (await res.text()).trim().toLowerCase() === "healthy"
    return { at: Date.now(), ok, ms: Math.round(performance.now() - start) }
  } catch {
    return { at: Date.now(), ok: false, ms: Math.round(performance.now() - start) }
  }
}

const TEXT = {
  title: "System",
  accent: "status",
  lead: "Checked live from your browser every 20 seconds.",
  up: "Everything is working",
  down: "We can't reach the server",
  checking: "Checking…",
  last: (time: string, ms: number) => `Last check ${time}, answered in ${ms} ms.`,
  wait: "One moment.",
  downNote: " If this stays, your editor keeps your changes and sends them when it's back.",
  again: "Check now",
  parts: ["Live editing (sync server)", "Invite links and downloads", "Dashboard and website"],
  working: "Working",
  notReachable: "Not reachable",
  checkingShort: "Checking",
  history: "Checks during this visit",
  sofar: (n: number) => `${n} so far`,
  historyLabel: (ok: number, all: number) => `${ok} of ${all} checks answered`,
  okShort: "OK",
  failed: "Failed",
  note: "Taller bars took longer to answer. Response times include your own connection.",
}

export function Status() {
  const t = TEXT
  const [checks, setChecks] = useState<Check[]>([])
  const [busy, setBusy] = useState(false)
  const timer = useRef<number | undefined>(undefined)

  const run = async () => {
    setBusy(true)
    const c = await ping()
    setChecks((all) => [...all, c].slice(-40))
    setBusy(false)
  }

  useEffect(() => {
    run()
    timer.current = window.setInterval(() => !document.hidden && run(), EVERY)
    return () => clearInterval(timer.current)
  }, [])

  const last = checks[checks.length - 1]
  const state: "checking" | "up" | "down" = !last ? "checking" : last.ok ? "up" : "down"
  const time = (ms: number) => new Date(ms).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" })

  return (
    <>
      <PageTop title={t.title} accent={t.accent} lead={t.lead} />
      <div className="mx-auto grid max-w-4xl gap-4 px-5 pb-16 sm:px-7">
        <div className={cn("flex flex-wrap items-center gap-4 rounded-[1.5rem] p-7", state === "up" ? "bg-tint-green" : state === "down" ? "bg-tint-rust" : "bg-card")} role="status">
          {state === "down" ? <XCircle className="size-8 text-bad" aria-hidden="true" /> : <CheckCircle2 className={cn("size-8", state === "up" ? "text-good" : "text-dim")} aria-hidden="true" />}
          <div className="min-w-0 flex-1">
            <p className="text-2xl font-bold text-text">{state === "up" ? t.up : state === "down" ? t.down : t.checking}</p>
            <p className="mt-1 text-sm text-text/75">
              {last ? t.last(time(last.at), last.ms) : t.wait}
              {state === "down" ? t.downNote : ""}
            </p>
          </div>
          <Button variant="secondary" onClick={run} disabled={busy}>
            <RefreshCw className={cn(busy && "animate-spin")} aria-hidden="true" /> {t.again}
          </Button>
        </div>

        <div className="rounded-[1.5rem] bg-card p-7">
          <ul className="grid gap-1">
            {t.parts.map((p) => (
              <li key={p} className="flex items-center gap-3 border-b border-line py-3.5 last:border-b-0">
                <span className="flex-1 font-medium text-text">{p}</span>
                <span className={cn("rounded-full px-2.5 py-0.5 text-xs font-semibold", state === "up" ? "bg-tint-green text-good" : state === "down" ? "bg-tint-rust text-bad" : "bg-card-2 text-dim")}>
                  {state === "up" ? t.working : state === "down" ? t.notReachable : t.checkingShort}
                </span>
              </li>
            ))}
          </ul>
        </div>

        <div className="rounded-[1.5rem] bg-card p-7">
          <div className="flex items-baseline gap-3">
            <h2 className="text-lg font-bold text-text">{t.history}</h2>
            <span className="text-sm text-dim">{t.sofar(checks.length)}</span>
          </div>
          <div className="mt-5 flex h-16 items-end gap-1 select-none" role="img" aria-label={t.historyLabel(checks.filter((c) => c.ok).length, checks.length)}>
            {Array.from({ length: 40 }, (_, i) => {
              const c = checks[i - (40 - checks.length)]
              return (
                <div
                  key={i}
                  title={c ? `${c.ok ? t.okShort : t.failed} · ${c.ms} ms` : undefined}
                  className={cn("flex-1 rounded-md", !c ? "bg-card-2" : c.ok ? "bg-good" : "bg-bad")}
                  style={{ height: c ? `${Math.max(30, Math.min(100, 30 + c.ms / 4))}%` : "30%" }}
                />
              )
            })}
          </div>
          <p className="mt-3 text-xs text-dim">{t.note}</p>
        </div>
      </div>
    </>
  )
}

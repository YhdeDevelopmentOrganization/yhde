import { useCallback, useEffect, useState } from "react"
import { OFFICIAL } from "@/world/site"
import { Activity, Building2, FolderKanban, Globe, HardDrive, LogOut, Package, ScrollText, Server, TicketPercent, UserCog, Users, WifiOff } from "lucide-react"
import { versionFull } from "@/world/version"
import { Toaster } from "@/components/ui/sonner"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { TooltipProvider } from "@/components/ui/tooltip"
import { cn } from "@/lib/utils"
import { LiveTag, Meter, Wordmark } from "@/world/parts"
import { api, SignedOut, type Me, type Overview, type Stats, type Updates } from "./api"
import { bytes, duration } from "./format"
import { OverviewPage } from "./pages/Overview"
import { ProjectsPage } from "./pages/Projects"
import { PeoplePage } from "./pages/People"
import { StoragePage } from "./pages/Storage"
import { ServerPage } from "./pages/Server"
import { AddonPage } from "./pages/Addon"
import { SitePage } from "./pages/Site"
import { UsersPage } from "./pages/Users"
import { TeamsPage } from "./pages/Teams"
import { PromosPage } from "./pages/Promos"
import { AuditPage } from "./pages/Audit"

export type Data = { overview: Overview; stats: Stats; updates: Updates; me: Me }

const TABS = [
  { id: "overview", label: "Overview", icon: Activity },
  { id: "projects", label: "Projects", icon: FolderKanban },
  { id: "people", label: "In the editor", icon: Users },
  { id: "users", label: "Accounts", icon: UserCog },
  { id: "teams", label: "Owners", icon: Building2 },
  { id: "promos", label: "Promo codes", icon: TicketPercent },
  { id: "storage", label: "Storage", icon: HardDrive },
  { id: "server", label: "Server", icon: Server },
  { id: "add-on", label: "Add-on", icon: Package },
  { id: "site", label: "Website", icon: Globe },
  { id: "audit", label: "Audit log", icon: ScrollText },
] as const
type Tab = (typeof TABS)[number]["id"]
// Promo codes are for YHDE's plans: a self-hosted server has none (SiteMode.cs).
const SHOWN = TABS.filter((t) => OFFICIAL || t.id !== "promos")
const isTab = (t: string): t is Tab => SHOWN.some((x) => x.id === t)

export default function App() {
  const [data, setData] = useState<Data | null>(null)
  const [signedOut, setSignedOut] = useState(false)
  // When the last refresh failed (server restarting, connection lost), the
  // page keeps showing the last data and says so.
  const [unreachable, setUnreachable] = useState<Date | null>(null)
  const [loadedAt, setLoadedAt] = useState<Date | null>(null)
  const [tab, setTab] = useState<Tab>(() => {
    const h = location.hash.slice(1)
    return isTab(h) ? h : "overview"
  })

  const refresh = useCallback(async () => {
    try {
      const [overview, stats, updates, me] = await Promise.all([
        api<Overview>("GET", "/overview"),
        api<Stats>("GET", "/stats"),
        api<Updates>("GET", "/server-update").catch(() => ({ configured: false })),
        api<Me>("GET", "/me"),
      ])
      setData({ overview, stats, updates, me })
      setSignedOut(false)
      setUnreachable(null)
      setLoadedAt(new Date())
    } catch (e) {
      if (e instanceof SignedOut) setSignedOut(true)
      else setUnreachable((u) => u ?? new Date())
    }
  }, [])

  useEffect(() => {
    refresh()
    const t = setInterval(() => !document.hidden && refresh(), 15000)
    return () => clearInterval(t)
  }, [refresh])

  useEffect(() => {
    const on = () => {
      const h = location.hash.slice(1)
      if (isTab(h)) setTab(h)
    }
    addEventListener("hashchange", on)
    return () => removeEventListener("hashchange", on)
  }, [])

  if (signedOut) return <SignIn onSignedIn={refresh} />
  if (!data && unreachable)
    return (
      <div className="grid min-h-svh place-items-center px-5">
        <div className="grid max-w-md justify-items-center gap-4 rounded-[1.75rem] bg-card p-9 text-center">
          <span className="grid size-14 place-items-center rounded-2xl bg-tint-rust text-bad">
            <WifiOff className="size-7" />
          </span>
          <h1 className="text-2xl font-bold text-text">Can't reach the server</h1>
          <p className="text-dim">It may be restarting after an update. This page tries again every 15 seconds.</p>
          <Button onClick={refresh}>Try now</Button>
        </div>
      </div>
    )
  if (!data) return <Loading />

  const waiting = data.updates.status?.available.length ?? 0
  const badge: Partial<Record<Tab, number>> = { server: waiting, "add-on": data.overview.addonPending ? 1 : 0 }
  const disk = data.stats.storage.disk
  const h = data.stats.health

  return (
    <TooltipProvider>
      <div className="lg:grid lg:min-h-svh lg:grid-cols-[15.5rem_1fr]">
        <aside className="bg-side lg:sticky lg:top-0 lg:flex lg:h-svh lg:flex-col">
          <div className="flex items-center gap-3 px-4 py-4 lg:block lg:px-5 lg:py-5">
            <Wordmark sub="Server" />
            <p className="ml-auto text-xs font-semibold text-sky lg:mt-4 lg:ml-0">{versionFull(data.overview.version)}</p>
          </div>
          <nav className="flex gap-1 overflow-x-auto px-3 pb-3 lg:grid lg:gap-0.5 lg:pb-0" aria-label="Admin">
            {SHOWN.map((t) => (
              <a
                key={t.id}
                href={"#" + t.id}
                aria-current={tab === t.id ? "page" : undefined}
                className={cn(
                  "flex shrink-0 items-center gap-3 rounded-xl px-3 py-2.5 text-[0.9375rem] font-medium transition-colors",
                  tab === t.id ? "bg-card-2 text-text" : "text-dim hover:bg-card/70 hover:text-text",
                )}
              >
                <t.icon className={cn("size-4", tab === t.id ? "text-sky" : "")} /> {t.label}
                {badge[t.id] ? <span className="ml-auto rounded-full bg-sky px-2 text-xs leading-5 font-bold text-sky-ink">{badge[t.id]}</span> : null}
              </a>
            ))}
          </nav>
          <div className="mx-3 mt-auto mb-3 hidden gap-4 rounded-2xl bg-card p-4 lg:grid">
            {data.overview.online.length ? <LiveTag>{data.overview.online.length} online</LiveTag> : <span className="label-dim">Nobody online</span>}
            <div className="grid gap-2">
              <div className="flex items-baseline justify-between">
                <span className="label-dim">Disk</span>
                <span className="text-xs text-text tabular-nums">{disk.total ? `${bytes(disk.free)} free` : "-"}</span>
              </div>
              {disk.total ? <Meter value={(disk.total - disk.free) / disk.total} color={disk.free / disk.total < 0.1 ? "var(--bad)" : undefined} /> : null}
            </div>
            <p className="text-[0.6875rem] text-dim">
              Up {duration((Date.now() - new Date(h.started).getTime()) / 1000)} · CPU {h.cpu}%
            </p>
            <p className="truncate text-xs text-text" title={data.me.email}>
              {data.me.name} <span className="text-dim capitalize">· {data.me.account ? data.me.role : "emergency sign-in"}</span>
            </p>
            <Button
              variant="ghost"
              size="sm"
              className="justify-start px-0"
              onClick={async () => {
                await api("POST", "/logout").catch(() => {})
                setSignedOut(true)
              }}
            >
              <LogOut /> Sign out
            </Button>
          </div>
        </aside>
        <main className="min-w-0 px-4 py-6 sm:px-6 lg:px-10 lg:py-9">
          {unreachable ? (
            <div role="status" className="mb-6 flex flex-wrap items-center gap-3 rounded-2xl bg-tint-rust px-5 py-3.5 text-sm text-text">
              <WifiOff className="size-4 text-bad" />
              <span className="font-semibold">Can't reach the server right now.</span>
              <span className="text-text/75">
                Showing what we had at {loadedAt?.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }) ?? "the start"}. Trying again every 15 seconds.
              </span>
              <Button size="sm" variant="secondary" className="ml-auto" onClick={refresh}>
                Try now
              </Button>
            </div>
          ) : null}
          {data.me.role === "support" ? (
            <p className="mb-6 rounded-2xl bg-tint-blue px-5 py-3.5 text-sm text-text">
              You're signed in as <strong>Support</strong>: you can see everything and help people (sign them out, send a confirmation email). Other changes need an admin.
            </p>
          ) : !data.me.account ? (
            <p className="mb-6 rounded-2xl bg-tint-rust px-5 py-3.5 text-sm text-text">
              You're using the server password, the emergency way in. Give your own account the Admin role under <a className="font-semibold text-sky hover:underline" href="#users">Accounts</a>, and sign in with it from now on.
            </p>
          ) : null}
          <div key={tab} className="animate-in fade-in-0 slide-in-from-bottom-1 duration-300">
          {tab !== "projects" ? (
            <h1 className="display mb-8 text-[2.25rem] text-text">
              {TABS.find((t) => t.id === tab)!.label}
            </h1>
          ) : null}
          {tab === "overview" ? (
            <OverviewPage data={data} />
          ) : tab === "projects" ? (
            <ProjectsPage data={data} refresh={refresh} />
          ) : tab === "people" ? (
            <PeoplePage />
          ) : tab === "storage" ? (
            <StoragePage data={data} />
          ) : tab === "server" ? (
            <ServerPage data={data} refresh={refresh} />
          ) : tab === "site" ? (
            <SitePage />
          ) : tab === "users" ? (
            <UsersPage me={data.me} />
          ) : tab === "teams" ? (
            <TeamsPage me={data.me} />
          ) : tab === "promos" ? (
            <PromosPage me={data.me} />
          ) : tab === "audit" ? (
            <AuditPage />
          ) : (
            <AddonPage data={data} refresh={refresh} />
          )}
          </div>
        </main>
      </div>
      <Toaster position="bottom-right" />
    </TooltipProvider>
  )
}

function Loading() {
  return (
    <div className="lg:grid lg:min-h-svh lg:grid-cols-[15.5rem_1fr]" aria-busy="true" aria-label="Loading">
      <div className="hidden border-r border-line bg-card lg:block" />
      <div className="grid content-start gap-4 px-10 py-9">
        <Skeleton className="h-8 w-48 rounded-2xl bg-card" />
        <div className="grid grid-cols-2 gap-4 md:grid-cols-5">
          {Array.from({ length: 5 }, (_, i) => (
            <Skeleton key={i} className="h-20 rounded-2xl bg-card" />
          ))}
        </div>
        <Skeleton className="h-64 rounded-2xl bg-card" />
      </div>
    </div>
  )
}

function SignIn({ onSignedIn }: { onSignedIn: () => Promise<void> }) {
  const [site, setSite] = useState<{ signedIn: boolean; name?: string; email?: string; staff?: boolean } | null>(null)
  const [email, setEmail] = useState("")
  const [password, setPassword] = useState("")
  const [serverPassword, setServerPassword] = useState("")
  const [error, setError] = useState("")
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    api<{ signedIn: boolean; name?: string; email?: string; staff?: boolean }>("GET", "/login/site").then(setSite, () => setSite({ signedIn: false }))
  }, [])
  const attempt = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    setError("")
    try {
      await fn()
      setPassword("")
      setServerPassword("")
      await onSignedIn()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <div className="grid min-h-svh lg:grid-cols-[minmax(0,32rem)_1fr]">
      <div className="flex flex-col px-5 py-6 sm:px-10">
        <Wordmark sub="Server" />
        <div className="my-auto grid w-full max-w-sm gap-8 py-12">
          <div>
            <h1 className="display text-[2.25rem] text-text">Server admin</h1>
            <p className="mt-2 text-sm text-dim">For YHDE staff: sign in with your YHDE account.</p>
          </div>
          {site?.signedIn && site.staff ? (
            <Button size="lg" disabled={busy} onClick={() => attempt(() => api("POST", "/login/site"))}>
              Continue as {site.name}
            </Button>
          ) : site?.signedIn ? (
            <p className="rounded-xl bg-card px-4 py-3 text-sm text-dim">
              You're signed in on the website as {site.email}, which has no admin access.
            </p>
          ) : null}
          <form className="grid gap-4" onSubmit={(e) => (e.preventDefault(), attempt(() => api("POST", "/login/account", { email, password })))}>
            <div className="grid gap-2">
              <Label htmlFor="email" className="label-dim">
                Email
              </Label>
              <Input id="email" className="h-10" type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="password" className="label-dim">
                Password
              </Label>
              <Input id="password" className="h-10" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
            </div>
            <Button type="submit" size="lg" variant={site?.staff ? "secondary" : "default"} disabled={busy || !email || !password}>
              {busy ? "Signing in…" : "Sign in"}
            </Button>
            <p className="text-xs text-dim">
              Signed up with GitHub or Google? Sign in on the <a className="font-semibold text-sky hover:underline" href="/app#/sign-in">website</a> first, then come back here.
            </p>
          </form>
          {error ? (
            <p role="alert" className="text-sm text-bad">
              {error}
            </p>
          ) : null}
          <details className="text-sm text-dim">
            <summary className="cursor-pointer hover:text-text">Emergency: the server password</summary>
            <form className="mt-3 grid gap-3" onSubmit={(e) => (e.preventDefault(), attempt(() => api("POST", "/login", { password: serverPassword })))}>
              <p className="text-xs">The ADMIN_PASSWORD on the server (see it with ./yhde key). Use it to give your account the Admin role the first time.</p>
              <Input className="h-10" type="password" autoComplete="off" aria-label="Server password" value={serverPassword} onChange={(e) => setServerPassword(e.target.value)} />
              <Button type="submit" variant="outline" disabled={busy || !serverPassword}>
                Sign in with the server password
              </Button>
            </form>
          </details>
        </div>
      </div>
      <div className="hidden p-4 lg:block">
        <div className="flex h-full items-end rounded-[2rem] bg-tint-blue p-12">
          <p className="display max-w-[12ch] text-[3.25rem] text-text">
            Your <span className="text-sky">server</span>, at a glance.
          </p>
        </div>
      </div>
    </div>
  )
}

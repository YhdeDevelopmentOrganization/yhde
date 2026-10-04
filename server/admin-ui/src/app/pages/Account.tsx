import { useCallback, useEffect, useState } from "react"
import { Download, Laptop, MonitorSmartphone, Puzzle } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { ago } from "@/admin/format"
import { Panel } from "@/world/parts"
import { PageHead, go, run } from "../App"
import { api, exportUrl, useAppState } from "../store"
import { auth, providerUrl, useMe, type Session } from "../auth"

// A readable name for a browser's user agent.
function deviceName(ua: string) {
  const browser = /Edg\//.test(ua) ? "Edge" : /Firefox\//.test(ua) ? "Firefox" : /Chrome\//.test(ua) ? "Chrome" : /Safari\//.test(ua) ? "Safari" : /Godot/i.test(ua) ? "Godot" : "Browser"
  const os = /Windows/.test(ua) ? "Windows" : /Mac OS X/.test(ua) ? "macOS" : /Android/.test(ua) ? "Android" : /iPhone|iPad/.test(ua) ? "iOS" : /Linux/.test(ua) ? "Linux" : ""
  return os ? `${browser} on ${os}` : browser
}

export function AccountPage() {
  const me = useMe()
  const s = useAppState()
  const [name, setName] = useState(me?.name ?? "")
  if (!me) return null

  return (
    <>
      <PageHead title="Account" sub={me.email} />
      <div className="grid max-w-3xl gap-5">
        {!me.emailVerified ? (
          <div className="flex flex-wrap items-center gap-3 rounded-2xl bg-tint-rust px-5 py-4 text-sm text-text">
            <span className="flex-1">
              <strong>Confirm your email.</strong> <span className="text-text/75">We sent a link to {me.email}.</span>
            </span>
            <Button size="sm" variant="secondary" onClick={() => run(() => auth.resend(), "Sent again")}>
              Send again
            </Button>
          </div>
        ) : null}

        <Panel title="You">
          <form
            className="flex flex-wrap items-end gap-3"
            onSubmit={(e) => {
              e.preventDefault()
              run(() => auth.rename(name), "Saved")
            }}
          >
            <div className="grid min-w-60 flex-1 gap-2">
              <Label htmlFor="ac-name" className="label-dim">
                Name
              </Label>
              <Input id="ac-name" className="h-10" value={name} maxLength={48} onChange={(e) => setName(e.target.value)} autoComplete="name" />
              <p className="text-xs text-dim">Shown next to your cursor in Godot.</p>
            </div>
            <Button type="submit" className="mb-6 h-10" disabled={!name.trim() || name === me.name}>
              Save
            </Button>
          </form>
          <p className="text-sm text-dim">
            Email: <span className="text-text">{me.email}</span> {me.emailVerified ? <span className="ml-1 rounded-full bg-tint-green px-2 py-0.5 text-xs font-semibold text-good">Confirmed</span> : null}
          </p>
        </Panel>

        <PasswordPanel hasPassword={me.hasPassword} />
        <ProvidersPanel />
        <SessionsPanel />

        <DataPanel email={me.email} ownsProjects={!!s.plan && s.plan.projects > 0} />

        <Panel title="Sign out">
          <div className="flex flex-wrap items-center gap-4">
            <p className="flex-1 text-sm text-dim">Sign out on this browser.</p>
            <Button
              variant="secondary"
              onClick={async () => {
                await auth.logout()
                go("/sign-in")
              }}
            >
              Sign out
            </Button>
          </div>
        </Panel>
      </div>
    </>
  )
}

// GDPR: see and take your data, or delete the account.
function DataPanel({ email, ownsProjects }: { email: string; ownsProjects: boolean }) {
  const [typed, setTyped] = useState("")
  const [busy, setBusy] = useState(false)
  return (
    <Panel title="Your data">
      <div className="grid gap-6">
        <div className="flex flex-wrap items-center gap-4">
          <p className="min-w-60 flex-1 text-sm text-dim">Download everything we store about your account, as a file.</p>
          <Button variant="secondary" asChild>
            <a href={exportUrl} download>
              <Download /> Download my data
            </a>
          </Button>
        </div>
        <form
          className="grid gap-3 border-t border-line pt-5"
          onSubmit={async (e) => {
            e.preventDefault()
            setBusy(true)
            const ok = await run(() => api.deleteAccount(typed))
            setBusy(false)
            if (ok) location.href = "/"
          }}
        >
          <p className="text-sm text-text">Delete your account</p>
          <p className="text-sm text-dim">
            {ownsProjects
              ? "You still own projects. Hand them to someone on each project's page, or archive and delete them first, so nothing is lost by accident."
              : "Your account, sign-ins and your place in other people's projects are deleted for good. This cannot be undone."}
          </p>
          <div className="flex flex-wrap items-end gap-3">
            <div className="grid min-w-60 flex-1 gap-2">
              <Label htmlFor="del-email" className="label-dim">
                Type your email to confirm
              </Label>
              <Input id="del-email" className="h-10" value={typed} placeholder={email} autoComplete="off" onChange={(e) => setTyped(e.target.value)} disabled={ownsProjects} />
            </div>
            <Button type="submit" variant="destructive" className="h-10" disabled={busy || ownsProjects || typed.trim().toLowerCase() !== email.toLowerCase()}>
              {busy ? "Deleting…" : "Delete account"}
            </Button>
          </div>
        </form>
      </div>
    </Panel>
  )
}

function PasswordPanel({ hasPassword }: { hasPassword: boolean }) {
  const [current, setCurrent] = useState("")
  const [next, setNext] = useState("")
  const [busy, setBusy] = useState(false)
  return (
    <Panel title="Password" meta={hasPassword ? undefined : "You sign in with GitHub or Google. Add a password to sign in with email too."}>
      <form
        className="grid gap-4 sm:grid-cols-2"
        onSubmit={async (e) => {
          e.preventDefault()
          setBusy(true)
          const ok = await run(() => auth.changePassword(current, next), hasPassword ? "Password changed. Other devices were signed out." : "Password added")
          setBusy(false)
          if (ok) {
            setCurrent("")
            setNext("")
          }
        }}
      >
        {hasPassword ? (
          <div className="grid gap-2">
            <Label htmlFor="pw-current" className="label-dim">
              Current password
            </Label>
            <Input id="pw-current" type="password" autoComplete="current-password" className="h-10" value={current} onChange={(e) => setCurrent(e.target.value)} required />
          </div>
        ) : null}
        <div className="grid gap-2">
          <Label htmlFor="pw-next" className="label-dim">
            New password
          </Label>
          <Input id="pw-next" type="password" autoComplete="new-password" className="h-10" value={next} onChange={(e) => setNext(e.target.value)} minLength={10} required />
        </div>
        <Button type="submit" className="w-fit" disabled={busy || next.length < 10}>
          {busy ? "Saving…" : hasPassword ? "Change password" : "Add password"}
        </Button>
      </form>
    </Panel>
  )
}

function ProvidersPanel() {
  const me = useMe()
  const [available, setAvailable] = useState<{ github: boolean; google: boolean } | null>(null)
  useEffect(() => {
    auth.providers().then(setAvailable)
  }, [])
  if (!me || !available || (!available.github && !available.google && me.providers.length === 0)) return null
  const rows = (["github", "google"] as const).filter((p) => available[p] || me.providers.some((x) => x.provider === p))
  return (
    <Panel title="Sign-in methods" bodyClassName="p-0">
      <ul>
        {rows.map((p) => {
          const linked = me.providers.find((x) => x.provider === p)
          return (
            <li key={p} className="flex items-center gap-4 border-b border-line px-5 py-3.5 last:border-b-0">
              <span className="w-20 font-semibold text-text">{p === "github" ? "GitHub" : "Google"}</span>
              <span className="min-w-0 flex-1 truncate text-sm text-dim">{linked ? `Connected${linked.email ? ` · ${linked.email}` : ""}` : "Not connected"}</span>
              {linked ? (
                <Button variant="ghost" size="sm" onClick={() => run(() => auth.unlink(p), "Disconnected")}>
                  Disconnect
                </Button>
              ) : (
                <Button variant="secondary" size="sm" asChild>
                  <a href={providerUrl(p, { link: true, returnTo: "/app#/account" })}>Connect</a>
                </Button>
              )}
            </li>
          )
        })}
      </ul>
    </Panel>
  )
}

function SessionsPanel() {
  const [list, setList] = useState<Session[] | null>(null)
  const load = useCallback(() => auth.sessions().then(setList, (e) => toast.error((e as Error).message)), [])
  useEffect(() => {
    load()
  }, [load])
  return (
    <Panel title="Where you're signed in" meta="End any you don't recognise" bodyClassName="p-0">
      {!list ? (
        <p className="px-5 py-4 text-sm text-dim">Loading…</p>
      ) : (
        <ul>
          {list.map((x) => {
            const Icon = x.kind === "editor" ? Puzzle : /Mobile|Android|iPhone/.test(x.device) ? MonitorSmartphone : Laptop
            return (
              <li key={x.id} className="flex items-center gap-4 border-b border-line px-5 py-3.5 last:border-b-0">
                <Icon className="size-5 shrink-0 text-dim" />
                <div className="min-w-0 flex-1">
                  <p className="truncate font-semibold text-text">
                    {x.kind === "editor" ? "Godot editor" : deviceName(x.device)}
                    {x.current ? <span className="ml-2 rounded-full bg-tint-blue px-2 py-0.5 text-xs font-semibold text-sky">This browser</span> : null}
                  </p>
                  <p className="text-xs text-dim">
                    Last active {ago(x.lastSeen)} · signed in {ago(x.created)}
                  </p>
                </div>
                {!x.current ? (
                  <Button variant="ghost" size="sm" onClick={() => run(() => auth.endSession(x.id), "Signed out there").then(load)}>
                    Sign out
                  </Button>
                ) : null}
              </li>
            )
          })}
        </ul>
      )}
    </Panel>
  )
}

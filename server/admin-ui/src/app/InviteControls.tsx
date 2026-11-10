import { useState } from "react"
import { Link2 } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { until } from "@/admin/format"
import { FAILED, call, run } from "./App"
import { api, projectSeats, useAppState, type Access, type Link, type Project } from "./store"

// The owner invites people into a project (teams.md §4, onboarding.md §3):
// an invite link that lets them edit or only watch, the project's code as a
// fallback, or an email invitation. Links are for one person and last 7
// days, as the ones made in Godot (ui/invite_dialog.gd).

export function linkState(l: Link) {
  if (l.revoked) return "Turned off"
  if (l.expires && new Date(l.expires).getTime() < Date.now()) return "Expired"
  if (l.maxUses && l.uses >= l.maxUses) return "Used up"
  return "Active"
}

async function copy(text: string, done: string) {
  try {
    await navigator.clipboard.writeText(text)
    toast.success(done)
    return true
  } catch {
    return false
  }
}

// The working links, each a row in the People list.
export function LinkRows({ p }: { p: Project }) {
  const active = p.links.filter((l) => linkState(l) === "Active")
  return (
    <>
      {active.map((l) => (
        <li key={l.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-line px-4 py-2.5 text-xs">
          <span className="grid size-8 place-items-center rounded-[0.6rem] bg-tint-blue text-sky">
            <Link2 className="size-3.5" aria-hidden="true" />
          </span>
          <span className="min-w-0 flex-1 truncate text-text">
            {l.access === "edit" ? "Invite link" : "Watch-only link"}
            {l.label && l.label !== "From Godot" ? ` · ${l.label}` : ""}
          </span>
          <span className="text-dim tabular-nums">
            {l.uses} of {l.maxUses ?? "∞"} used{l.expires ? ` · ${until(l.expires)} left` : ""}
          </span>
          <Button variant="ghost" size="xs" onClick={() => run(() => api.revokeLink(l.id), "Link turned off")}>
            Turn off
          </Button>
        </li>
      ))}
    </>
  )
}

export function InviteControls({ p }: { p: Project }) {
  const s = useAppState()
  const seats = projectSeats(p)
  const [access, setAccess] = useState<Access>("edit")
  const [url, setUrl] = useState<string | null>(null)
  const [said, setSaid] = useState("")
  const [email, setEmail] = useState("")
  const [busy, setBusy] = useState(false)
  // Until the add-on that reads tickets is out, people who edit join with the code.
  const byCode = access === "edit" && !s.editLinks
  const roomForEditors = seats.free > 0
  const roomForViewers = p.viewers < p.viewersLimit

  const makeLink = async () => {
    setBusy(true)
    setUrl(null)
    setSaid("")
    const r = await call(() => api.createLink(p.id, { label: "", hours: 168, maxUses: 1, access }))
    setBusy(false)
    if (r === FAILED) return
    setUrl(r)
    setSaid((await copy(r, "Link copied")) ? "Copied. Send it to one person. It works for 7 days." : "Send this link to one person. It works for 7 days.")
  }

  const code = async () => {
    const c = p.joinCode ?? (await call(() => api.makeJoinCode(p.id)))
    if (c === FAILED) return null
    return typeof c === "string" ? c : c.code
  }

  return (
    <div className="grid gap-3 px-4 py-4">
      <div role="radiogroup" aria-label="What they can do" className="flex flex-wrap gap-x-5 gap-y-1 text-sm text-text">
        {(
          [
            ["edit", "Can edit"],
            ["view", "Can only watch"],
          ] as const
        ).map(([v, label]) => (
          <label key={v} className="flex cursor-pointer items-center gap-2">
            <input
              type="radio"
              name={`access-${p.id}`}
              className="accent-sky"
              checked={access === v}
              onChange={() => {
                setAccess(v)
                setUrl(null)
                setSaid("")
              }}
            />
            {label}
          </label>
        ))}
      </div>

      {access === "edit" && !roomForEditors ? (
        <p className="text-xs text-text">
          {p.name} has room for {seats.limit} people besides you, and they're all in or invited. Remove someone or turn off a link to invite someone else.
        </p>
      ) : access === "view" && !roomForViewers ? (
        <p className="text-xs text-text">Up to {p.viewersLimit} people can watch, and links already hold all the places. Turn one off to make room.</p>
      ) : byCode ? (
        <>
          <Button
            className="w-fit"
            disabled={busy}
            onClick={async () => {
              setBusy(true)
              const c = await code()
              setBusy(false)
              if (c) setSaid((await copy(c, "Code copied")) ? `Copied: ${c}. They type it under "I was invited" on the YHDE website.` : `The code is ${c}. They type it under "I was invited" on the YHDE website.`)
            }}
          >
            Copy the code
          </Button>
          {said ? <p className="text-xs text-dim">{said}</p> : null}
        </>
      ) : (
        <>
          <Button className="w-fit" disabled={busy} onClick={makeLink}>
            {busy ? "Making the link…" : "Copy invite link"}
          </Button>
          {url ? <Input readOnly value={url} onFocus={(e) => e.target.select()} className="h-9 text-xs" aria-label="Invite link" /> : null}
          {said ? <p className="text-xs text-dim">{said}</p> : null}
        </>
      )}

      {access === "edit" && roomForEditors ? (
        <>
          {!byCode ? (
            <p className="text-xs text-dim">
              {p.joinCode ? (
                <>
                  Or tell them the code <span className="font-mono tracking-wider text-text">{p.joinCode}</span>.{" "}
                  <button type="button" className="cursor-pointer text-dim underline-offset-4 hover:text-sky hover:underline" onClick={() => run(() => api.joinCodeOff(p.id), "Code turned off")}>
                    Turn it off
                  </button>
                </>
              ) : (
                <button type="button" className="cursor-pointer text-dim underline-offset-4 hover:text-sky hover:underline" onClick={() => run(() => api.makeJoinCode(p.id))}>
                  Or give them a code
                </button>
              )}
            </p>
          ) : null}
          <form
            className="flex gap-2"
            onSubmit={async (e) => {
              e.preventDefault()
              setBusy(true)
              if (await run(() => api.invite(p.id, email), `Invitation sent to ${email.trim()}`)) setEmail("")
              setBusy(false)
            }}
          >
            <Input className="h-9" type="email" placeholder="Or their email" aria-label="Email address to invite" value={email} onChange={(e) => setEmail(e.target.value)} />
            <Button type="submit" variant="outline" className="h-9" disabled={busy || !email.trim()}>
              Send
            </Button>
          </form>
        </>
      ) : null}
    </div>
  )
}

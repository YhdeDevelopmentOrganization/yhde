import { useEffect, useState } from "react"
import { MailCheck, ShieldCheck, Users } from "lucide-react"
import { Button } from "@/components/ui/button"
import { ago } from "@/admin/format"
import { Wordmark } from "@/world/parts"
import { go, run, type Route } from "../App"
import { useMe } from "../auth"
import { api, type Invitation, type TokenInvitation } from "../store"

// An invitation from its email's link (#/invite/<token>). It shows who sent
// it and from which address, so the person can check it's really them, and
// works signed out: signing in or making an account comes back here.
const PENDING = "yhde.pendingInvite"

export function rememberInvite(token: string | null) {
  try {
    if (token) localStorage.setItem(PENDING, token)
    else localStorage.removeItem(PENDING)
  } catch {
    /* storage blocked: the person opens the email's link again */
  }
}

export function pendingInvite(): string | null {
  try {
    return localStorage.getItem(PENDING)
  } catch {
    return null
  }
}

export function InvitePage({ route }: { route: Route }) {
  const token = route.path[1] ?? ""
  const me = useMe()
  const [inv, setInv] = useState<TokenInvitation | null>(null)
  const [error, setError] = useState("")
  const [done, setDone] = useState<"" | "joined" | "declined">("")

  useEffect(() => {
    api.invitationByToken(token).then(setInv, (e: Error) => {
      setError(e.message)
      rememberInvite(null)
    })
  }, [token, me?.id])
  // Back here after signing in: the dashboard stops sending them here.
  useEffect(() => {
    if (me) rememberInvite(null)
  }, [me])


  return (
    <div className="flex min-h-svh flex-col items-center px-5 py-8">
      <div className="flex w-full max-w-lg items-center">
        <a href="/" aria-label="YHDE home">
          <Wordmark />
        </a>
      </div>
      <main id="main" className="my-auto w-full max-w-lg py-10">
        <div className="grid gap-6 rounded-[1.75rem] bg-card p-7 sm:p-9">
          {error ? (
            <>
              <h1 className="display text-[2.25rem] text-text">Invitation closed</h1>
              <p className="text-dim">{error}</p>
              <Button variant="outline" className="w-fit" onClick={() => go("/projects")}>
                Go to your dashboard
              </Button>
            </>
          ) : !inv ? (
            <p className="text-dim" aria-busy="true">
              Loading the invitation…
            </p>
          ) : done ? (
            <>
              <h1 className="display text-[2.25rem] text-text">{done === "joined" ? `You're in ${inv.project}` : "Invitation declined"}</h1>
              <p className="text-dim">
                {done === "joined"
                  ? `Open Godot, sign in from the YHDE panel and pick ${inv.project}.`
                  : `${inv.from} gets an email saying so, and the place goes back to their project.`}
              </p>
              <Button className="w-fit" onClick={() => go("/projects")}>
                {done === "joined" ? "See the projects" : "Go to your dashboard"}
              </Button>
            </>
          ) : (
            <>
              <div>
                <p className="text-dim">You're invited to join</p>
                <h1 className="display mt-1 text-[2.75rem] break-words text-sky">{inv.project}</h1>
              </div>
              <dl className="grid gap-3 rounded-2xl bg-card-2/60 p-4 text-sm">
                <div className="flex gap-3">
                  <ShieldCheck className="mt-0.5 size-4 shrink-0 text-good" aria-hidden="true" />
                  <div className="min-w-0">
                    <dt className="text-dim">Sent by</dt>
                    <dd className="text-text">
                      {inv.from} <span className="break-all text-dim">· {inv.fromEmail}</span>
                    </dd>
                  </div>
                </div>
                <div className="flex gap-3">
                  <MailCheck className="mt-0.5 size-4 shrink-0 text-sky" aria-hidden="true" />
                  <div className="min-w-0">
                    <dt className="text-dim">To</dt>
                    <dd className="break-all text-text">
                      {inv.to} <span className="text-dim">· {ago(inv.sent)}</span>
                    </dd>
                  </div>
                </div>
              </dl>
              <p className="text-[0.8125rem] leading-relaxed text-dim">
                YHDE shows the sender's account as it is on this site, so you can check it's someone you know. Not sure? Ask them, or decline.
              </p>

              {me === null ? (
                <div className="grid gap-3">
                  <p className="text-sm text-text">Sign in, or make a free account, to accept. You'll come back here.</p>
                  <div className="flex flex-wrap gap-2">
                    <Button
                      onClick={() => {
                        rememberInvite(token)
                        go("/sign-in")
                      }}
                    >
                      Sign in
                    </Button>
                    <Button
                      variant="outline"
                      onClick={() => {
                        rememberInvite(token)
                        go("/start")
                      }}
                    >
                      Make an account
                    </Button>
                    <Button variant="ghost" onClick={() => api.declineInvitationByToken(token).then(() => setDone("declined"), (e: Error) => setError(e.message))}>
                      Decline
                    </Button>
                  </div>
                </div>
              ) : me ? (
                <div className="grid gap-3">
                  <p className="text-sm text-dim">
                    You're signed in as <span className="text-text">{me.email}</span>.
                    {me.email.toLowerCase() !== inv.to.toLowerCase() ? " That's a different address from the one invited, which is fine: opening this link proves the invitation reached you." : ""}
                  </p>
                  <div className="flex flex-wrap gap-2">
                    <Button
                      onClick={async () => {
                        if (await run(() => api.acceptInvitationByToken(token), `You joined ${inv.project}`)) {
                          rememberInvite(null)
                          setDone("joined")
                        }
                      }}
                    >
                      <Users /> Join {inv.project}
                    </Button>
                    <Button
                      variant="ghost"
                      onClick={async () => {
                        if (await run(() => api.declineInvitationByToken(token))) {
                          rememberInvite(null)
                          setDone("declined")
                        }
                      }}
                    >
                      Decline
                    </Button>
                  </div>
                </div>
              ) : null}
            </>
          )}
        </div>
      </main>
    </div>
  )
}

// Invitations to the signed-in person's address, on the dashboard. Shown on
// every page; joining a project never takes anyone out of another.
export function InvitationList({ invitations }: { invitations: Invitation[] }) {
  return (
    <div className="grid gap-3">
      {invitations.map((i) => (
        <div key={i.id} className="flex flex-wrap items-center gap-4 rounded-2xl bg-tint-blue p-5">
          <span className="grid size-11 place-items-center rounded-xl bg-bg/40 text-sky">
            <Users className="size-5" aria-hidden="true" />
          </span>
          <div className="min-w-0 flex-1">
            <p className="font-semibold text-text">{i.project}</p>
            <p className="text-sm break-words text-text/75">
              {i.from} ({i.fromEmail}) invited you into their project · {ago(i.sent)}
            </p>
          </div>
          <Button variant="ghost" onClick={() => run(() => api.declineInvitation(i.id), "Invitation declined")}>
            Decline
          </Button>
          <Button
            onClick={async () => {
              if (await run(() => api.acceptInvitation(i.id), `You joined ${i.project}`)) go(`/projects/${i.projectId}`)
            }}
          >
            Join project
          </Button>
        </div>
      ))}
    </div>
  )
}

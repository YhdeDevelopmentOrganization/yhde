import { useState } from "react"
import { Crown, LogOut, Mail } from "lucide-react"
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
import { Button } from "@/components/ui/button"
import { ago, until } from "@/admin/format"
import { Panel, Peer } from "@/world/parts"
import { go, run } from "./App"
import { InviteControls, LinkRows } from "./InviteControls"
import { api, owns, projectSeats, useAppState, type Access, type Member, type Project } from "./store"

// Who is in a project (teams.md): people, open invitations and working
// invite links. Its owner invites people (InviteControls.tsx), sets who can
// edit or only view, removes people and can hand the project over. Members
// see who else is in it and can leave.
export function PeoplePanel({ p }: { p: Project }) {
  const s = useAppState()
  const owner = owns(p)
  const seats = projectSeats(p)
  const [confirm, setConfirm] = useState<null | { kind: "remove" | "transfer"; m: Member } | { kind: "leave" }>(null)

  return (
    <Panel title="People" meta={owner ? `${seats.used} of ${seats.limit} places taken` : undefined} bodyClassName="p-0">
      <ul>
        {p.members.map((m) => (
          <li key={m.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-line px-4 py-3">
            <Peer name={m.name} color={m.id === s.user.id ? "var(--accent)" : undefined} />
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm text-text">
                {m.name}
                {m.id === s.user.id ? <span className="text-dim"> (you)</span> : null}
              </p>
              <p className="truncate text-xs text-dim">
                {m.email}
                {m.role === "member" ? ` · ${m.lastSeen ? `seen ${ago(m.lastSeen)}` : `joined ${ago(m.joined)}`}` : ""}
              </p>
            </div>
            {m.role === "owner" ? (
              <span className="label-dim inline-flex items-center gap-1.5">
                <Crown className="size-3" aria-hidden="true" /> Owner
              </span>
            ) : owner ? (
              <>
                <select
                  aria-label={`What ${m.name} can do in ${p.name}`}
                  className="h-7 cursor-pointer rounded-lg bg-card-2 px-2 text-xs text-text"
                  value={m.access}
                  onChange={(e) => run(() => api.setAccess(p.id, m.id, e.target.value as Access), e.target.value === "view" ? `${m.name} can only view now` : `${m.name} can edit now`)}
                >
                  <option value="edit">Can edit</option>
                  <option value="view">Can view</option>
                </select>
                <Button variant="ghost" size="xs" onClick={() => setConfirm({ kind: "transfer", m })}>
                  Make owner
                </Button>
                <Button variant="ghost" size="xs" onClick={() => setConfirm({ kind: "remove", m })}>
                  Remove
                </Button>
              </>
            ) : (
              <span className="label-dim">{m.access === "view" ? "Can view" : "Can edit"}</span>
            )}
          </li>
        ))}
        {owner
          ? p.invites.map((i) => (
              <li key={i.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-line px-4 py-2.5 text-xs">
                <span className="grid size-8 place-items-center rounded-[0.6rem] bg-tint-blue text-sky">
                  <Mail className="size-3.5" aria-hidden="true" />
                </span>
                <span className="min-w-0 flex-1 truncate text-text">{i.email}</span>
                <span className={i.expired ? "text-bad" : "text-dim"}>{i.expired ? "Expired" : `Invited · ${until(i.expires)} left`}</span>
                <Button variant="ghost" size="xs" onClick={() => run(() => api.resendInvite(p.id, i.id), `Sent again to ${i.email}, good for 14 more days`)}>
                  Resend
                </Button>
                <Button variant="ghost" size="xs" onClick={() => run(() => api.cancelInvite(p.id, i.id), `Invitation to ${i.email} withdrawn`)}>
                  {i.expired ? "Remove" : "Withdraw"}
                </Button>
              </li>
            ))
          : null}
        {owner ? <LinkRows p={p} /> : null}
      </ul>

      {owner && !p.archived ? <InviteControls p={p} /> : null}
      {!owner ? (
        <div className="grid gap-3 px-4 py-4">
          <p className="text-[0.6875rem] leading-relaxed text-dim">{p.owner?.name ?? "The owner"} decides who is in {p.name}.</p>
          <Button variant="outline" size="sm" className="w-fit" onClick={() => setConfirm({ kind: "leave" })}>
            <LogOut /> Leave project
          </Button>
        </div>
      ) : null}

      <AlertDialog open={!!confirm} onOpenChange={(o) => !o && setConfirm(null)}>
        <AlertDialogContent>
          {confirm?.kind === "remove" ? (
            <>
              <AlertDialogHeader>
                <AlertDialogTitle>Remove {confirm.m.name} from {p.name}?</AlertDialogTitle>
                <AlertDialogDescription>
                  Their seat becomes free. Their past changes stay in the history, and Godot disconnects them from {p.name} right away. Their other
                  projects aren't touched.
                </AlertDialogDescription>
              </AlertDialogHeader>
              <AlertDialogFooter>
                <AlertDialogCancel>Cancel</AlertDialogCancel>
                <AlertDialogAction onClick={() => run(() => api.removeMember(p.id, confirm.m.id), `${confirm.m.name} removed`)}>Remove</AlertDialogAction>
              </AlertDialogFooter>
            </>
          ) : confirm?.kind === "transfer" ? (
            <>
              <AlertDialogHeader>
                <AlertDialogTitle>
                  Make {confirm.m.name} the owner of {p.name}?
                </AlertDialogTitle>
                <AlertDialogDescription>
                  {p.name} moves to their plan and counts toward their projects and storage. They decide who is in it from now on. You stay in it and
                  can still edit. They need their own beta access, and room for one more project.
                </AlertDialogDescription>
              </AlertDialogHeader>
              <AlertDialogFooter>
                <AlertDialogCancel>Cancel</AlertDialogCancel>
                <AlertDialogAction onClick={() => run(() => api.transferProject(p.id, confirm.m.id), `${confirm.m.name} owns ${p.name} now`)}>
                  Hand it over
                </AlertDialogAction>
              </AlertDialogFooter>
            </>
          ) : confirm?.kind === "leave" ? (
            <>
              <AlertDialogHeader>
                <AlertDialogTitle>Leave {p.name}?</AlertDialogTitle>
                <AlertDialogDescription>
                  It disappears from your projects and Godot, and {p.owner?.name ?? "its owner"} gets an email saying so. To come back, they invite you
                  again.
                </AlertDialogDescription>
              </AlertDialogHeader>
              <AlertDialogFooter>
                <AlertDialogCancel>Stay</AlertDialogCancel>
                <AlertDialogAction
                  onClick={async () => {
                    if (await run(() => api.leaveProject(p.id), `You left ${p.name}`)) go("/projects")
                  }}
                >
                  Leave
                </AlertDialogAction>
              </AlertDialogFooter>
            </>
          ) : null}
        </AlertDialogContent>
      </AlertDialog>
    </Panel>
  )
}

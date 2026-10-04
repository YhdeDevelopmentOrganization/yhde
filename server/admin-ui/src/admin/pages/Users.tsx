import { useCallback, useEffect, useState } from "react"
import { Copy, FlaskConical, MoreHorizontal, Search } from "lucide-react"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Label } from "@/components/ui/label"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Peer } from "@/world/parts"
import { api, type Me, type Overview } from "../api"
import { ago } from "../format"
import { Empty, Section } from "../parts"

export type UserRow = {
  id: string
  email: string
  name: string
  emailVerified: boolean
  hasPassword: boolean
  created: string
  disabled: boolean
  staffRole: "admin" | "support" | null
  providers: string[]
  teamId: string | null
  team: string | null
  teamRole: string | null
  lastSeen: string | null
  sessions: number
  isTest: boolean
  owned: number // projects they own
  inProjects: number // other people's projects they're in
}

// Runs an admin action and says how it went.
export async function act(fn: () => Promise<unknown>, done: string): Promise<boolean> {
  try {
    await fn()
    toast.success(done)
    return true
  } catch (e) {
    toast.error((e as Error).message)
    return false
  }
}

export function UsersPage({ me }: { me: Me }) {
  const [q, setQ] = useState("")
  const [rows, setRows] = useState<UserRow[] | null>(null)
  const [deleting, setDeleting] = useState<UserRow | null>(null)
  const [testing, setTesting] = useState(false)
  const admin = me.role === "admin"

  const load = useCallback(async (query: string) => {
    try {
      setRows(await api<UserRow[]>("GET", "/users?q=" + encodeURIComponent(query)))
    } catch (e) {
      toast.error((e as Error).message)
    }
  }, [])

  useEffect(() => {
    const t = setTimeout(() => load(q), 250)
    return () => clearTimeout(t)
  }, [q, load])

  const reload = () => load(q)

  return (
    <div className="grid gap-6">
      <Section
        title="Everyone"
        description={rows ? `${rows.length === 300 ? "The newest 300" : rows.length} ${rows.length === 1 ? "account" : "accounts"}` : "Loading…"}
        action={
          <div className="flex flex-wrap items-center gap-2">
            <div className="relative w-64 max-w-full">
              <Search className="absolute top-1/2 left-3 size-4 -translate-y-1/2 text-dim" aria-hidden="true" />
              <Input className="h-9 pl-9" placeholder="Name or email" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search accounts" />
            </div>
            {admin ? (
              <Button variant="outline" onClick={() => setTesting(true)}>
                <FlaskConical /> New test account
              </Button>
            ) : null}
          </div>
        }
      >
        {rows && rows.length === 0 ? (
          <Empty>No accounts match.</Empty>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Person</TableHead>
                <TableHead>Projects</TableHead>
                <TableHead>Signs in with</TableHead>
                <TableHead>Last seen</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="w-0" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {(rows ?? []).map((u) => (
                <TableRow key={u.id} className={u.disabled ? "opacity-60" : undefined}>
                  <TableCell>
                    <span className="flex items-center gap-2.5">
                      <Peer name={u.name || u.email} size="sm" />
                      <span className="min-w-0">
                        <span className="block truncate font-medium">
                          {u.name || "-"}
                          {u.isTest ? (
                            <Badge variant="outline" className="ml-2">
                              Test
                            </Badge>
                          ) : null}
                          {u.staffRole ? (
                            <Badge variant="secondary" className="ml-2 capitalize">
                              {u.staffRole}
                            </Badge>
                          ) : null}
                        </span>
                        <span className="block truncate text-xs text-muted-foreground">{u.email}</span>
                      </span>
                    </span>
                  </TableCell>
                  <TableCell className="text-muted-foreground">
                    {u.owned || u.inProjects ? [u.owned ? `owns ${u.owned}` : null, u.inProjects ? `in ${u.inProjects}` : null].filter(Boolean).join(" · ") : u.teamRole === "owner" ? "Can make projects" : "-"}
                  </TableCell>
                  <TableCell className="text-muted-foreground">
                    {[u.hasPassword ? "Email" : null, ...u.providers.map((p) => (p === "github" ? "GitHub" : "Google"))].filter(Boolean).join(", ") || "-"}
                  </TableCell>
                  <TableCell className="text-muted-foreground">{u.lastSeen ? ago(u.lastSeen) : "Never"}</TableCell>
                  <TableCell>
                    {u.disabled ? (
                      <Badge variant="outline">Disabled</Badge>
                    ) : !u.emailVerified ? (
                      <Badge variant="outline">Not confirmed</Badge>
                    ) : (
                      <span className="text-sm text-muted-foreground">Active</span>
                    )}
                  </TableCell>
                  <TableCell>
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${u.email}`}>
                          <MoreHorizontal />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        <DropdownMenuItem onSelect={() => act(() => api("POST", `/users/${u.id}/sign-out`), `${u.email} is signed out everywhere`).then(reload)}>
                          Sign out everywhere
                        </DropdownMenuItem>
                        {!u.emailVerified ? (
                          <DropdownMenuItem onSelect={() => act(() => api("POST", `/users/${u.id}/resend-confirmation`), "Confirmation email sent")}>
                            Send a new confirmation email
                          </DropdownMenuItem>
                        ) : null}
                        {admin ? (
                          <>
                            <DropdownMenuSeparator />
                            <DropdownMenuLabel>Admin page access</DropdownMenuLabel>
                            {(["admin", "support", null] as const).map((role) =>
                              role !== u.staffRole ? (
                                <DropdownMenuItem
                                  key={role ?? "none"}
                                  onSelect={() => act(() => api("POST", `/users/${u.id}/role`, { role }), role ? `${u.email} is now ${role}` : `${u.email} has no admin access now`).then(reload)}
                                >
                                  {role === "admin" ? "Make admin" : role === "support" ? "Make support" : "Remove access"}
                                </DropdownMenuItem>
                              ) : null,
                            )}
                            <DropdownMenuSeparator />
                            <DropdownMenuItem onSelect={() => act(() => api("POST", `/users/${u.id}/disable`, { disabled: !u.disabled }), u.disabled ? "Account enabled" : "Account disabled and signed out").then(reload)}>
                              {u.disabled ? "Enable account" : "Disable account"}
                            </DropdownMenuItem>
                            <DropdownMenuItem variant="destructive" onSelect={() => setDeleting(u)}>
                              Delete account…
                            </DropdownMenuItem>
                          </>
                        ) : null}
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Section>
      <DeleteUser user={deleting} onClose={() => setDeleting(null)} onDone={reload} />
      <TestAccountDialog open={testing} onClose={() => setTesting(false)} onDone={reload} />
      {admin && rows?.some((u) => u.isTest) ? (
        <Button
          variant="ghost"
          className="w-fit text-muted-foreground"
          onClick={() =>
            api<{ deleted: number }>("POST", "/test-accounts/delete-all").then(
              (r) => (toast.success(`${r.deleted} test ${r.deleted === 1 ? "account" : "accounts"} removed`), reload()),
              (e) => toast.error((e as Error).message),
            )
          }
        >
          Remove all test accounts
        </Button>
      ) : null}
    </div>
  )
}

function DeleteUser({ user, onClose, onDone }: { user: UserRow | null; onClose: () => void; onDone: () => void }) {
  const [typed, setTyped] = useState("")
  return (
    <AlertDialog open={!!user} onOpenChange={(o) => (setTyped(""), !o && onClose())}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete {user?.email}?</AlertDialogTitle>
          <AlertDialogDescription>
            Their account, sign-ins and place in other people's projects are deleted for good. If they own projects, delete or move those first.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <div className="grid gap-2">
          <Label htmlFor="del-user">Type their email to confirm</Label>
          <Input id="del-user" value={typed} onChange={(e) => setTyped(e.target.value)} placeholder={user?.email} autoComplete="off" />
        </div>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <Button
            variant="destructive"
            disabled={!user || typed.trim().toLowerCase() !== user.email}
            onClick={async () => {
              if (user && (await act(() => api("POST", `/users/${user.id}/delete`, { email: typed }), `${user.email} deleted`))) {
                onClose()
                onDone()
              }
            }}
          >
            Delete for good
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}

// A test account: made here, confirmed, on the reserved .test domain (no
// email goes there). The password is shown once.
function TestAccountDialog({ open, onClose, onDone }: { open: boolean; onClose: () => void; onDone: () => void }) {
  const [name, setName] = useState("Test person")
  const [project, setProject] = useState("none")
  const [projects, setProjects] = useState<{ id: string; name: string }[]>([])
  const [made, setMade] = useState<{ email: string; password: string; project: string | null } | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    if (open) {
      setMade(null)
      api<Overview>("GET", "/overview").then((o) => setProjects(o.projects.filter((p) => !p.archived)), () => setProjects([]))
    }
  }, [open])
  const copy = (text: string) => navigator.clipboard?.writeText(text).then(() => toast.success("Copied"), () => toast.error("Couldn't copy"))
  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{made ? "Test account ready" : "New test account"}</DialogTitle>
          <DialogDescription>
            {made
              ? "Copy the sign-in now: the password isn't shown again. Sign in with it on the website or in Godot."
              : "A confirmed account for testing, marked Test. It gets a made-up address where no email is sent."}
          </DialogDescription>
        </DialogHeader>
        {made ? (
          <div className="grid gap-3">
            {[
              ["Email", made.email],
              ["Password", made.password],
            ].map(([label, value]) => (
              <div key={label} className="grid gap-1.5">
                <Label>{label}</Label>
                <div className="flex gap-2">
                  <Input readOnly value={value} className="font-mono" onFocus={(e) => e.target.select()} />
                  <Button variant="outline" size="icon" aria-label={`Copy ${label.toLowerCase()}`} onClick={() => copy(value)}>
                    <Copy />
                  </Button>
                </div>
              </div>
            ))}
            {made.project ? <p className="text-sm text-muted-foreground">Already in {made.project}.</p> : null}
          </div>
        ) : (
          <div className="grid gap-4">
            <div className="grid gap-1.5">
              <Label htmlFor="t-name">Name</Label>
              <Input id="t-name" value={name} maxLength={48} onChange={(e) => setName(e.target.value)} />
            </div>
            <div className="grid gap-1.5">
              <Label>Put in a project</Label>
              <Select value={project} onValueChange={setProject}>
                <SelectTrigger className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="none">None (invite them later)</SelectItem>
                  {projects.map((t) => (
                    <SelectItem key={t.id} value={t.id}>
                      {t.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>
        )}
        <DialogFooter>
          {made ? (
            <Button onClick={onClose}>Done</Button>
          ) : (
            <Button
              disabled={busy}
              onClick={async () => {
                setBusy(true)
                try {
                  setMade(await api("POST", "/test-accounts", { name, projectId: project === "none" ? null : project }))
                  onDone()
                } catch (e) {
                  toast.error((e as Error).message)
                } finally {
                  setBusy(false)
                }
              }}
            >
              {busy ? "Making…" : "Make test account"}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

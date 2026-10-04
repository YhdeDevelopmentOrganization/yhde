import { useCallback, useEffect, useRef, useState } from "react"
import { ChevronDown, Copy, MoreHorizontal, Plus, Upload } from "lucide-react"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible"
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
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Progress } from "@/components/ui/progress"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { Data } from "../App"
import { api, upload, type Link, type Project } from "../api"
import { ago, bytes, num, plural, until } from "../format"
import { Empty } from "../parts"

type Refresh = () => Promise<void>

async function run(action: () => Promise<unknown>, done?: string) {
  try {
    await action()
    if (done) toast.success(done)
    return true
  } catch (e) {
    toast.error((e as Error).message)
    return false
  }
}

function copy(text: string) {
  navigator.clipboard?.writeText(text).then(
    () => toast.success("Copied"),
    () => toast.error("Could not copy. Select the text and copy it."),
  )
}

type Owners = Record<string, { id: string; name: string }>
type TeamPick = { id: string; name: string; ownerEmail: string }

export function ProjectsPage({ data, refresh }: { data: Data; refresh: Refresh }) {
  const [creating, setCreating] = useState(false)
  // Which team owns each project (made on the website), and the teams to move to.
  const [owners, setOwners] = useState<Owners>({})
  const [teams, setTeams] = useState<TeamPick[]>([])
  const loadOwners = useCallback(async () => {
    try {
      const [o, t] = await Promise.all([api<Owners>("GET", "/project-teams"), api<TeamPick[]>("GET", "/teams")])
      setOwners(o)
      setTeams(t)
    } catch {
      /* shown without teams */
    }
  }, [])
  useEffect(() => {
    loadOwners()
  }, [loadOwners, data.overview.projects.length])
  const admin = data.me.role === "admin"
  const projects = [...data.overview.projects].sort(
    (a, b) =>
      Number(!!a.archived) - Number(!!b.archived) ||
      new Date(b.lastActivity ?? b.created).getTime() - new Date(a.lastActivity ?? a.created).getTime(),
  )
  return (
    <div className="grid gap-4">
      <div className="flex items-center justify-between">
        <h2 className="text-[1.75rem] leading-tight font-bold text-text">Projects</h2>
        <Button onClick={() => setCreating(true)}>
          <Plus /> New project
        </Button>
      </div>
      {projects.length ? (
        projects.map((p) => (
          <ProjectCard key={p.id} project={p} refresh={refresh} owner={owners[p.id]} teams={teams} admin={admin} onMoved={loadOwners} />
        ))
      ) : (
        <Empty>No projects yet.</Empty>
      )}
      <NewProjectDialog open={creating} onOpenChange={setCreating} refresh={refresh} />
    </div>
  )
}

function linkState(l: Link): "active" | "expired" | "used up" | "turned off" {
  if (l.revoked) return "turned off"
  if (l.expires && new Date(l.expires).getTime() < Date.now()) return "expired"
  if (l.maxUses && l.uses >= l.maxUses) return "used up"
  return "active"
}

function ProjectCard({
  project: p,
  refresh,
  owner,
  teams,
  admin,
  onMoved,
}: {
  project: Project
  refresh: Refresh
  owner?: { id: string; name: string }
  teams: TeamPick[]
  admin: boolean
  onMoved: () => void
}) {
  const [dialog, setDialog] = useState<null | "invite" | "rename" | "archive" | "delete" | "code" | "move">(null)
  const active = p.links.filter((l) => linkState(l) === "active")
  const inactive = p.links.filter((l) => linkState(l) !== "active")
  const codes = [...p.invites].reverse()
  const close = () => setDialog(null)

  return (
    <Card className={p.archived ? "opacity-70" : undefined}>
      <CardHeader className="flex flex-row items-center gap-3">
        <CardTitle className="text-base font-semibold tracking-normal text-text normal-case">{p.name}</CardTitle>
        {p.online ? <Badge variant="secondary">{p.online} online</Badge> : null}
        {p.archived ? <Badge variant="outline">Archived</Badge> : null}
        <span className="text-xs text-muted-foreground">{owner ? `Team: ${owner.name}` : "No team (server's own)"}</span>
        <div className="ml-auto flex items-center gap-2">
          {!p.archived ? (
            <Button size="sm" variant="outline" onClick={() => setDialog("invite")}>
              Download link
            </Button>
          ) : null}
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" aria-label="More">
                <MoreHorizontal />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem onSelect={() => setDialog("rename")}>Rename</DropdownMenuItem>
              {admin ? <DropdownMenuItem onSelect={() => setDialog("move")}>Move to a team…</DropdownMenuItem> : null}
              {!p.archived ? <DropdownMenuItem onSelect={() => setDialog("code")}>New download code</DropdownMenuItem> : null}
              <DropdownMenuSeparator />
              {p.archived ? (
                <>
                  <DropdownMenuItem onSelect={() => run(() => api("POST", `/projects/${p.id}`, { archived: false }), "Restored").then(refresh)}>
                    Restore
                  </DropdownMenuItem>
                  <DropdownMenuItem variant="destructive" onSelect={() => setDialog("delete")}>
                    Delete
                  </DropdownMenuItem>
                </>
              ) : (
                <DropdownMenuItem onSelect={() => setDialog("archive")}>Archive</DropdownMenuItem>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </CardHeader>
      <CardContent className="grid gap-4">
        <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-sm sm:grid-cols-4">
          <Fact label="Files" value={`${num(p.files)} · ${bytes(p.fileBytes)}`} />
          <Fact label="Changes" value={num(p.operations)} />
          <Fact label="Last change" value={ago(p.lastActivity)} />
          <Fact label="Created" value={ago(p.created)} />
        </dl>

        {!p.archived ? <JoinCode project={p} owned={!!owner} refresh={refresh} /> : null}

        {!p.archived && (active.length > 0 || inactive.length > 0) ? (
          <div className="grid gap-2">
            <h3 className="text-sm font-medium">Download links</h3>
            {active.length ? <LinksTable links={active} refresh={refresh} /> : <p className="text-sm text-muted-foreground">No active links.</p>}
            {inactive.length ? (
              <Collapsible>
                <CollapsibleTrigger asChild>
                  <Button variant="ghost" size="sm" className="w-fit px-2 text-muted-foreground">
                    Inactive links ({inactive.length}) <ChevronDown />
                  </Button>
                </CollapsibleTrigger>
                <CollapsibleContent>
                  <LinksTable links={inactive} refresh={refresh} />
                </CollapsibleContent>
              </Collapsible>
            ) : null}
          </div>
        ) : null}

        {codes.length ? (
          <Collapsible>
            <CollapsibleTrigger asChild>
              <Button variant="ghost" size="sm" className="w-fit px-2 text-muted-foreground">
                Invite codes ({codes.filter((c) => !c.revoked).length} active) <ChevronDown />
              </Button>
            </CollapsibleTrigger>
            <CollapsibleContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>For</TableHead>
                    <TableHead>From</TableHead>
                    <TableHead>Created</TableHead>
                    <TableHead>Status</TableHead>
                    <TableHead className="w-0" />
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {codes.map((c) => (
                    <TableRow key={c.id}>
                      <TableCell className="font-medium">{c.label || "-"}</TableCell>
                      <TableCell className="text-muted-foreground">{c.fromLink ? "Download link" : "Made by hand"}</TableCell>
                      <TableCell className="text-muted-foreground">{ago(c.created)}</TableCell>
                      <TableCell>{c.revoked ? <Badge variant="outline">Revoked</Badge> : <Badge variant="secondary">Active</Badge>}</TableCell>
                      <TableCell>
                        {!c.revoked ? (
                          <Confirm
                            title="Revoke this invite code?"
                            description="Whoever uses it can no longer connect."
                            action="Revoke"
                            onConfirm={() => run(() => api("POST", `/invites/${c.id}/revoke`), "Revoked").then(refresh)}
                          >
                            <Button variant="ghost" size="sm">Revoke</Button>
                          </Confirm>
                        ) : (
                          <Confirm
                            title="Delete this invite code?"
                            description="It is removed from the list for good."
                            action="Delete"
                            onConfirm={() => run(() => api("POST", `/invites/${c.id}/delete`), "Code deleted").then(refresh)}
                          >
                            <Button variant="ghost" size="sm">Delete</Button>
                          </Confirm>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CollapsibleContent>
          </Collapsible>
        ) : null}
      </CardContent>

      <InviteDialog project={p} open={dialog === "invite"} onOpenChange={(o) => !o && close()} refresh={refresh} />
      <MoveDialog project={p} owner={owner} teams={teams} open={dialog === "move"} onClose={close} onMoved={onMoved} />
      <CodeDialog project={p} open={dialog === "code"} onOpenChange={(o) => !o && close()} refresh={refresh} />
      <RenameDialog project={p} open={dialog === "rename"} onOpenChange={(o) => !o && close()} refresh={refresh} />
      <AlertDialog open={dialog === "archive"} onOpenChange={(o) => !o && close()}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Archive {p.name}?</AlertDialogTitle>
            <AlertDialogDescription>Nobody can connect or download it until you restore it. Nothing is deleted.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={() => run(() => api("POST", `/projects/${p.id}`, { archived: true }), "Archived").then(refresh)}>
              Archive
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <DeleteDialog project={p} open={dialog === "delete"} onOpenChange={(o) => !o && close()} refresh={refresh} />
    </Card>
  )
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="font-medium tabular-nums">{value}</dd>
    </div>
  )
}

// The project's join code: anyone signed in who enters it on their Projects
// page joins as a developer (not a download: that is Download link).
function JoinCode({ project: p, owned, refresh }: { project: Project; owned: boolean; refresh: Refresh }) {
  const code = p.joinCode
  return (
    <div className="grid gap-1.5 rounded-lg border px-3 py-2.5">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-medium">Join code</h3>
        {code ? <code className="rounded bg-muted px-2 py-0.5 font-mono text-sm tracking-wider">{code}</code> : <span className="text-sm text-muted-foreground">Off</span>}
        <span className="flex-1" />
        {code ? (
          <>
            <Button size="sm" variant="ghost" onClick={() => run(() => navigator.clipboard.writeText(code), "Join code copied")}>
              Copy
            </Button>
            <Button size="sm" variant="ghost" onClick={() => run(() => api("POST", `/projects/${p.id}/join-code`), "New join code made").then(refresh)}>
              New code
            </Button>
            <Button size="sm" variant="ghost" onClick={() => run(() => api("POST", `/projects/${p.id}/join-code/off`), "Join code turned off").then(refresh)}>
              Turn off
            </Button>
          </>
        ) : (
          <Button size="sm" disabled={!owned} onClick={() => run(() => api("POST", `/projects/${p.id}/join-code`), "Join code made").then(refresh)}>
            Make join code
          </Button>
        )}
      </div>
      <p className="text-xs text-muted-foreground">
        {owned
          ? "Adds people to the project: they sign in on the website and enter it under Join a project. They edit, while the project has room."
          : "Needs an owner to take people in. Move it to a team first."}
      </p>
    </div>
  )
}

function LinksTable({ links, refresh }: { links: Link[]; refresh: Refresh }) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>For</TableHead>
          <TableHead>Downloads</TableHead>
          <TableHead>Expires</TableHead>
          <TableHead>Status</TableHead>
          <TableHead className="w-0" />
        </TableRow>
      </TableHeader>
      <TableBody>
        {[...links].reverse().map((l) => {
          const state = linkState(l)
          return (
            <TableRow key={l.id}>
              <TableCell className="font-medium">{l.label || "-"}</TableCell>
              <TableCell className="tabular-nums">
                {l.uses}
                {l.maxUses ? ` / ${l.maxUses}` : ""}
              </TableCell>
              <TableCell className="text-muted-foreground">{l.expires ? (state === "active" ? `in ${until(l.expires)}` : ago(l.expires)) : "Never"}</TableCell>
              <TableCell>{state === "active" ? <Badge variant="secondary">Active</Badge> : <Badge variant="outline">{state[0].toUpperCase() + state.slice(1)}</Badge>}</TableCell>
              <TableCell>
                {state === "active" ? (
                  <Confirm
                    title="Turn off this link?"
                    description="Nobody can download with it any more. People who already joined keep working."
                    action="Turn off"
                    onConfirm={() => run(() => api("POST", `/links/${l.id}/revoke`), "Link turned off").then(refresh)}
                  >
                    <Button variant="ghost" size="sm">Turn off</Button>
                  </Confirm>
                ) : (
                  <Confirm
                    title="Delete this link?"
                    description="It is removed from the list for good. People who already joined keep working."
                    action="Delete"
                    onConfirm={() => run(() => api("POST", `/links/${l.id}/delete`), "Link deleted").then(refresh)}
                  >
                    <Button variant="ghost" size="sm">Delete</Button>
                  </Confirm>
                )}
              </TableCell>
            </TableRow>
          )
        })}
      </TableBody>
    </Table>
  )
}

function Confirm({ title, description, action, onConfirm, children }: { title: string; description: string; action: string; onConfirm: () => void; children: React.ReactNode }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <span onClick={() => setOpen(true)}>{children}</span>
      <AlertDialog open={open} onOpenChange={setOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{title}</AlertDialogTitle>
            <AlertDialogDescription>{description}</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={onConfirm}>{action}</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}

type DialogProps = { project: Project; open: boolean; onOpenChange: (open: boolean) => void; refresh: Refresh }

function ShownOnce({ value }: { value: string }) {
  return (
    <div className="grid gap-2">
      <div className="flex gap-2">
        <Input readOnly value={value} onFocus={(e) => e.target.select()} className="font-mono text-sm" />
        <Button variant="outline" size="icon" onClick={() => copy(value)} aria-label="Copy">
          <Copy />
        </Button>
      </div>
      <p className="text-xs text-muted-foreground">Copy it now. It is not shown again.</p>
    </div>
  )
}

function InviteDialog({ project: p, open, onOpenChange, refresh }: DialogProps) {
  const [label, setLabel] = useState("")
  const [hours, setHours] = useState("24")
  const [uses, setUses] = useState("0")
  const [url, setUrl] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const reset = (o: boolean) => {
    if (!o) {
      setUrl(null)
      setLabel("")
      setHours("24")
      setUses("0")
    }
    onOpenChange(o)
  }
  return (
    <Dialog open={open} onOpenChange={reset}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Invite to {p.name}</DialogTitle>
          <DialogDescription>A download link for a ready project.</DialogDescription>
        </DialogHeader>
        {url ? (
          <ShownOnce value={url} />
        ) : (
          <form
            id="invite-form"
            className="grid gap-4"
            onSubmit={async (e) => {
              e.preventDefault()
              setBusy(true)
              await run(async () => {
                const res = await api<{ url: string }>("POST", `/projects/${p.id}/links`, {
                  label: label.trim(),
                  hours: Number(hours) || null,
                  maxUses: Number(uses) || null,
                })
                setUrl(res.url)
                await refresh()
              })
              setBusy(false)
            }}
          >
            <div className="grid gap-2">
              <Label htmlFor="invite-label">For</Label>
              <Input id="invite-label" placeholder="Name (optional)" maxLength={80} value={label} onChange={(e) => setLabel(e.target.value)} />
            </div>
            <div className="grid grid-cols-2 gap-4">
              <div className="grid gap-2">
                <Label>Expires</Label>
                <Select value={hours} onValueChange={setHours}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="1">In 1 hour</SelectItem>
                    <SelectItem value="24">In 1 day</SelectItem>
                    <SelectItem value="168">In 7 days</SelectItem>
                    <SelectItem value="720">In 30 days</SelectItem>
                    <SelectItem value="0">Never</SelectItem>
                  </SelectContent>
                </Select>
              </div>
              <div className="grid gap-2">
                <Label>Downloads</Label>
                <Select value={uses} onValueChange={setUses}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="1">1</SelectItem>
                    <SelectItem value="5">5</SelectItem>
                    <SelectItem value="25">25</SelectItem>
                    <SelectItem value="0">Unlimited</SelectItem>
                  </SelectContent>
                </Select>
              </div>
            </div>
          </form>
        )}
        <DialogFooter>
          {url ? (
            <Button onClick={() => reset(false)}>Done</Button>
          ) : (
            <Button type="submit" form="invite-form" disabled={busy}>
              Create link
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function CodeDialog({ project: p, open, onOpenChange, refresh }: DialogProps) {
  const [label, setLabel] = useState("")
  const [code, setCode] = useState<string | null>(null)
  const reset = (o: boolean) => {
    if (!o) {
      setCode(null)
      setLabel("")
    }
    onOpenChange(o)
  }
  return (
    <Dialog open={open} onOpenChange={reset}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>New invite code</DialogTitle>
          <DialogDescription>For the join page (/join): it hands out the project with this code inside.</DialogDescription>
        </DialogHeader>
        {code ? (
          <ShownOnce value={code} />
        ) : (
          <form
            id="code-form"
            className="grid gap-2"
            onSubmit={async (e) => {
              e.preventDefault()
              await run(async () => {
                const res = await api<{ code: string }>("POST", `/projects/${p.id}/invites`, { label: label.trim() })
                setCode(res.code)
                await refresh()
              })
            }}
          >
            <Label htmlFor="code-label">For</Label>
            <Input id="code-label" placeholder="Name (optional)" maxLength={80} value={label} onChange={(e) => setLabel(e.target.value)} />
          </form>
        )}
        <DialogFooter>
          {code ? <Button onClick={() => reset(false)}>Done</Button> : <Button type="submit" form="code-form">Create code</Button>}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function RenameDialog({ project: p, open, onOpenChange, refresh }: DialogProps) {
  const [name, setName] = useState(p.name)
  return (
    <Dialog open={open} onOpenChange={(o) => (setName(p.name), onOpenChange(o))}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Rename project</DialogTitle>
        </DialogHeader>
        <form
          id="rename-form"
          onSubmit={async (e) => {
            e.preventDefault()
            if (await run(() => api("POST", `/projects/${p.id}`, { name: name.trim() }), "Renamed")) {
              onOpenChange(false)
              await refresh()
            }
          }}
        >
          <Input value={name} maxLength={80} onChange={(e) => setName(e.target.value)} autoFocus />
        </form>
        <DialogFooter>
          <Button type="submit" form="rename-form" disabled={!name.trim()}>
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function DeleteDialog({ project: p, open, onOpenChange, refresh }: DialogProps) {
  const [typed, setTyped] = useState("")
  return (
    <AlertDialog open={open} onOpenChange={(o) => (setTyped(""), onOpenChange(o))}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete {p.name}?</AlertDialogTitle>
          <AlertDialogDescription>
            Removes {plural(p.files, "file")}, their history ({plural(p.operations, "change")}), chat, comments, invite codes and links. This cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <div className="grid gap-2">
          <Label htmlFor="delete-name">Type the project name to confirm</Label>
          <Input id="delete-name" value={typed} onChange={(e) => setTyped(e.target.value)} placeholder={p.name} autoComplete="off" />
        </div>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <Button
            variant="destructive"
            disabled={typed.trim() !== p.name}
            onClick={async () => {
              if (await run(() => api("POST", `/projects/${p.id}/delete`, { name: typed.trim() }), `${p.name} deleted`)) {
                onOpenChange(false)
                await refresh()
              }
            }}
          >
            Delete
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}

function NewProjectDialog({ open, onOpenChange, refresh }: { open: boolean; onOpenChange: (o: boolean) => void; refresh: Refresh }) {
  const [name, setName] = useState("")
  const [file, setFile] = useState<File | null>(null)
  const [progress, setProgress] = useState<number | null>(null)
  const [result, setResult] = useState<string | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const busy = progress !== null && result === null
  const reset = (o: boolean) => {
    if (busy) return
    if (!o) {
      setName("")
      setFile(null)
      setProgress(null)
      setResult(null)
    }
    onOpenChange(o)
  }
  return (
    <Dialog open={open} onOpenChange={reset}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>New project</DialogTitle>
          <DialogDescription>Start empty, or from a zip of a Godot project.</DialogDescription>
        </DialogHeader>
        {result ? (
          <p className="text-sm">{result}</p>
        ) : (
          <form
            id="new-project-form"
            className="grid gap-4"
            onSubmit={async (e) => {
              e.preventDefault()
              if (!file) {
                if (await run(() => api("POST", "/projects", { name: name.trim() }), "Project created")) {
                  await refresh()
                  reset(false)
                }
                return
              }
              setProgress(0)
              try {
                const res = await upload<{ name: string; files: number; bytes: number; skippedCount: number; skipped: string[] }>(
                  "/projects/import?name=" + encodeURIComponent(name.trim()),
                  file,
                  setProgress,
                )
                setResult(
                  `${res.name}: ${plural(res.files, "file")}, ${bytes(res.bytes)}.` +
                    (res.skippedCount ? ` Left out: ${res.skipped.slice(0, 5).join(", ")}${res.skippedCount > 5 ? "…" : ""}` : ""),
                )
                await refresh()
              } catch (err) {
                setProgress(null)
                toast.error((err as Error).message)
              }
            }}
          >
            <div className="grid gap-2">
              <Label htmlFor="project-name">Name</Label>
              <Input
                id="project-name"
                placeholder={file ? "From the game (optional)" : "Space Game"}
                maxLength={80}
                value={name}
                onChange={(e) => setName(e.target.value)}
                disabled={busy}
              />
            </div>
            <div className="grid gap-2">
              <Label>Game zip</Label>
              <input ref={input} type="file" accept=".zip,application/zip" hidden onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
              <div className="flex items-center gap-3">
                <Button type="button" variant="outline" onClick={() => input.current?.click()} disabled={busy}>
                  <Upload /> {file ? "Change" : "Choose zip"}
                </Button>
                <span className="truncate text-sm text-muted-foreground">{file ? `${file.name} · ${bytes(file.size)}` : "Optional"}</span>
              </div>
            </div>
            {progress !== null ? (
              <div className="grid gap-2">
                <Progress value={progress * 100} />
                <p className="text-xs text-muted-foreground">{progress < 1 ? `Uploading… ${Math.round(progress * 100)}%` : "Adding files…"}</p>
              </div>
            ) : null}
          </form>
        )}
        <DialogFooter>
          {result ? (
            <Button onClick={() => reset(false)}>Done</Button>
          ) : (
            <Button type="submit" form="new-project-form" disabled={busy || (!file && !name.trim())}>
              Create
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// Moving a project to a team: its people see it in their dashboard and in
// Godot. Projects made on this page belong to no team until moved.
function MoveDialog({ project: p, owner, teams, open, onClose, onMoved }: { project: Project; owner?: { id: string; name: string }; teams: TeamPick[]; open: boolean; onClose: () => void; onMoved: () => void }) {
  const [team, setTeam] = useState(owner?.id ?? "none")
  return (
    <Dialog open={open} onOpenChange={(o) => (setTeam(owner?.id ?? "none"), !o && onClose())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Move {p.name} to a team</DialogTitle>
          <DialogDescription>The team's people then see it in their dashboard and in Godot's YHDE panel, and can connect with their accounts.</DialogDescription>
        </DialogHeader>
        <Select value={team} onValueChange={setTeam}>
          <SelectTrigger className="w-full">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="none">No team (the server's own)</SelectItem>
            {teams.map((t) => (
              <SelectItem key={t.id} value={t.id}>
                {t.name} · {t.ownerEmail}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button
            disabled={team === (owner?.id ?? "none")}
            onClick={async () => {
              try {
                await api("POST", `/projects/${p.id}/team`, { teamId: team === "none" ? null : team })
                toast.success(team === "none" ? `${p.name} belongs to no team now` : `${p.name} moved`)
                onMoved()
                onClose()
              } catch (e) {
                toast.error((e as Error).message)
              }
            }}
          >
            Move
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

import { useCallback, useEffect, useRef, useState } from "react"
import { OFFICIAL } from "@/world/site"
import { CalendarClock, Download, Eye, ImagePlus, Pencil, Plus, Trash2, X } from "lucide-react"
import { toast } from "sonner"
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
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { cn } from "@/lib/utils"
import { EmptyState, Panel, Segmented } from "@/world/parts"
import { PostImage, PostText, FORMAT_HELP } from "@/site/PostText"
import { Prose } from "@/site/parts"
import type { Post } from "@/site/api"
import { api, upload } from "../api"
import { ago, dateTime } from "../format"

type Settings = {
  announcement: { enabled: boolean; text: string; link: string; tone: "info" | "warning" }
  maintenance: { enabled: boolean; message: string }
}
type Signup = { email: string; source: string; created: string }

async function run<T>(fn: () => Promise<T>, done?: string): Promise<T | null> {
  try {
    const r = await fn()
    if (done) toast.success(done)
    return r
  } catch (e) {
    toast.error((e as Error).message)
    return null
  }
}

// Where a post stands: draft, scheduled or live.
function postState(p: Post): { label: string; tone: string } {
  if (p.status === "draft") return { label: "Draft", tone: "bg-card-2 text-dim" }
  if (p.publishAt && new Date(p.publishAt).getTime() > Date.now()) return { label: `Scheduled · ${dateTime(p.publishAt)}`, tone: "bg-tint-blue text-sky" }
  return { label: "Live", tone: "bg-tint-green text-good" }
}

export function SitePage() {
  return (
    <div className="grid gap-5">
      <Posts />
      <div className="grid gap-5 xl:grid-cols-2">
        <SiteSwitches />
        {OFFICIAL ? <EarlyAccessList /> : null}
      </div>
    </div>
  )
}

function Posts() {
  const [posts, setPosts] = useState<Post[] | null>(null)
  const [editing, setEditing] = useState<Post | "new" | null>(null)
  const load = useCallback(async () => setPosts(await run(() => api<Post[]>("GET", "/posts")) ?? []), [])
  useEffect(() => {
    load()
  }, [load])

  return (
    <Panel
      title="News and release notes"
      meta="Shown on the News and What's new pages"
      action={
        editing ? null : (
          <Button size="sm" onClick={() => setEditing("new")}>
            <Plus /> New post
          </Button>
        )
      }
      bodyClassName={editing ? "p-5" : "px-3 pt-2 pb-3"}
    >
      {editing ? (
        <PostEditor
          post={editing === "new" ? null : editing}
          onClose={async (changed) => {
            setEditing(null)
            if (changed) await load()
          }}
        />
      ) : !posts ? (
        <div className="grid gap-2 p-2">
          <Skeleton className="h-12 rounded-xl bg-card-2" />
          <Skeleton className="h-12 rounded-xl bg-card-2" />
        </div>
      ) : posts.length === 0 ? (
        <div className="p-2">
          <EmptyState title="No posts yet" action={<Button size="sm" onClick={() => setEditing("new")}><Plus /> Write the first one</Button>}>
            Write news for everyone, or release notes for a new version. Save as a draft, publish now, or schedule it.
          </EmptyState>
        </div>
      ) : (
        <ul className="grid">
          {posts.map((p) => {
            const st = postState(p)
            return (
              <li key={p.id} className="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-xl px-3 py-3 hover:bg-card-2/50">
                <span className="w-24 shrink-0 text-xs font-semibold text-dim">{p.kind === "release" ? `Release ${p.version}` : "News"}</span>
                <span className="min-w-0 flex-1 truncate font-semibold text-text">{p.title}</span>
                <span className={cn("rounded-full px-2.5 py-0.5 text-xs font-semibold", st.tone)}>{st.label}</span>
                <span className="w-24 text-right text-xs text-dim">{ago(p.updated)}</span>
                <Button variant="ghost" size="sm" onClick={() => setEditing(p)}>
                  <Pencil /> Edit
                </Button>
              </li>
            )
          })}
        </ul>
      )}
    </Panel>
  )
}

type Media = { hash: string; url: string; contentType: string; size: number; name: string }

// Uploads a picture for a post; returns its /media/ address.
async function uploadPicture(file: File, onProgress: (f: number) => void): Promise<string | null> {
  if (!/^image\/(png|jpeg|gif|webp)$/.test(file.type)) {
    toast.error("Use a PNG, JPEG, GIF or WebP picture.")
    return null
  }
  if (file.size > 8 * 1024 * 1024) {
    toast.error("Pictures can be up to 8 MB.")
    return null
  }
  const m = await run(() => upload<Media>(`/media?name=${encodeURIComponent(file.name)}`, file, onProgress, file.type))
  return m?.url ?? null
}

function PictureButton({ label, onPicked }: { label: string; onPicked: (url: string, name: string) => void }) {
  const input = useRef<HTMLInputElement>(null)
  const [progress, setProgress] = useState<number | null>(null)
  return (
    <>
      <input
        ref={input}
        type="file"
        accept="image/png,image/jpeg,image/gif,image/webp"
        hidden
        onChange={async (e) => {
          const file = e.target.files?.[0]
          e.target.value = ""
          if (!file) return
          setProgress(0)
          const url = await uploadPicture(file, setProgress)
          setProgress(null)
          if (url) onPicked(url, file.name.replace(/\.[a-z0-9]+$/i, ""))
        }}
      />
      <Button type="button" variant="secondary" size="sm" disabled={progress !== null} onClick={() => input.current?.click()}>
        <ImagePlus /> {progress !== null ? `Uploading ${Math.round(progress * 100)}%` : label}
      </Button>
    </>
  )
}

// A local date-time for <input type="datetime-local">.
const toLocalInput = (iso: string) => {
  const d = new Date(iso)
  const pad = (n: number) => String(n).padStart(2, "0")
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

function PostEditor({ post, onClose }: { post: Post | null; onClose: (changed: boolean) => void }) {
  const [kind, setKind] = useState<"news" | "release">(post?.kind ?? "news")
  const [title, setTitle] = useState(post?.title ?? "")
  const [version, setVersion] = useState(post?.version ?? "")
  const [summary, setSummary] = useState(post?.summary ?? "")
  const [body, setBody] = useState(post?.body ?? "")
  const [cover, setCover] = useState(post?.cover ?? "")
  const bodyRef = useRef<HTMLTextAreaElement>(null)
  // Puts a picture line where the cursor is in the text.
  const insertPicture = (url: string, name: string) => {
    const el = bodyRef.current
    const at = el ? el.selectionStart : body.length
    const line = `![${name}](${url})`
    const before = body.slice(0, at)
    const after = body.slice(at)
    const pad = (s: string, end: boolean) => (s === "" || (end ? s.endsWith("\n\n") : s.startsWith("\n\n")) ? "" : end ? (s.endsWith("\n") ? "\n" : "\n\n") : s.startsWith("\n") ? "\n" : "\n\n")
    setBody(before + pad(before, true) + line + pad(after, false) + after)
  }
  const initialWhen: "draft" | "now" | "later" = !post || post.status === "draft" ? "draft" : post.publishAt && new Date(post.publishAt) > new Date() ? "later" : "now"
  const [when, setWhen] = useState(initialWhen)
  const [at, setAt] = useState(post?.publishAt ? toLocalInput(post.publishAt) : toLocalInput(new Date(Date.now() + 86400000).toISOString()))
  const [preview, setPreview] = useState(false)
  const [busy, setBusy] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const save = async () => {
    if (when === "later" && new Date(at).getTime() <= Date.now()) return toast.error("Pick a time in the future, or publish now.")
    setBusy(true)
    const input = {
      kind,
      title,
      version: kind === "release" ? version : "",
      summary,
      body,
      cover,
      status: when === "draft" ? "draft" : "published",
      // Keep the original date when an already live post is edited.
      publishAt: when === "later" ? new Date(at).toISOString() : when === "now" ? (post?.status === "published" && post.publishAt && initialWhen === "now" ? post.publishAt : null) : null,
    }
    const ok = await run(
      () => api("POST", post ? `/posts/${post.id}` : "/posts", input),
      when === "draft" ? "Draft saved" : when === "later" ? "Scheduled" : "Published",
    )
    setBusy(false)
    if (ok) onClose(true)
  }

  return (
    <div className="grid gap-5">
      <div className="flex flex-wrap items-center gap-3">
        <h3 className="text-lg font-bold text-text">{post ? "Edit post" : "New post"}</h3>
        <Segmented
          label="Kind"
          value={kind}
          onChange={setKind}
          options={[
            { value: "news", label: "News" },
            { value: "release", label: "Release notes" },
          ]}
        />
        <Button variant="secondary" size="sm" className="ml-auto" onClick={() => setPreview(!preview)}>
          {preview ? <Pencil /> : <Eye />} {preview ? "Write" : "Preview"}
        </Button>
      </div>

      {preview ? (
        <div className="overflow-hidden rounded-2xl bg-bg p-6">
          {cover ? <PostImage src={cover} alt="" className="-mx-6 -mt-6 mb-6 [&_img]:aspect-[2/1] [&_img]:rounded-none [&_img]:object-cover" /> : null}
          {kind === "release" && version ? <p className="text-sm font-semibold text-sky">Beta {version}</p> : null}
          <p className="mt-1 text-[1.75rem] leading-tight font-bold text-text">{title || "Untitled"}</p>
          {summary ? <p className="mt-2 text-lg text-dim">{summary}</p> : null}
          <Prose className="mt-2">
            <PostText body={body} />
          </Prose>
        </div>
      ) : (
        <div className="grid gap-4">
          <div className={cn("grid gap-4", kind === "release" && "sm:grid-cols-[1fr_10rem]")}>
            <div className="grid gap-2">
              <Label htmlFor="post-title" className="label-dim">
                Title
              </Label>
              <Input id="post-title" className="h-10" maxLength={160} value={title} onChange={(e) => setTitle(e.target.value)} placeholder={kind === "release" ? "Follow mode for 3D, faster imports" : "New: roles and access per project"} />
            </div>
            {kind === "release" ? (
              <div className="grid gap-2">
                <Label htmlFor="post-version" className="label-dim">
                  Version
                </Label>
                <Input id="post-version" className="h-10" value={version} onChange={(e) => setVersion(e.target.value)} placeholder="0.4.0" />
              </div>
            ) : null}
          </div>
          <div className="grid gap-2">
            <span className="label-dim">Cover picture (optional)</span>
            {cover ? (
              <div className="flex items-center gap-3">
                <img src={cover} alt="" className="h-20 w-36 rounded-xl bg-card-2 object-cover" />
                <PictureButton label="Change" onPicked={(url) => setCover(url)} />
                <Button type="button" variant="ghost" size="sm" onClick={() => setCover("")}>
                  <X /> Remove
                </Button>
              </div>
            ) : (
              <PictureButton label="Add a cover picture" onPicked={(url) => setCover(url)} />
            )}
          </div>
          <div className="grid gap-2">
            <Label htmlFor="post-summary" className="label-dim">
              Short summary (optional)
            </Label>
            <Input id="post-summary" className="h-10" maxLength={280} value={summary} onChange={(e) => setSummary(e.target.value)} placeholder="One sentence people see first." />
          </div>
          <div className="grid gap-2">
            <div className="flex items-center gap-3">
              <Label htmlFor="post-body" className="label-dim">
                Text
              </Label>
              <span className="ml-auto">
                <PictureButton label="Insert picture" onPicked={insertPicture} />
              </span>
            </div>
            <textarea
              ref={bodyRef}
              id="post-body"
              value={body}
              onChange={(e) => setBody(e.target.value)}
              rows={12}
              className="min-h-48 w-full rounded-xl border border-input bg-bg px-3.5 py-3 text-sm leading-relaxed text-text outline-none focus-visible:border-sky"
              placeholder={kind === "release" ? "## New\n- Follow a teammate in the 3D view\n\n## Fixed\n- Imports of large zips no longer time out" : "Write your post here."}
            />
            <p className="text-xs text-dim">{FORMAT_HELP}</p>
          </div>
        </div>
      )}

      <div className="grid gap-3 rounded-2xl bg-card-2/60 p-4">
        <span className="label-dim">When</span>
        <div className="flex flex-wrap items-center gap-3">
          <Segmented
            label="When to publish"
            value={when}
            onChange={setWhen}
            options={[
              { value: "draft", label: "Keep as draft" },
              { value: "now", label: "Publish now" },
              { value: "later", label: "Schedule" },
            ]}
          />
          {when === "later" ? (
            <label className="inline-flex items-center gap-2 text-sm text-dim">
              <CalendarClock className="size-4 text-sky" />
              <input
                type="datetime-local"
                value={at}
                onChange={(e) => setAt(e.target.value)}
                className="h-9 rounded-xl border border-input bg-bg px-3 text-sm text-text [color-scheme:dark] outline-none focus-visible:border-sky"
              />
              <span>your time</span>
            </label>
          ) : null}
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <Button onClick={save} disabled={busy || !title.trim()}>
          {busy ? "Saving…" : when === "draft" ? "Save draft" : when === "later" ? "Schedule" : post?.status === "published" ? "Update" : "Publish"}
        </Button>
        <Button variant="ghost" onClick={() => onClose(false)}>
          Cancel
        </Button>
        {post ? (
          <Button variant="ghost" className="ml-auto text-bad hover:text-bad" onClick={() => setConfirmDelete(true)}>
            <Trash2 /> Delete
          </Button>
        ) : null}
      </div>

      <AlertDialog open={confirmDelete} onOpenChange={setConfirmDelete}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete “{post?.title}”?</AlertDialogTitle>
            <AlertDialogDescription>It disappears from the site. The audit log keeps a record that it existed.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={async () => {
                if (post && (await run(() => api("POST", `/posts/${post.id}/delete`), "Post deleted"))) onClose(true)
              }}
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}

function Switch({ on, onChange, label }: { on: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      aria-label={label}
      onClick={() => onChange(!on)}
      className="relative h-6 w-11 shrink-0 cursor-pointer rounded-full bg-card-2 transition-colors aria-checked:bg-sky"
    >
      <span className="absolute top-1 left-1 size-4 rounded-full bg-text transition-transform duration-150" style={{ transform: on ? "translateX(1.25rem)" : undefined, background: on ? "var(--accent-ink)" : undefined }} />
    </button>
  )
}

function SiteSwitches() {
  const [s, setS] = useState<Settings | null>(null)
  const [saved, setSaved] = useState<Settings | null>(null)
  const [confirmMaint, setConfirmMaint] = useState(false)
  useEffect(() => {
    run(() => api<Settings>("GET", "/site-settings")).then((r) => {
      if (r) {
        setS(r)
        setSaved(r)
      }
    })
  }, [])

  const save = async (next: Settings, done: string) => {
    const r = await run(() => api<Settings>("POST", "/site-settings", next), done)
    if (r) {
      setS(r)
      setSaved(r)
    }
  }

  if (!s || !saved)
    return (
      <Panel title="Announcement and maintenance">
        <Skeleton className="h-40 rounded-xl bg-card-2" />
      </Panel>
    )
  const a = s.announcement
  const dirtyA = JSON.stringify(a) !== JSON.stringify(saved.announcement)
  const dirtyM = s.maintenance.message !== saved.maintenance.message

  return (
    <Panel title="Announcement and maintenance">
      <div className="grid gap-4">
        <div className="flex items-center gap-3">
          <Switch on={a.enabled} label="Show the announcement bar" onChange={(v) => setS({ ...s, announcement: { ...a, enabled: v } })} />
          <span className="font-semibold text-text">Announcement bar</span>
          <span className="text-sm text-dim">on top of every public page</span>
        </div>
        <div className="grid gap-2">
          <Label htmlFor="ann-text" className="label-dim">
            Text
          </Label>
          <Input id="ann-text" className="h-10" maxLength={200} value={a.text} onChange={(e) => setS({ ...s, announcement: { ...a, text: e.target.value } })} placeholder="Beta 0.4 is out: follow mode in 3D" />
        </div>
        <div className="grid gap-4 sm:grid-cols-[1fr_auto]">
          <div className="grid gap-2">
            <Label htmlFor="ann-link" className="label-dim">
              Link (optional)
            </Label>
            <Input id="ann-link" className="h-10" value={a.link} onChange={(e) => setS({ ...s, announcement: { ...a, link: e.target.value } })} placeholder="/changelog" />
          </div>
          <div className="grid content-start gap-2">
            <span className="label-dim">Style</span>
            <Segmented
              label="Style"
              value={a.tone}
              onChange={(tone) => setS({ ...s, announcement: { ...a, tone } })}
              options={[
                { value: "info", label: "News" },
                { value: "warning", label: "Warning" },
              ]}
            />
          </div>
        </div>
        {a.text ? (
          <div className={cn("rounded-xl px-4 py-2.5 text-center text-sm font-semibold", a.tone === "warning" ? "bg-[#4a3413] text-[#ffd79a]" : "bg-sky text-sky-ink")}>{a.text}</div>
        ) : null}
        <Button className="w-fit" disabled={!dirtyA} onClick={() => save(s, a.enabled ? "Announcement is live" : "Saved")}>
          Save announcement
        </Button>
      </div>

      <div className="mt-6 grid gap-4 border-t border-line pt-6">
        <div className="flex items-center gap-3">
          <Switch
            on={s.maintenance.enabled}
            label="Maintenance mode"
            onChange={(v) => (v ? setConfirmMaint(true) : save({ ...s, maintenance: { ...s.maintenance, enabled: false } }, "The site is back"))}
          />
          <span className="font-semibold text-text">Maintenance mode</span>
          {s.maintenance.enabled ? <span className="rounded-full bg-tint-rust px-2.5 py-0.5 text-xs font-semibold text-bad">On: visitors see "Back soon"</span> : null}
        </div>
        <p className="text-sm text-dim">Visitors get a "back soon" page instead of the site. This admin page, invite downloads and editors keep working.</p>
        <div className="grid gap-2">
          <Label htmlFor="maint-msg" className="label-dim">
            Message (optional)
          </Label>
          <Input id="maint-msg" className="h-10" maxLength={400} value={s.maintenance.message} onChange={(e) => setS({ ...s, maintenance: { ...s.maintenance, message: e.target.value } })} placeholder="We're updating YHDE. Back around 22:30." />
        </div>
        {dirtyM ? (
          <Button variant="secondary" className="w-fit" onClick={() => save(s, "Message saved")}>
            Save message
          </Button>
        ) : null}
      </div>

      <AlertDialog open={confirmMaint} onOpenChange={setConfirmMaint}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Turn on maintenance mode?</AlertDialogTitle>
            <AlertDialogDescription>Everyone visiting the site sees a "back soon" page until you turn it off here.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={() => save({ ...s, maintenance: { ...s.maintenance, enabled: true } }, "Maintenance mode is on")}>Turn on</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Panel>
  )
}

function EarlyAccessList() {
  const [list, setList] = useState<Signup[] | null>(null)
  const [removing, setRemoving] = useState<string | null>(null)
  const load = useCallback(async () => setList(await run(() => api<Signup[]>("GET", "/early-access")) ?? []), [])
  useEffect(() => {
    load()
  }, [load])

  return (
    <Panel
      title="Early access list"
      meta={list ? `${list.length} ${list.length === 1 ? "person" : "people"}` : undefined}
      action={
        list?.length ? (
          <Button variant="secondary" size="sm" asChild>
            <a href="/admin/api/early-access.csv" download>
              <Download /> Export CSV
            </a>
          </Button>
        ) : null
      }
      bodyClassName="px-3 pb-3"
    >
      {!list ? (
        <Skeleton className="m-2 h-32 rounded-xl bg-card-2" />
      ) : list.length === 0 ? (
        <div className="p-2">
          <EmptyState title="Nobody yet">People who leave their email on the home page show up here.</EmptyState>
        </div>
      ) : (
        <div className="max-h-[26rem] overflow-y-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Email</TableHead>
                <TableHead>Signed up</TableHead>
                <TableHead className="w-0" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {list.map((e) => (
                <TableRow key={e.email}>
                  <TableCell className="font-medium text-text">{e.email}</TableCell>
                  <TableCell className="text-dim">{ago(e.created)}</TableCell>
                  <TableCell>
                    <Button variant="ghost" size="xs" onClick={() => setRemoving(e.email)}>
                      Remove
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      <AlertDialog open={!!removing} onOpenChange={(o) => !o && setRemoving(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove {removing}?</AlertDialogTitle>
            <AlertDialogDescription>Use this when someone asks to be taken off the list. The address is deleted for good.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={async () => {
                if (removing && (await run(() => api("POST", "/early-access/remove", { email: removing }), "Removed"))) await load()
              }}
            >
              Remove
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Panel>
  )
}

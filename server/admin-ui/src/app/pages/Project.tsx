import { useRef, useState } from "react";
import { ArrowLeft, ImageUp, MoreHorizontal, Trash2 } from "lucide-react";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ago, bytes, num } from "@/admin/format";
import { Bars, EmptyState, LiveTag, Panel, Peer, Readout } from "@/world/parts";
import { last30Days, shortDay } from "@/admin/format";
import { PageHead, go, run } from "../App";
import { ActivityPanel } from "../ActivityPanel";
import { PeoplePanel } from "../People";
import { ViewLinks } from "../ViewLinks";
import {
  api,
  owns,
  projectImage,
  useAppState,
  type Project,
} from "../store";

export function ProjectPage({ id }: { id: string }) {
  const s = useAppState();
  const p = s.projects.find((x) => x.id === id);
  const [dialog, setDialog] = useState<null | "rename" | "archive" | "delete">(
    null,
  );
  if (!p)
    return (
      <EmptyState
        title="That project is gone"
        action={
          <Button variant="outline" onClick={() => go("/projects")}>
            Back to projects
          </Button>
        }
      >
        It may have been deleted, or the link is wrong.
      </EmptyState>
    );

  const here = s.presence.filter((x) => x.projectId === p.id);
  const owner = owns(p);

  return (
    <>
      <PageHead
        back={
          <a
            href="#/projects"
            className="label-dim inline-flex w-fit items-center gap-1.5 hover:text-sky"
          >
            <ArrowLeft className="size-3" /> Projects
          </a>
        }
        title={p.name}
        sub={
          <span className="flex flex-wrap items-center gap-x-4 gap-y-1">
            {!owner && p.owner ? <span>{p.owner.name}'s project</span> : null}
            {p.myAccess === "view" ? <span className="text-sky">View only</span> : null}
            {p.archived ? (
              <span className="text-sky">Archived: nobody can connect</span>
            ) : here.length ? (
              <LiveTag>{here.length} in the scene</LiveTag>
            ) : (
              <span>Nobody connected</span>
            )}
            <span>
              {num(p.files)} files · {bytes(p.bytes)}
            </span>
            <span>Created {ago(p.created)}</span>
          </span>
        }
        action={
          owner ? (
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button
                  variant="outline"
                  size="icon"
                  aria-label="Project actions"
                >
                  <MoreHorizontal />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem onSelect={() => setDialog("rename")}>
                  Rename
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                {p.archived ? (
                  <>
                    <DropdownMenuItem
                      onSelect={() =>
                        run(() => api.archiveProject(p.id, false), "Restored")
                      }
                    >
                      Restore
                    </DropdownMenuItem>
                    <DropdownMenuItem
                      variant="destructive"
                      onSelect={() => setDialog("delete")}
                    >
                      Delete
                    </DropdownMenuItem>
                  </>
                ) : (
                  <DropdownMenuItem onSelect={() => setDialog("archive")}>
                    Archive
                  </DropdownMenuItem>
                )}
              </DropdownMenuContent>
            </DropdownMenu>
          ) : undefined
        }
      />

      <div className="grid gap-5 xl:grid-cols-12">
        <div className="grid content-start gap-5 xl:col-span-8">
          <Panel
            title="In the scene now"
            meta={here.length ? `${here.length} connected` : undefined}
            bodyClassName="p-0"
          >
            {here.length ? (
              <ul>
                {here.map((x) => {
                  return (
                    <li
                      key={x.sessionId}
                      className="flex items-center gap-4 border-b border-line px-4 py-3 last:border-b-0"
                    >
                      <Peer name={x.name} />
                      <div className="min-w-0 flex-1">
                        <p className="text-sm text-text">{x.name}</p>
                        <p className="truncate text-xs text-dim">
                          {x.scene || "Connected"}
                        </p>
                      </div>
                      <span className="text-xs text-dim">
                        {ago(x.since).replace(" ago", "")}
                      </span>
                    </li>
                  );
                })}
              </ul>
            ) : (
              <p className="px-4 py-6 text-xs text-dim">
                Nobody is in {p.name} right now. Open it in Godot (4.7 or newer)
                and press Connect in the YHDE panel.
              </p>
            )}
          </Panel>

          <Panel title="Changes" meta="Last 30 days">
            <Bars
              values={p.daily}
              label={`Changes per day in ${p.name}, last 30 days`}
              labels={[shortDay(last30Days()[0]), "", "Today"]}
            />
          </Panel>

          <ActivityPanel p={p} me={s.user.name} />
        </div>

        <div className="grid content-start gap-5 xl:col-span-4">
          <ImagePanel p={p} canChange={owner} />
          <PeoplePanel p={p} />
          {owner ? <ViewLinks p={p} /> : null}
          <Panel title="Files">
            <Readout label="Files" value={num(p.files)} />
            <Readout label="Size" value={bytes(p.bytes)} />
            <Readout label="Changes, all time" value={num(p.changes)} />
            <Readout label="Last change" value={ago(p.activity[0]?.at)} />
          </Panel>
        </div>
      </div>

      <RenameDialog
        p={p}
        open={dialog === "rename"}
        onClose={() => setDialog(null)}
      />
      <AlertDialog
        open={dialog === "archive"}
        onOpenChange={(o) => !o && setDialog(null)}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Archive {p.name}?</AlertDialogTitle>
            <AlertDialogDescription>
              Nobody can connect or download it until you restore it. Nothing is
              deleted, and its files still count toward your storage.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={() =>
                run(() => api.archiveProject(p.id, true), "Archived")
              }
            >
              Archive
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <DeleteDialog
        p={p}
        open={dialog === "delete"}
        onClose={() => setDialog(null)}
      />
    </>
  );
}

function RenameDialog({
  p,
  open,
  onClose,
}: {
  p: Project;
  open: boolean;
  onClose: () => void;
}) {
  const [name, setName] = useState(p.name);
  return (
    <Dialog
      open={open}
      onOpenChange={(o) => (setName(p.name), !o && onClose())}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Rename project</DialogTitle>
        </DialogHeader>
        <form
          id="rename"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await run(() => api.renameProject(p.id, name), "Renamed"))
              onClose();
          }}
        >
          <Input
            className="h-10"
            value={name}
            maxLength={80}
            onChange={(e) => setName(e.target.value)}
            autoFocus
          />
        </form>
        <DialogFooter>
          <Button type="submit" form="rename" disabled={!name.trim()}>
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function DeleteDialog({
  p,
  open,
  onClose,
}: {
  p: Project;
  open: boolean;
  onClose: () => void;
}) {
  const [typed, setTyped] = useState("");
  return (
    <AlertDialog
      open={open}
      onOpenChange={(o) => (setTyped(""), !o && onClose())}
    >
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete {p.name}?</AlertDialogTitle>
          <AlertDialogDescription>
            Removes {num(p.files)} files, their history ({num(p.changes)}{" "}
            changes), chat, comments and links. This cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <div className="grid gap-2">
          <Label htmlFor="del" className="label-dim">
            Type the project name to confirm
          </Label>
          <Input
            id="del"
            className="h-10"
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            placeholder={p.name}
            autoComplete="off"
          />
        </div>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <Button
            variant="destructive"
            disabled={typed.trim() !== p.name}
            onClick={async () => {
              if (
                await run(
                  () => api.deleteProject(p.id, typed),
                  `${p.name} deleted`,
                )
              ) {
                onClose();
                go("/projects");
              }
            }}
          >
            Delete forever
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

// The project's picture on the dashboard. Its owner can set one;
// without it the YHDE "No image yet" picture shows.
function ImagePanel({ p, canChange }: { p: Project; canChange: boolean }) {
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const pick = async (file: File | undefined) => {
    if (!file) return;
    setBusy(true);
    await run(() => api.setProjectImage(p.id, file), "Image updated");
    setBusy(false);
    if (input.current) input.current.value = "";
  };
  return (
    <Panel title="Image" bodyClassName="grid gap-3">
      <img
        src={projectImage(p)}
        alt={p.image ? `${p.name}` : "No image yet"}
        className="aspect-video w-full rounded-xl bg-card-2 object-cover"
      />
      {canChange ? (
        <div className="flex flex-wrap items-center gap-2">
          <input
            ref={input}
            type="file"
            accept="image/png,image/jpeg,image/webp"
            hidden
            onChange={(e) => pick(e.target.files?.[0])}
          />
          <Button
            variant="outline"
            size="sm"
            disabled={busy}
            onClick={() => input.current?.click()}
          >
            <ImageUp /> {busy ? "Uploading…" : p.image ? "Change image" : "Add an image"}
          </Button>
          {p.image ? (
            <Button
              variant="ghost"
              size="sm"
              disabled={busy}
              onClick={() => run(() => api.removeProjectImage(p.id), "Image removed")}
            >
              <Trash2 /> Remove
            </Button>
          ) : null}
          <span className="basis-full text-[0.6875rem] text-dim">
            PNG, JPEG or WebP, up to 2 MB. Wide pictures (16:9) fit best.
          </span>
        </div>
      ) : null}
    </Panel>
  );
}

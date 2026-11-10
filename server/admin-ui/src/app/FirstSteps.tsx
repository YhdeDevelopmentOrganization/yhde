import { useState } from "react"
import { Check, Download } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Panel } from "@/world/parts"
import { run } from "./App"
import { linkState } from "./InviteControls"
import { api, owns, useAppState, type Project } from "./store"

// The first steps for a project this account hasn't opened in Godot yet:
// download it ready for Godot, open it and press Join, and (its owner)
// invite people. Each shows whether it is done; the panel goes away when
// they all are.
export function FirstSteps({ p, onInvite }: { p: Project; onInvite: () => void }) {
  const s = useAppState()
  const [busy, setBusy] = useState(false)
  const owner = owns(p)
  const invited =
    p.members.length > 1 || p.invites.some((i) => !i.expired) || p.links.some((l) => linkState(l) === "Active")
  if (p.archived || (p.inGodot && (!owner || invited))) return null
  const godot = s.oldestGodot ?? "4.7"

  const steps = [
    {
      title: `Download ${p.name} for Godot`,
      text: "A folder with YHDE already in it.",
      done: p.inGodot,
      action: (
        <Button
          disabled={busy}
          onClick={async () => {
            setBusy(true)
            await run(() => api.downloadStarter(p.id, p.name))
            setBusy(false)
          }}
        >
          <Download /> {busy ? "Getting it ready…" : `Download ${p.name}`}
        </Button>
      ),
    },
    {
      title: "Open it in Godot and press Join",
      text: (
        <>
          In Godot {godot} or newer, press Import and pick the zip. Then press Join {p.name} in the YHDE panel.{" "}
          <a className="text-sky underline-offset-4 hover:underline" href="https://godotengine.org/download/" target="_blank" rel="noreferrer">
            Get Godot
          </a>
        </>
      ),
      done: p.inGodot,
    },
    ...(owner
      ? [
          {
            title: "Invite people",
            text: "Send them a link. They get the game the same way.",
            done: invited,
            action: (
              <Button variant={p.inGodot ? "default" : "outline"} onClick={onInvite}>
                Invite people
              </Button>
            ),
          },
        ]
      : []),
  ]

  return (
    <Panel title="Get started" bodyClassName="p-0">
      <ol>
        {steps.map((step, i) => (
          <li key={step.title} className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-line px-5 py-4 last:border-b-0">
            <span
              className={`grid size-8 shrink-0 place-items-center rounded-[0.6rem] text-sm ${step.done ? "bg-tint-blue text-sky" : "bg-card-2 text-text"}`}
              aria-label={step.done ? "Done" : undefined}
            >
              {step.done ? <Check className="size-4" aria-hidden="true" /> : i + 1}
            </span>
            <div className="min-w-0 flex-1">
              <p className={`text-sm ${step.done ? "text-dim" : "text-text"}`}>{step.title}</p>
              {!step.done ? <p className="text-xs text-dim">{step.text}</p> : null}
            </div>
            {!step.done && step.action ? step.action : null}
          </li>
        ))}
      </ol>
    </Panel>
  )
}

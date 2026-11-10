import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import { Download } from "lucide-react"
import "@/index.css"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { cn } from "@/lib/utils"
import { Wordmark } from "@/world/parts"

// The join page (onboarding.md). The server puts what to show into
// <script id="page-data" type="application/json">:
// - invite: an invite link that works (who invited, to what, edit or watch)
// - dead: a link that no longer works (and who to ask for a new one)
// - form: /join, for typing a code
// - missing: no add-on released on this server yet
type PageData = {
  mode: "invite" | "dead" | "form" | "missing"
  project?: string
  from?: string | null
  access?: "edit" | "view"
  downloadUrl?: string
  fileName?: string
  image?: string | null
  godot?: string
  error?: string | null
  code?: string | null
}

const data: PageData = JSON.parse(document.getElementById("page-data")?.textContent || '{"mode":"form"}')

const TEXT = {
  invitedYou: (from: string) => `${from} invited you to`,
  invited: "You're invited to",
  canEdit: "You can edit it with them.",
  canWatch: "You can watch it being made. You can't change anything.",
  download: (game: string) => `Download ${game}`,
  stepDownload: "Download it",
  stepOpen: "Open it in Godot",
  openBody: (file: string) => `In Godot, press Import, pick ${file}, then press Import again.`,
  noGodot: (v: string) => [`No Godot yet? Get Godot ${v} or newer from `, "godotengine.org", "."],
  stepJoin: "Press Join",
  joinEdit: "In the YHDE panel on the right, press Join. Then sign in or make an account.",
  joinWatch: "In the YHDE panel on the right, press Join.",
  haveCode: "Have a code?",
  code: "Code",
  next: "Next",
  deadTitle: "This link doesn't work any more",
  deadBody: (from?: string | null) =>
    `It ran out, was used up or was turned off. Ask ${from ?? "the person who sent it"} for a new link.`,
  formTitle: "Have a code?",
  formLead: "Type the code you were given.",
  notReady: "Not ready yet",
  notReadyBody: "This server has no YHDE add-on yet. Ask the person who invited you to try again soon.",
  footer: "YHDE · make your game together",
  home: "YHDE home",
}

const STEP_TINTS = ["bg-tint-green text-good", "bg-tint-blue text-sky", "bg-tint-rust text-[#ffb49c]"]

function Step({ n, title, children }: { n: number; title: string; children: React.ReactNode }) {
  return (
    <li className="flex gap-4 rounded-2xl bg-card-2/60 p-4">
      <span className={cn("grid size-8 shrink-0 place-items-center rounded-xl text-sm font-bold", STEP_TINTS[n - 1])}>{n}</span>
      <div className="min-w-0 flex-1">
        <p className="font-semibold text-text">{title}</p>
        <div className="mt-1 text-sm leading-relaxed text-dim">{children}</div>
      </div>
    </li>
  )
}

function CodeForm({ small }: { small: boolean }) {
  const t = TEXT
  return (
    <form method="post" action="/join" className={cn("grid gap-3", small && "mt-3")}>
      <Label htmlFor="code" className="label-dim">
        {t.code}
      </Label>
      <Input
        id="code"
        name="code"
        defaultValue={data.code ?? ""}
        className="h-12 text-base tracking-[0.06em] uppercase"
        autoComplete="off"
        spellCheck={false}
        maxLength={64}
        autoFocus={!small}
      />
      <Button type="submit" size={small ? "default" : "lg"} variant={small ? "secondary" : "default"}>
        {t.next}
      </Button>
    </form>
  )
}

function Invite() {
  const t = TEXT
  const game = data.project ?? ""
  const v = data.godot ?? "4.7"
  const godot = t.noGodot(v)
  return (
    <div className="grid gap-7">
      {data.image ? <img src={data.image} alt="" className="aspect-video w-full rounded-2xl object-cover" /> : null}
      <div>
        <p className="text-dim">{data.from ? t.invitedYou(data.from) : t.invited}</p>
        <h1 className="display mt-1 text-[2.75rem] text-sky">{game}</h1>
        <p className="mt-2 text-dim">{data.access === "edit" ? t.canEdit : t.canWatch}</p>
      </div>
      <ol className="grid gap-3">
        <Step n={1} title={t.stepDownload}>
          <Button size="lg" className="mt-2 w-full" asChild>
            <a href={data.downloadUrl}>
              <Download aria-hidden="true" /> {t.download(game)}
            </a>
          </Button>
        </Step>
        <Step n={2} title={t.stepOpen}>
          <p>{t.openBody(data.fileName ?? `${game}.zip`)}</p>
          <p className="mt-1">
            {godot[0]}
            <a className="font-semibold text-sky hover:underline" href="https://godotengine.org/download/" rel="noreferrer">
              {godot[1]}
            </a>
            {godot[2]}
          </p>
        </Step>
        <Step n={3} title={t.stepJoin}>
          <p>{data.access === "edit" ? t.joinEdit : t.joinWatch}</p>
        </Step>
      </ol>
      <details className="text-sm text-dim">
        <summary className="cursor-pointer hover:text-text">{t.haveCode}</summary>
        <CodeForm small />
      </details>
    </div>
  )
}

function Page() {
  const t = TEXT
  return (
    <div className="flex min-h-svh flex-col items-center px-5 py-8">
      <div className="flex w-full max-w-lg items-center">
        <a href="/" aria-label={t.home}>
          <Wordmark />
        </a>
      </div>
      <main id="main" className="my-auto w-full max-w-lg py-10">
        <div className="rounded-[1.75rem] bg-card p-7 sm:p-9">
          {data.mode === "invite" ? (
            <Invite />
          ) : data.mode === "dead" ? (
            <div className="grid gap-3">
              <h1 className="display text-[2.25rem] leading-tight text-text">{t.deadTitle}</h1>
              <p className="text-dim">{t.deadBody(data.from)}</p>
            </div>
          ) : data.mode === "missing" ? (
            <div className="grid gap-3">
              <h1 className="display text-[2.5rem] text-text">{t.notReady}</h1>
              <p className="text-dim">{t.notReadyBody}</p>
            </div>
          ) : (
            <div className="grid gap-7">
              <div>
                <h1 className="display text-[2.75rem] text-text">{t.formTitle}</h1>
                <p className="mt-2 text-dim">{t.formLead}</p>
              </div>
              {data.error ? (
                <div role="alert" className="rounded-2xl bg-tint-rust px-4 py-3">
                  <p className="font-semibold text-bad">{data.error}</p>
                </div>
              ) : null}
              <CodeForm small={false} />
            </div>
          )}
        </div>
      </main>
      <p className="text-sm text-dim">{t.footer}</p>
    </div>
  )
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Page />
  </StrictMode>,
)

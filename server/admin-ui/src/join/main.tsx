import { StrictMode, useEffect, useState } from "react"
import { createRoot } from "react-dom/client"
import { Download } from "lucide-react"
import "@/index.css"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { cn } from "@/lib/utils"
import { Wordmark } from "@/world/parts"

// The join page (onboarding.md). The server puts what to show into
// <script id="page-data" type="application/json">.
type PageData = {
  mode: "download" | "form" | "missing"
  project?: string
  downloadUrl?: string
  error?: string
}

const data: PageData = JSON.parse(document.getElementById("page-data")?.textContent || '{"mode":"form"}')

const TEXT = {
  steps: [
    { title: "Install Godot 4.7 or newer", body: ["From ", ", if you don't have it yet."] },
    { title: "Unzip and open it", body: ["In Godot: Import, then pick project.godot from the folder you unzipped."] },
    { title: "Join from the YHDE panel", body: ["Press Join with the invite in this project. The game's files arrive, and you're in the project with your team."] },
  ],
  invited: "You're invited to",
  theProject: "the project",
  started: "Your download has started.",
  starting: "Your download is starting…",
  ready: "A ready Godot project with YHDE and your invite already in it.",
  again: "Again",
  notReady: "Not ready yet",
  notReadyBody: "This server has no YHDE add-on released yet. Ask the person who invited you to try again soon.",
  joinA: "Join a ",
  joinB: "project",
  joinLead: "Enter the invite code you were given, or leave it empty to get just the add-on.",
  askNew: "Ask for a new link or code.",
  code: "Invite code",
  download: "Download",
  footer: "YHDE · make your game together",
  home: "YHDE home",
}

const STEP_TINTS = ["bg-tint-green text-good", "bg-tint-blue text-sky", "bg-tint-rust text-[#ffb49c]"]

function Steps() {
  const t = TEXT
  return (
    <ol className="grid gap-3">
      {t.steps.map((s, i) => (
        <li key={s.title} className="flex gap-4 rounded-2xl bg-card-2/60 p-4">
          <span className={cn("grid size-8 shrink-0 place-items-center rounded-xl text-sm font-bold", STEP_TINTS[i])}>{i + 1}</span>
          <div>
            <p className="font-semibold text-text">{s.title}</p>
            <p className="mt-0.5 text-sm leading-relaxed text-dim">
              {i === 0 ? (
                <>
                  {s.body[0]}
                  <a className="font-semibold text-sky hover:underline" href="https://godotengine.org/download/" rel="noreferrer">
                    godotengine.org
                  </a>
                  {s.body[1]}
                </>
              ) : (
                s.body[0]
              )}
            </p>
          </div>
        </li>
      ))}
    </ol>
  )
}

function Page() {
  const t = TEXT
  const [started, setStarted] = useState(false)
  useEffect(() => {
    // Starting the download from script keeps link previews in chat apps
    // (which do not run scripts) from using the link up.
    if (data.mode === "download" && data.downloadUrl) {
      const timer = setTimeout(() => {
        window.location.href = data.downloadUrl!
        setStarted(true)
      }, 300)
      return () => clearTimeout(timer)
    }
  }, [])

  return (
    <div className="flex min-h-svh flex-col items-center px-5 py-8">
      <div className="flex w-full max-w-lg items-center">
        <a href="/" aria-label={t.home}>
          <Wordmark />
        </a>
      </div>
      <main id="main" className="my-auto w-full max-w-lg py-10">
        <div className="rounded-[1.75rem] bg-card p-7 sm:p-9">
          {data.mode === "download" ? (
            <div className="grid gap-7">
              <div>
                <p className="text-dim">{t.invited}</p>
                <h1 className="display mt-1 text-[2.75rem] text-sky">{data.project ?? t.theProject}</h1>
              </div>
              <div className="flex flex-wrap items-center gap-4 rounded-2xl bg-tint-blue p-4" role="status">
                <div className="min-w-0 flex-1">
                  <p className="font-semibold text-text">{started ? t.started : t.starting}</p>
                  <p className="text-sm text-text/75">{t.ready}</p>
                </div>
                <Button variant="secondary" size="sm" asChild>
                  <a href={data.downloadUrl}>
                    <Download aria-hidden="true" /> {t.again}
                  </a>
                </Button>
              </div>
              <Steps />
            </div>
          ) : data.mode === "missing" ? (
            <div className="grid gap-3">
              <h1 className="display text-[2.5rem] text-text">{t.notReady}</h1>
              <p className="text-dim">{t.notReadyBody}</p>
            </div>
          ) : (
            <div className="grid gap-7">
              <div>
                <h1 className="display text-[2.75rem] text-text">
                  {t.joinA}
                  <span className="text-sky">{t.joinB}</span>
                </h1>
                <p className="mt-2 text-dim">{t.joinLead}</p>
              </div>
              {data.error ? (
                <div role="alert" className="rounded-2xl bg-tint-rust px-4 py-3">
                  <p className="font-semibold text-bad">{data.error}</p>
                  <p className="mt-0.5 text-sm text-text/75">{t.askNew}</p>
                </div>
              ) : null}
              <form method="post" action="/join" className="grid gap-3">
                <Label htmlFor="code" className="label-dim">
                  {t.code}
                </Label>
                <Input id="code" name="code" className="h-12 text-base tracking-[0.06em] uppercase" placeholder="YHDE-XXXX-XXXX-XXXX-XXXX" autoComplete="off" spellCheck={false} maxLength={64} autoFocus />
                <Button type="submit" size="lg">
                  <Download aria-hidden="true" /> {t.download}
                </Button>
              </form>
              <Steps />
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

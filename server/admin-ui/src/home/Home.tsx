import { Fragment, useState, type ComponentProps, type ReactNode } from "react"
import { ArrowRight, Check, LayoutDashboard, Minus, Plus, Puzzle, Server, X } from "lucide-react"
import { Button } from "@/components/ui/button"
import { cn } from "@/lib/utils"
import { Segmented } from "@/world/parts"
import { SiteFooter, SiteHeader, href } from "@/site/chrome"
import { BETA, BETA_INVITES, BETA_PROJECTS, VIEWERS_PER_PROJECT, EXTRA_STORAGE, PLANS, PLANS_OPEN, euro, nextPlan, yearly, type Plan } from "@/world/plans"
import { EarlyAccess } from "@/site/EarlyAccess"
import { useMe } from "@/app/auth"

// Numbers from our load test (docs/capacity.md): many people in ONE project.
// Production load test (docs/capacity.md §1): simulated editors in teams of 5.
const TEAM_TEST = [
  { people: 100, cursor: 6, edit: 12 },
  { people: 500, cursor: 13, edit: 24 },
]
const FRAME_MS = 16.7

const people = (n: number) => (n === 1 ? "1 person" : `${n} people`)

const TEXT = {
  h1a: "Make your game ",
  h1b: "together",
  lead: "YHDE is an add-on for the Godot editor. It puts your whole team in the same project at the same time, and everyone sees every change the moment it happens.",
  start: "Start your team",
  openProjects: "Open your projects",
  watch: "Watch the video",
  from: (price: string) =>
    PLANS_OPEN
      ? `From ${price} a month for the owner, VAT included. Teammates join free. For Godot 4.7 and newer, on Windows, macOS and Linux.`
      : "Free during the beta. For Godot 4.7 and newer, on Windows, macOS and Linux.",
  videoAlt: "YHDE in the Godot editor: teammates editing the same scene live.",
  videoNote: "Recorded in the Godot editor with YHDE.",
  stepsA: "Up and running in ",
  stepsB: "three steps",
  steps: [
    {
      title: "Make a project",
      body: PLANS_OPEN ? "Pick a plan and make a project, empty or from your game." : "Sign up and make a project, empty or from your game.",
    },
    { title: "Invite your people", body: "Invite teammates into the project by email. They join free, and can be in as many projects as they like." },
    { title: "Sign in from Godot", body: "Pick the project in the YHDE panel. Everyone edits the same scene, at the same time. No merge conflicts." },
  ],
  getA: "What you ",
  getB: "get",
  parts: [
    { title: "An add-on for Godot", body: "A YHDE panel inside the Godot editor: sign in, pick a project, see who's in it, follow them and watch the activity. You keep using Godot as usual." },
    { title: "Our servers", body: "We host your project and pass every change to your team in milliseconds. Backed up daily. Nothing for you to set up." },
    { title: "A dashboard for the owner", body: "Make projects, send invite links, see who's online and how much storage you use, and manage your team." },
  ],
  problemTitle: "No more “don't touch the level, I'm in it.”",
  problemBody: "Making a game as a team usually means taking turns, waiting, and fixing merge conflicts in scene files. With YHDE everyone works in the same live scene instead of separate copies.",
  copies: [
    { who: "Your copy", where: "Platform at x 576, y 400" },
    { who: "Maya's copy", where: "Platform at x 720, y 336" },
  ],
  copiesNote: "Two copies of the same level, two answers. Someone has to pick one by hand, and the other person's work is lost.",
  without: "Without YHDE",
  withoutPoints: ["Only one person can safely edit a level at a time. The rest wait.", "Or everyone edits their own copy, and the copies stop matching:"],
  with: "With YHDE",
  withPoints: [
    "Everyone opens the same level at the same time.",
    "When a teammate moves a platform, it moves on your screen too.",
    "There are no copies, so there is nothing to merge.",
  ],
  featA: "Godot, but ",
  featB: "multiplayer",
  featLead: "Your editor sends each change to the server, which puts them in order and sends them to everyone. That's why nothing gets lost.",
  features: [
    { title: "Every property syncs", body: "Create, delete, move, rename and reorder nodes, and every Inspector property. 2D, 3D and UI scenes, plus resources like materials." },
    { title: "See each other work", body: "Everyone's cursor, selection and name, in the 2D and 3D views." },
    { title: "Your undo is yours", body: "Ctrl+Z undoes your own last change, never a teammate's." },
    { title: "Reused scenes update everywhere", body: "Edit an enemy scene and every copy in every level updates for everyone." },
    { title: "Follow a teammate", body: "Click a name and your view jumps to what they are looking at." },
    { title: "Keeps working offline", body: "Connection drops? Keep editing. Your changes are sent when you are back, even after an editor crash." },
    { title: "Scripts, chat and comments", body: "Type in the same script together, talk in the project chat, and pin comments where you mean them." },
    { title: "Works alongside Git", body: "Keep Git for history and releases. Nothing to give up." },
  ],
  speedA: "Your whole team, ",
  speedB: "instantly",
  speedLead: "We load-tested our production server in Helsinki with hundreds of simulated editors. Changes still arrive faster than a single frame.",
  stats: [
    { value: "800", label: "simulated editors on one server, still smooth" },
    { value: "6 ms", label: "cursor delay with 100 online" },
    { value: "12 ms", label: "for an edit to be saved and delivered" },
  ],
  inProject: "online",
  cursors: "Cursor moves",
  edits: "Edits saved and delivered",
  frame: "One frame at 60 fps",
  speedNote:
    "95 of every 100 updates arrived at least this fast. Measured in our load test on one server, from Finland to our server in Helsinki, with simulated editors in teams of 5. Your speed depends on your connection and distance to the server.",
  plansA: "One owner pays. The team joins ",
  plansB: "free",
  plansLead: "Pick by how many people you need. Add seats one by one as your team grows.",
  period: "Billing period",
  monthly: "Monthly",
  yearlyLabel: "Yearly · 2 months free",
  most: "Recommended",
  perMonth: "month",
  perYear: "year",
  vatIncl: "VAT included",
  upTo: (n: number) => (n === 1 ? "Just you" : `Up to ${people(n)}: you and ${n - 1} teammates`),
  storage: (gb: number) => `${gb} GB for your game's files`,
  projects: "As many projects as fit",
  live: "Scenes, scripts, chat, all live",
  seats: (price: string, max: number, next?: string) => (next ? `Extra seats ${price} each. Need ${max} more? ${next} costs less.` : `Extra seats ${price} each, up to ${max} more`),
  startWith: (n: string) => `Start with ${n}`,
  bigger: "Bigger than 24 people?",
  contactUs: "Contact us",
  storageNote: (gb: number, price: string) => `Need more room for big art or audio? Add ${gb} GB for ${price} a month. All prices include VAT (Finland, 25.5 %).`,
  betaA: "Free while we're in ",
  betaB: "beta",
  betaLead: `Everyone gets the same plan during the beta: up to ${BETA_PROJECTS} projects with ${BETA_INVITES} teammates in each, at no cost. Paid plans and prices come with YHDE 1.0.`,
  betaPrice: "Free",
  betaPeriod: "during the beta",
  betaUpTo: `${BETA_PROJECTS} projects, you and ${BETA_INVITES} invited teammates in each`,
  betaNoCard: "No card or payment details",
  noAi: "Never used to train AI",
  betaStart: "Start free",
  betaNoCode: "Not ready yet? Leave your email and we'll tell you when new versions come out.",
  faqTitle: "Questions",
  faq: [
    ...(PLANS_OPEN
      ? [
          { q: "Do my teammates need to pay?", a: "No. Only whoever makes the projects subscribes; invited teammates join free." },
          { q: "Can I add people later?", a: "Yes. Add seats one at a time from your dashboard. When your extra seats cost as much as the next plan, we tell you, because the bigger plan also has more storage." },
        ]
      : [
          { q: "What does the beta cost?", a: "Nothing. During the beta YHDE is free, and we don't ask for payment details. Paid plans start with version 1.0, and we tell everyone who makes projects well before." },
          { q: "How many people can join?", a: `Up to ${BETA_INVITES} invited teammates in each of your ${BETA_PROJECTS} projects, and ${VIEWERS_PER_PROJECT} more who only watch through a view link.` },
        ]),
    { q: "Do I have to stop using Git?", a: "No. YHDE works alongside Git. Many teams keep Git for history and releases." },
    { q: "What if two people change the same thing at the same moment?", a: "Both see the same result right away: the most recent change wins, and nobody ends up with a broken or conflicted file." },
    { q: "What if my internet drops?", a: "Keep working. Your changes are saved on your computer and sent when you reconnect, even if the editor closes in between." },
    { q: "Which Godot version?", a: "Godot 4.7 and newer, on Windows, macOS and Linux." },
    { q: "Do I need to run a server?", a: "No. We host it. You make a project and invite your teammates." },
    ...(PLANS_OPEN ? [{ q: "Are the prices with VAT?", a: "Yes. Every price on this page includes VAT (Finnish VAT, 25.5 %)." }] : []),
    { q: "Is my project private?", a: "Yes. Connections are encrypted, only people you invite can join, and your project is backed up daily." },
    { q: "Do you train AI on my game?", a: "No, never. Your scenes, scripts, art, files, chat and comments are not used to train AI, by us or by anyone we work with. Your game is yours." },
  ],
  closeA: "One scene. Your whole ",
  closeB: "team",
  closeLead: "Start a team, make a project and send your teammates a link. It takes a few minutes.",
  guide: "Read the getting started guide",
}

type T = typeof TEXT

// "Start your team", or straight to the dashboard when already signed in.
// Used inside <Button asChild>, so it passes the button's props to the link.
function StartLink({ t, label, ...props }: { t: T; label?: string } & ComponentProps<"a">) {
  const me = useMe()
  return me ? (
    <a {...props} href={href("/app#/projects")}>
      {t.openProjects} <ArrowRight aria-hidden="true" />
    </a>
  ) : (
    <a {...props} href={href("/app#/start")}>
      {label ?? t.start} {label ? null : <ArrowRight aria-hidden="true" />}
    </a>
  )
}

export function Home() {
  const t = TEXT
  return (
    <div className="min-h-svh overflow-x-clip">
      <SiteHeader />
      <main id="main" tabIndex={-1} className="outline-none">
        <Hero t={t} />
        <WhatYouGet t={t} />
        <Problem t={t} />
        <Features t={t} />
        <Speed t={t} />
        <Plans t={t} />
        <Questions t={t} />
        <Close t={t} />
      </main>
      <SiteFooter />
    </div>
  )
}

const H2 = ({ children, className, id }: { children: ReactNode; className?: string; id?: string }) => (
  <h2 id={id} className={cn("display text-[clamp(2rem,4vw,3rem)] text-text", className)}>
    {children}
  </h2>
)

const STEP_TINTS = [
  { tint: "bg-tint-green", num: "text-good" },
  { tint: "bg-tint-blue", num: "text-sky" },
  { tint: "bg-tint-rust", num: "text-[#ffb49c]" },
]

function Hero({ t }: { t: T }) {
  return (
    <section className="mx-auto max-w-6xl px-5 pt-16 pb-20 text-center sm:px-7 sm:pt-24 lg:pb-28">
      <h1 className="display mx-auto max-w-[11ch] text-[clamp(3.25rem,9vw,6.5rem)] text-text">
        {t.h1a}
        <span className="text-sky">{t.h1b}</span>.
      </h1>
      <p className="mx-auto mt-7 max-w-[36rem] text-lg leading-relaxed font-medium text-dim sm:text-xl">{t.lead}</p>
      <div className="mt-9 flex flex-wrap justify-center gap-3">
        <Button size="lg" asChild>
          <StartLink t={t} />
        </Button>
        <Button size="lg" variant="secondary" asChild>
          <a href="#demo">{t.watch}</a>
        </Button>
      </div>
      <p className="mt-6 text-sm text-dim">{t.from(euro(PLANS[0].month))}</p>

      <div id="demo" className="mx-auto mt-14 max-w-5xl scroll-mt-24">
        <div className="overflow-hidden rounded-[1.75rem] bg-card p-2 shadow-[0_40px_80px_-30px_rgb(0_0_0/0.8)] sm:p-3">
          <video
            className="aspect-video w-full rounded-[1.25rem] bg-bg"
            src="/ui/media/yhde-promo.mp4"
            poster="/ui/media/yhde-promo.jpg"
            autoPlay
            muted
            loop
            playsInline
            controls
            preload="metadata"
            aria-label={t.videoAlt}
          >
            {t.videoAlt}
          </video>
        </div>
        <p className="mt-3 text-sm text-dim">{t.videoNote}</p>
      </div>

      <H2 id="how" className="mt-24 scroll-mt-24">
        {t.stepsA}
        <span className="text-sky">{t.stepsB}</span>
      </H2>
      <ol className="mx-auto mt-10 grid max-w-5xl gap-4 text-left md:grid-cols-3">
        {t.steps.map((s, i) => (
          <li key={s.title} className={cn("flex flex-col gap-2 rounded-[1.5rem] p-6", STEP_TINTS[i].tint)}>
            <span className={cn("text-sm font-bold", STEP_TINTS[i].num)}>{i + 1}</span>
            <h3 className="text-2xl font-bold text-text">{s.title}</h3>
            <p className="text-[0.9375rem] leading-relaxed font-medium text-text/80">{s.body}</p>
          </li>
        ))}
      </ol>
    </section>
  )
}

const PART_LOOK = [
  { icon: Puzzle, tint: "bg-tint-blue text-sky" },
  { icon: Server, tint: "bg-tint-green text-good" },
  { icon: LayoutDashboard, tint: "bg-tint-rust text-[#ffb49c]" },
]

function WhatYouGet({ t }: { t: T }) {
  return (
    <section className="mx-auto max-w-6xl px-5 py-16 sm:px-7 lg:py-20">
      <H2 className="text-center">
        {t.getA}
        <span className="text-sky">{t.getB}</span>
      </H2>
      <div className="mt-10 grid gap-4 md:grid-cols-3">
        {t.parts.map((p, i) => {
          const Icon = PART_LOOK[i].icon
          return (
            <div key={p.title} className="rounded-[1.5rem] bg-card p-7">
              <span className={cn("grid size-12 place-items-center rounded-2xl", PART_LOOK[i].tint)}>
                <Icon className="size-6" aria-hidden="true" />
              </span>
              <h3 className="mt-5 text-xl font-bold text-text">{p.title}</h3>
              <p className="mt-2 text-[0.9375rem] leading-relaxed text-dim">{p.body}</p>
            </div>
          )
        })}
      </div>
    </section>
  )
}

function Problem({ t }: { t: T }) {
  return (
    <section className="mx-auto max-w-6xl px-5 py-16 sm:px-7 lg:py-24">
      <div className="grid gap-10 rounded-[2rem] bg-card p-7 sm:p-10 lg:grid-cols-2 lg:items-center lg:gap-14 lg:p-14">
        <div>
          <H2>{t.problemTitle}</H2>
          <p className="mt-5 max-w-[34rem] text-lg leading-relaxed text-dim">{t.problemBody}</p>
        </div>
        <div className="grid gap-3">
          <div className="rounded-2xl bg-bg p-5">
            <p className="text-sm font-semibold text-dim">{t.without}</p>
            <ul className="mt-3 grid gap-2.5 text-[0.9375rem] leading-relaxed text-text/85">
              {t.withoutPoints.map((line) => (
                <li key={line} className="flex gap-3">
                  <X className="mt-1 size-4 shrink-0 text-bad" aria-hidden="true" /> {line}
                </li>
              ))}
            </ul>
            <div className="mt-3 grid grid-cols-[1fr_auto_1fr] items-center gap-2">
              {t.copies.map((c, i) => (
                <Fragment key={c.who}>
                  {i ? (
                    <span className="grid size-7 place-items-center rounded-full bg-tint-rust text-sm font-bold text-bad" aria-hidden="true">
                      ≠
                    </span>
                  ) : null}
                  <div className="rounded-xl bg-card px-3.5 py-3">
                    <p className="text-xs font-semibold text-dim">{c.who}</p>
                    <p className="mt-0.5 text-[0.8125rem] text-text">{c.where}</p>
                  </div>
                </Fragment>
              ))}
            </div>
            <p className="mt-3 text-[0.8125rem] leading-relaxed text-dim">{t.copiesNote}</p>
          </div>
          <div className="rounded-2xl bg-tint-blue p-5">
            <p className="text-sm font-semibold text-sky">{t.with}</p>
            <ul className="mt-3 grid gap-2.5 text-[0.9375rem] leading-relaxed text-text">
              {t.withPoints.map((line) => (
                <li key={line} className="flex gap-3">
                  <Check className="mt-1 size-4 shrink-0 text-sky" aria-hidden="true" /> {line}
                </li>
              ))}
            </ul>
          </div>
        </div>
      </div>
    </section>
  )
}

function Features({ t }: { t: T }) {
  return (
    <section id="features" className="mx-auto max-w-6xl scroll-mt-20 px-5 py-16 sm:px-7 lg:py-24">
      <H2 className="max-w-[16ch]">
        {t.featA}
        <span className="text-sky">{t.featB}</span>.
      </H2>
      <p className="mt-5 max-w-[40rem] text-lg leading-relaxed text-dim">{t.featLead}</p>
      <div className="mt-12 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {t.features.map((f) => (
          <div key={f.title} className="rounded-[1.25rem] bg-card p-6">
            <h3 className="text-lg leading-snug font-bold text-text">{f.title}</h3>
            <p className="mt-2 text-[0.9375rem] leading-relaxed text-dim">{f.body}</p>
          </div>
        ))}
      </div>
    </section>
  )
}

function Speed({ t }: { t: T }) {
  const scale = 25
  return (
    <section className="mx-auto max-w-6xl px-5 py-16 sm:px-7 lg:py-24">
      <div className="grid gap-10 lg:grid-cols-[1fr_1.3fr] lg:items-start lg:gap-14">
        <div>
          <H2>
            {t.speedA}
            <span className="text-sky">{t.speedB}</span>.
          </H2>
          <p className="mt-5 max-w-[34rem] text-lg leading-relaxed text-dim">{t.speedLead}</p>
          <dl className="mt-8 grid gap-3 sm:grid-cols-3 lg:grid-cols-1 xl:grid-cols-3">
            {t.stats.map((s) => (
              <div key={s.label} className="flex flex-col-reverse justify-end rounded-[1.25rem] bg-card p-5">
                <dt className="mt-1 text-sm leading-snug text-dim">{s.label}</dt>
                <dd className="tabular text-3xl font-extrabold tracking-[-0.03em] text-sky">{s.value}</dd>
              </div>
            ))}
          </dl>
        </div>
        <div className="rounded-[1.5rem] bg-card p-6 sm:p-8">
          <div className="flex flex-wrap gap-x-6 gap-y-2 text-sm text-dim">
            <span className="inline-flex items-center gap-2">
              <span className="h-2 w-4 rounded-[3px] bg-sky" aria-hidden="true" /> {t.cursors}
            </span>
            <span className="inline-flex items-center gap-2">
              <span className="h-2 w-4 rounded-[3px] bg-good" aria-hidden="true" /> {t.edits}
            </span>
          </div>
          <div className="mt-6 grid gap-7">
            {TEAM_TEST.map((r) => (
              <div key={r.people} className="grid grid-cols-[5.5rem_1fr] items-center gap-4">
                <div>
                  <p className="tabular text-2xl font-bold text-text">{r.people}</p>
                  <p className="text-xs text-dim">{t.inProject}</p>
                </div>
                <div className="grid gap-2">
                  <SpeedBar ms={r.cursor} scale={scale} color="bg-sky" />
                  <SpeedBar ms={r.edit} scale={scale} color="bg-good" />
                </div>
              </div>
            ))}
          </div>
          <div className="mt-6 grid grid-cols-[5.5rem_1fr] gap-4">
            <span className="text-xs text-bad">{t.frame}</span>
            <div className="flex items-center gap-3">
              <div className="relative h-3 flex-1">
                <div className="absolute inset-y-0 left-0 rounded-full border-2 border-dashed border-bad/70" style={{ width: `${(FRAME_MS / scale) * 100}%` }} />
              </div>
              <span className="tabular w-14 shrink-0 text-right text-sm font-semibold text-bad">16.7 ms</span>
            </div>
          </div>
          <p className="mt-7 text-xs leading-relaxed text-dim">{t.speedNote}</p>
        </div>
      </div>
    </section>
  )
}

function SpeedBar({ ms, scale, color }: { ms: number; scale: number; color: string }) {
  return (
    <div className="flex items-center gap-3">
      <div className="h-3 flex-1 overflow-hidden rounded-full bg-card-2">
        <div className={cn("h-full rounded-full", color)} style={{ width: `${(ms / scale) * 100}%` }} />
      </div>
      <span className="tabular w-14 shrink-0 text-right text-sm font-semibold text-text">{ms} ms</span>
    </div>
  )
}

function PlanCard({ p, t, period }: { p: Plan; t: T; period: "month" | "year" }) {
  const featured = p.id === "team"
  const next = nextPlan(p)
  const price = (m: number) => euro(period === "month" ? m : yearly(m))
  return (
    <article className={cn("flex flex-col rounded-[1.5rem] p-7", featured ? "bg-tint-blue ring-2 ring-sky" : "bg-card")}>
      <div className="flex min-h-7 items-center gap-3">
        <h3 className="text-xl font-bold text-text">{p.name}</h3>
        {featured ? <span className="rounded-full bg-sky px-2.5 py-0.5 text-xs font-bold text-sky-ink">{t.most}</span> : null}
      </div>
      <p className="mt-5 flex items-baseline gap-1.5">
        <span className="tabular text-5xl font-extrabold tracking-[-0.04em] text-text">{price(p.month)}</span>
        <span className="text-dim">/ {period === "month" ? t.perMonth : t.perYear}</span>
      </p>
      <p className="mt-1 text-xs font-medium text-dim">{t.vatIncl}</p>
      <p className="mt-3 min-h-[2.5rem] text-sm text-text/80">{t.upTo(p.seats)}</p>
      <ul className="mt-6 grid flex-1 content-start gap-3 text-[0.9375rem] text-text">
        <li className="flex gap-3">
          <Check className="mt-0.5 size-4 shrink-0 text-sky" aria-hidden="true" /> {t.storage(p.storageGb)}
        </li>
        <li className="flex gap-3">
          <Check className="mt-0.5 size-4 shrink-0 text-sky" aria-hidden="true" /> {t.projects}
        </li>
        <li className="flex gap-3">
          <Check className="mt-0.5 size-4 shrink-0 text-sky" aria-hidden="true" /> {t.live}
        </li>
        <li className="flex gap-3">
          <Plus className="mt-0.5 size-4 shrink-0 text-sky" aria-hidden="true" /> {t.seats(price(p.seatMonth), p.maxExtraSeats, next?.name)}
        </li>
      </ul>
      <Button className="mt-8" size="lg" variant={featured ? "default" : "secondary"} asChild>
        <a href={href(`/app#/start?plan=${p.id}`)}>{t.startWith(p.name)}</a>
      </Button>
    </article>
  )
}

// Before 1.0 the only plan: free, the owner and three invited people.
function BetaPlan({ t }: { t: T }) {
  return (
    <section id="plans" className="mx-auto max-w-6xl scroll-mt-20 px-5 py-16 sm:px-7 lg:py-24">
      <div className="text-center">
        <H2>
          {t.betaA}
          <span className="text-sky">{t.betaB}</span>.
        </H2>
        <p className="mx-auto mt-4 max-w-[38rem] text-lg text-dim">{t.betaLead}</p>
      </div>
      <div className="mx-auto mt-12 grid max-w-3xl gap-4 md:grid-cols-[1fr_1fr]">
        <article className="flex flex-col rounded-[1.5rem] bg-tint-blue p-7 ring-2 ring-sky">
          <h3 className="text-xl font-bold text-text">{BETA.name}</h3>
          <p className="mt-5 flex items-baseline gap-1.5">
            <span className="text-5xl font-extrabold tracking-[-0.04em] text-text">{t.betaPrice}</span>
            <span className="text-dim">{t.betaPeriod}</span>
          </p>
          <p className="mt-3 text-sm text-text/80">{t.betaUpTo}</p>
          <ul className="mt-6 grid flex-1 content-start gap-3 text-[0.9375rem] text-text">
            {[t.live, t.betaNoCard, t.noAi].map((x) => (
              <li key={x} className="flex gap-3">
                <Check className="mt-0.5 size-4 shrink-0 text-sky" aria-hidden="true" /> {x}
              </li>
            ))}
          </ul>
          <Button className="mt-8" size="lg" asChild>
            <StartLink t={t} label={t.betaStart} />
          </Button>
        </article>
        <div className="flex flex-col justify-center gap-5 rounded-[1.5rem] bg-card p-7">
          <p className="text-[1.0625rem] leading-relaxed text-text">{t.betaNoCode}</p>
          <EarlyAccess source="pricing" />
        </div>
      </div>
    </section>
  )
}

function Plans({ t }: { t: T }) {
  const [period, setPeriod] = useState<"month" | "year">("month")
  if (!PLANS_OPEN) return <BetaPlan t={t} />
  return (
    <section id="plans" className="mx-auto max-w-6xl scroll-mt-20 px-5 py-16 sm:px-7 lg:py-24">
      <div className="text-center">
        <H2>
          {t.plansA}
          <span className="text-sky">{t.plansB}</span>.
        </H2>
        <p className="mx-auto mt-4 max-w-[38rem] text-lg text-dim">{t.plansLead}</p>
        <div className="mt-7">
          <Segmented
            label={t.period}
            value={period}
            onChange={setPeriod}
            options={[
              { value: "month", label: t.monthly },
              { value: "year", label: t.yearlyLabel },
            ]}
          />
        </div>
      </div>
      <div className="mt-12 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {PLANS.map((p) => (
          <PlanCard key={p.id} p={p} t={t} period={period} />
        ))}
      </div>
      <p className="mt-8 text-center text-sm text-dim">
        {t.storageNote(EXTRA_STORAGE.gb, euro(EXTRA_STORAGE.month))}{" "}
        <span className="whitespace-nowrap">
          {t.bigger}{" "}
          <a href={href("/contact")} className="font-semibold text-sky hover:underline">
            {t.contactUs}
          </a>
        </span>
      </p>
    </section>
  )
}

function Questions({ t }: { t: T }) {
  return (
    <section id="questions" className="mx-auto max-w-3xl scroll-mt-20 px-5 py-16 sm:px-7 lg:py-24">
      <H2 className="text-center">{t.faqTitle}</H2>
      <div className="mt-10 grid gap-3">
        {t.faq.map((f) => (
          <details key={f.q} className="group rounded-[1.25rem] bg-card px-6 open:pb-5">
            <summary className="flex cursor-pointer list-none items-center gap-4 py-5 text-[1.0625rem] font-semibold text-text [&::-webkit-details-marker]:hidden">
              {f.q}
              <span className="ml-auto grid size-8 shrink-0 place-items-center rounded-full bg-card-2 text-sky" aria-hidden="true">
                <Plus className="size-4 group-open:hidden" />
                <Minus className="hidden size-4 group-open:block" />
              </span>
            </summary>
            <p className="max-w-[60ch] text-[0.9375rem] leading-relaxed text-dim">{f.a}</p>
          </details>
        ))}
      </div>
    </section>
  )
}

function Close({ t }: { t: T }) {
  return (
    <section className="mx-auto max-w-6xl px-5 pt-8 pb-24 sm:px-7">
      <div className="rounded-[2rem] bg-tint-blue px-7 py-16 text-center sm:px-10 lg:py-20">
        <H2 className="mx-auto max-w-[14ch] text-[clamp(2.25rem,5vw,4rem)]">
          {t.closeA}
          <span className="text-sky">{t.closeB}</span>.
        </H2>
        <p className="mx-auto mt-5 max-w-[32rem] text-lg text-text/80">{t.closeLead}</p>
        <div className="mt-9 flex flex-wrap justify-center gap-3">
          <Button size="lg" asChild>
            <StartLink t={t} />
          </Button>
          <Button size="lg" variant="secondary" asChild>
            <a href={href("/docs")}>{t.guide}</a>
          </Button>
        </div>
      </div>
    </section>
  )
}

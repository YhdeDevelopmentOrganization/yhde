import { useState } from "react"
import { Minus, Plus } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { cn } from "@/lib/utils"
import { Meter, Panel, Readout } from "@/world/parts"
import { BETA, EXTRA_STORAGE, PLANS, PLANS_OPEN, VIEWERS_PER_PROJECT, euro, yearly, type PlanId } from "@/world/plans"
import { PageHead, run } from "../App"
import { api, planUsage, projectSeats, useAppState, type MyPlan } from "../store"

// The plan of someone who makes projects: what it allows and how much of it
// is used. Until 1.0 everyone is on the free beta tester plan.
export function BillingPage() {
  const s = useAppState()
  if (!s.plan) return null
  return PLANS_OPEN ? <PaidBilling plan={s.plan} /> : <BetaBilling plan={s.plan} />
}

function Usage({ plan }: { plan: MyPlan }) {
  const s = useAppState()
  const u = planUsage(plan)
  const mine = s.projects.filter((p) => p.role === "owner")
  return (
    <Panel title="Usage" className="xl:col-span-7">
      <div className="grid gap-6 sm:grid-cols-2">
        <div className="grid gap-2.5">
          <div className="flex items-baseline justify-between">
            <span className="label-dim">Projects</span>
            <span className="text-sm text-text tabular-nums">
              {plan.projects}
              {plan.maxProjects !== null ? ` / ${plan.maxProjects}` : ""}
            </span>
          </div>
          {plan.maxProjects !== null ? <Meter value={plan.projects / plan.maxProjects} /> : null}
          <p className="text-xs text-dim">{u.projectsFull ? "No room for another. Archive and delete one to make a new one." : "Archived projects count: they keep their files."}</p>
        </div>
        <div className="grid gap-2.5">
          <div className="flex items-baseline justify-between">
            <span className="label-dim">Storage</span>
            <span className="text-sm text-text tabular-nums">
              {Math.round(u.storageShare * 100)} %
            </span>
          </div>
          <Meter value={u.storageShare} color={u.storageFull ? "var(--bad)" : u.storageWarning ? "#ffb49c" : undefined} />
          <p className={cn("text-xs", u.storageWarning ? "text-text" : "text-dim")}>
            {u.storageFull
              ? "Full: new files from Godot are refused until you free space."
              : u.storageWarning
                ? `${Math.round(u.storageShare * 100)} % used. When it's full, new files from Godot are refused.`
                : "All your projects' files together."}
          </p>
        </div>
      </div>
      {mine.length ? (
        <div className="mt-6 border-t border-line pt-4">
          <p className="label-dim mb-2">People in each project</p>
          {mine.map((p) => {
            const seats = projectSeats(p)
            return (
              <Readout
                key={p.id}
                label={<a className="hover:text-sky" href={`#/projects/${p.id}`}>{p.name}</a>}
                hint={`${p.viewers} of ${p.viewersLimit} viewers`}
                value={`${seats.used} / ${seats.limit} invited`}
              />
            )
          })}
        </div>
      ) : null}
    </Panel>
  )
}

function Codes({ plan }: { plan: MyPlan }) {
  return (
    <>
      {plan.promos.map((p, i) => (
        <Readout
          key={i}
          label="Code"
          value={<span className="text-xs text-text">{p.text}</span>}
          hint={p.until ? `until ${new Date(p.until).toLocaleDateString([], { dateStyle: "medium" })}` : undefined}
        />
      ))}
      <CodeField />
    </>
  )
}

// Until 1.0: the free beta tester plan, so there is only usage to show and
// nothing to buy.
function BetaBilling({ plan }: { plan: MyPlan }) {
  return (
    <>
      <PageHead title="Plan" sub="Free during the beta." />
      <div className="grid gap-5 xl:grid-cols-12">
        <Panel title="Your plan" className="xl:col-span-5">
          <div className="flex items-baseline gap-3 pb-4">
            <span className="text-4xl leading-none text-sky">{BETA.name}</span>
          </div>
          <Readout label="Price" value="Free" hint="during the beta" />
          <Readout label="Projects" value={`${plan.maxProjects ?? "Any number"}`} />
          <Readout label="People" value={`${plan.peoplePerProject}`} hint={`invited into each project${plan.freeSeats ? `, ${plan.freeSeats} of them free from YHDE` : ""}`} />
          <Readout label="Viewers" value={`${VIEWERS_PER_PROJECT}`} hint="per project, through view links" />
          <Readout label="Payment" value={<span className="text-xs text-text">Not needed</span>} />
          <Codes plan={plan} />
        </Panel>
        <Usage plan={plan} />
        <Panel title="After the beta" className="xl:col-span-12">
          <p className="max-w-[60ch] text-sm leading-relaxed text-dim">
            Paid plans and their prices come with YHDE 1.0. We'll email you at least 30 days before, and nobody is moved to a paid plan without
            choosing one. The {BETA.name} plan stays free until then. People you invite never pay.
          </p>
        </Panel>
      </div>
    </>
  )
}

// From 1.0: the paid plans, extra seats (more people in each project) and
// extra storage.
function PaidBilling({ plan }: { plan: MyPlan }) {
  const [period, setPeriod] = useState(plan.period)
  const [busy, setBusy] = useState<PlanId | null>(null)
  const info = planUsage(plan).info
  const perPeriod = (m: number) => (plan.period === "month" ? m : yearly(m))
  const base = perPeriod(info.month)
  const seatCost = plan.extraSeats * perPeriod(info.seatMonth)
  const extraCost = plan.extraStorage * perPeriod(EXTRA_STORAGE.month)

  return (
    <>
      <PageHead title="Plan & billing" sub="Prices include Finnish VAT (25.5 %)." />
      <div className="grid gap-5 xl:grid-cols-12">
        <Panel title="This period" className="xl:col-span-5">
          <div className="flex items-baseline gap-3 pb-4">
            <span className="text-4xl leading-none text-sky tabular-nums">{euro(base + seatCost + extraCost)}</span>
            <span className="text-xs text-dim">/ {plan.period}</span>
          </div>
          <Readout label={info.name} value={euro(base)} hint={`${info.seats - 1} people per project · ${info.storageGb} GB`} />
          <Readout label="Extra seats" value={plan.extraSeats ? euro(seatCost) : "-"} hint={plan.extraSeats ? `+${plan.extraSeats} per project` : undefined} />
          <Readout label="Extra storage" value={plan.extraStorage ? euro(extraCost) : "-"} hint={plan.extraStorage ? `${plan.extraStorage * EXTRA_STORAGE.gb} GB` : undefined} />
          <Codes plan={plan} />
        </Panel>
        <Usage plan={plan} />
        <Panel title="More room" className="xl:col-span-12">
          <Stepper
            title="Extra seats"
            hint={info.maxExtraSeats ? `${euro(info.seatMonth)} a month each: one more person in every project, up to ${info.maxExtraSeats}.` : "Your plan has no extra seats."}
            value={`+${plan.extraSeats}`}
            less={plan.extraSeats > 0 ? () => run(() => api.setExtraSeats(plan.extraSeats - 1)) : undefined}
            more={plan.extraSeats < info.maxExtraSeats ? () => run(() => api.setExtraSeats(plan.extraSeats + 1)) : undefined}
          />
          <Stepper
            title="Extra storage"
            hint={`${EXTRA_STORAGE.gb} GB for ${euro(EXTRA_STORAGE.month)} a month, for big art and audio.`}
            value={`+${plan.extraStorage * EXTRA_STORAGE.gb} GB`}
            less={plan.extraStorage > 0 ? () => run(() => api.setExtraStorage(plan.extraStorage - 1)) : undefined}
            more={plan.extraStorage < 20 ? () => run(() => api.setExtraStorage(plan.extraStorage + 1)) : undefined}
          />
        </Panel>
        <Panel
          title="Plans"
          className="xl:col-span-12"
          action={
            <div className="flex rounded-full bg-card-2 p-1" role="group" aria-label="Billing period">
              {(["month", "year"] as const).map((p) => (
                <button
                  key={p}
                  type="button"
                  aria-pressed={period === p}
                  onClick={() => setPeriod(p)}
                  className={cn("cursor-pointer rounded-full px-3.5 py-1 text-sm font-semibold transition-colors", period === p ? "bg-sky text-sky-ink" : "text-dim hover:text-text")}
                >
                  {p === "month" ? "Monthly" : "Yearly"}
                </button>
              ))}
            </div>
          }
        >
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            {PLANS.map((p) => {
              const current = p.id === plan.id && period === plan.period
              return (
                <div key={p.id} className={cn("flex flex-col gap-4 rounded-2xl border-2 px-5 py-5", current ? "border-sky bg-tint-blue" : "border-transparent bg-card-2/60")}>
                  <div className="flex items-baseline justify-between">
                    <span className="label">{p.name}</span>
                    <span className="text-lg text-sky tabular-nums">
                      {euro(period === "month" ? p.month : yearly(p.month))}
                      <span className="text-xs text-text/75"> / {period}, incl. VAT</span>
                    </span>
                  </div>
                  <p className="text-xs text-dim">
                    {p.seats - 1} people per project · {p.storageGb} GB
                  </p>
                  {current ? (
                    <span className="label mt-auto py-2">Your plan</span>
                  ) : (
                    <Button
                      className="mt-auto"
                      variant="outline"
                      disabled={busy !== null}
                      onClick={async () => {
                        setBusy(p.id)
                        await run(() => api.changePlan(p.id, period), `Now on ${p.name}`)
                        setBusy(null)
                      }}
                    >
                      {busy === p.id ? "Changing…" : `Switch to ${p.name}`}
                    </Button>
                  )}
                </div>
              )
            })}
          </div>
        </Panel>
      </div>
    </>
  )
}

function Stepper({ title, hint, value, less, more }: { title: string; hint: string; value: string; less?: () => void; more?: () => void }) {
  return (
    <div className="flex flex-wrap items-center gap-4 border-b border-line py-4 last:border-b-0">
      <div className="min-w-0 flex-1">
        <p className="text-sm text-text">{title}</p>
        <p className="text-xs text-dim">{hint}</p>
      </div>
      <div className="flex items-center rounded-full bg-card-2">
        <Button variant="ghost" size="icon-sm" aria-label={`Less: ${title}`} disabled={!less} onClick={less}>
          <Minus />
        </Button>
        <span className="w-20 text-center text-xs text-text tabular-nums">{value}</span>
        <Button variant="ghost" size="icon-sm" aria-label={`More: ${title}`} disabled={!more} onClick={more}>
          <Plus />
        </Button>
      </div>
    </div>
  )
}

// "Have a code?": a quiet link that opens the field (codes are handed out,
// never listed on the site).
function CodeField() {
  const [open, setOpen] = useState(false)
  const [code, setCode] = useState("")
  const [busy, setBusy] = useState(false)
  if (!open)
    return (
      <button type="button" className="mt-3 w-fit cursor-pointer text-xs font-semibold text-sky hover:underline" onClick={() => setOpen(true)}>
        Have a code?
      </button>
    )
  return (
    <form
      className="mt-4 flex gap-2"
      onSubmit={async (e) => {
        e.preventDefault()
        setBusy(true)
        const ok = await run(() => api.redeemCode(code), "Code applied")
        setBusy(false)
        if (ok) {
          setCode("")
          setOpen(false)
        }
      }}
    >
      <Input className="h-9 font-mono uppercase" placeholder="CODE" aria-label="Promo code" value={code} maxLength={40} autoFocus autoComplete="off" onChange={(e) => setCode(e.target.value)} />
      <Button type="submit" size="sm" className="h-9" disabled={busy || code.trim().length < 3}>
        {busy ? "Checking…" : "Apply"}
      </Button>
    </form>
  )
}

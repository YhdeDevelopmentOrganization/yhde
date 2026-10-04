// The plans (drafted, not final). Every price includes Finnish VAT (25.5 %).
//
// Each plan can add seats one at a time, up to the next plan's size. The
// bigger the plan, the cheaper its extra seats; and filling a plan up to the
// next one's size always costs a little more than the next plan, which also
// has more storage, so moving up is the better deal at that point.
import { PRODUCT_VERSION } from "./version"
import { OFFICIAL } from "./site"

export type PlanId = "beta" | "solo" | "trio" | "team" | "studio" | "self"
export type Plan = {
  id: PlanId
  name: string
  seats: number
  storageGb: number
  month: number
  // An extra seat on this plan, per month, and how many can be added.
  seatMonth: number
  maxExtraSeats: number
}

export const PLANS: Plan[] = [
  { id: "solo", name: "Solo", seats: 1, storageGb: 2, month: 3.75, seatMonth: 3.5, maxExtraSeats: 2 },
  { id: "trio", name: "Trio", seats: 3, storageGb: 5, month: 7.5, seatMonth: 3, maxExtraSeats: 3 },
  { id: "team", name: "Team", seats: 6, storageGb: 15, month: 15, seatMonth: 2.75, maxExtraSeats: 6 },
  { id: "studio", name: "Studio", seats: 12, storageGb: 40, month: 30, seatMonth: 2.25, maxExtraSeats: 12 },
]

// The beta tester plan: free, up to three projects and 2 GB in all (whichever
// comes first), each project its owner and three invited people. Until 1.0.0
// it is the only plan anyone can pick or see; the paid plans above show from
// 1.0.0 on. The same numbers are in the server's Plans (TeamStore.cs).
export const BETA: Plan = { id: "beta", name: "Beta tester", seats: 4, storageGb: 2, month: 0, seatMonth: 0, maxExtraSeats: 0 }
export const BETA_INVITES = BETA.seats - 1
export const BETA_PROJECTS = 3
// People who can watch a project through its view links, at most.
export const VIEWERS_PER_PROJECT = 5

// A self-hosted server sells nothing: no plans to pick, ever (SiteMode.cs).
export const PLANS_OPEN = OFFICIAL && Number(PRODUCT_VERSION.split(".")[0]) >= 1

// The plans people can pick right now.
export const OPEN_PLANS: Plan[] = PLANS_OPEN ? PLANS : [BETA]

export const EXTRA_STORAGE = { gb: 10, month: 3.75 }

// Yearly: two months free.
export const yearly = (month: number) => month * 10

// Old plan ids (before Solo and Trio) still resolve.
// A self-hosted server's only plan: no limits beyond its own disk (Plans.SelfHosted).
export const SELF_HOSTED: Plan = { id: "self", name: "Self-hosted", seats: 100_000, storageGb: 1_000_000, month: 0, seatMonth: 0, maxExtraSeats: 0 }

export const planById = (id: string | null | undefined) =>
  !OFFICIAL ? SELF_HOSTED : id === "beta" ? BETA : PLANS.find((p) => p.id === (id === "duo" ? "trio" : id))

export const nextPlan = (p: Plan): Plan | undefined => (p.id === "beta" ? undefined : PLANS[PLANS.indexOf(p) + 1])

// What a plan with extra seats costs a month.
export const monthlyWith = (p: Plan, extraSeats: number) => p.month + Math.max(0, extraSeats) * p.seatMonth

// The bigger plan that costs no more than this one with its extra seats.
export function betterPlan(p: Plan, extraSeats: number): Plan | undefined {
  const next = nextPlan(p)
  return next && extraSeats > 0 && p.seats + extraSeats >= next.seats && next.month <= monthlyWith(p, extraSeats) ? next : undefined
}

export const euro = (n: number) => "€" + (Number.isInteger(n) ? String(n) : n.toFixed(2))

export const people = (n: number) => (n === 1 ? "1 person" : `${n} people`)

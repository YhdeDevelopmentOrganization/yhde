import { useEffect, useState, type ReactNode } from "react"
import { ArrowLeft, ArrowRight, Check, MailCheck } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { cn } from "@/lib/utils"
import { Wordmark } from "@/world/parts"
import { PLANS, PLANS_OPEN, euro, planById, yearly, type PlanId } from "@/world/plans"
import { CookieNotice, href } from "@/site/chrome"
import { go, type Route } from "../App"
import { auth, providerUrl, useMe } from "../auth"

const TEXT = {
  home: "YHDE home",
  sideTitleA: "Make your game ",
  sideTitleB: "together",
  side: ["Your whole team in the same Godot project", "Teammates join free", "Every change shows up for everyone at once"],
  sideNote: "Your password is stored only as a secure hash. We never see it, and never sell your data.",
  or: "or with email",
  github: "Continue with GitHub",
  google: "Continue with Google",
  signIn: "Sign in",
  email: "Email",
  password: "Password",
  forgot: "Forgot it?",
  signingIn: "Signing in…",
  newHere: "New to YHDE?",
  startTeam: "Start a team",
  joining: "Invited to a project? Open the link in the invitation, or sign up with the address it went to.",
  steps: ["Plan", "Account", "Confirm email"],
  stepsLabel: "Steps",
  planTitle: "How big is your team?",
  planSub: "You pay; everyone you invite joins free. Payment comes later: nothing is charged today.",
  period: "Billing period",
  monthly: "Monthly",
  yearlyLabel: "Yearly · 2 months free",
  planLabel: "Plan",
  perMonth: "month",
  perYear: "year",
  vat: "incl. VAT",
  people: (n: number) => (n === 1 ? "1 person" : `${n} people`),
  moreSeats: (n: number) => `+${n} seats later`,
  cont: "Continue",
  haveAccount: "Already have an account?",
  accountTitle: "Make your account",
  accountSub: "You'll be the team's owner.",
  name: "Your name",
  nameHint: "Your teammates see it next to your cursor.",
  pwHint: "At least 10 characters. A short sentence works well.",
  back: "Back",
  making: "Making your account…",
  make: "Make account",
  agreeA: "By making an account you agree to the",
  terms: "Terms",
  and: "and the",
  privacy: "Privacy policy",
  checkTitle: "Check your email",
  checkSub: (email: string) => <>We sent a link to <strong className="text-text">{email}</strong>. Open it to confirm your email, then sign in.</>,
  spam: "Nothing there after a few minutes? Check your spam folder.",
  toSignIn: "Go to sign in",
  confirming: "Confirming…",
  setTitle: "You're all set",
  setSub: "Your email is confirmed.",
  continueBtn: "Continue",
  badLink: "That link didn't work",
  forgotTitle: "Forgot your password?",
  forgotSub: "We'll email you a link to choose a new one.",
  sending: "Sending…",
  sendReset: "Send reset link",
  backSignIn: "Back to sign in",
  resetSent: (email: string) => <>If <strong className="text-text">{email}</strong> has a YHDE account, a reset link is on its way. It works for 1 hour.</>,
  newPwTitle: "Choose a new password",
  newPwSub: "You'll be signed out everywhere else.",
  newPw: "New password",
  saving: "Saving…",
  saveSignIn: "Save and sign in",
}

function AuthLayout({ children }: { children: ReactNode }) {
  const t = TEXT
  return (
    <div className="grid min-h-[calc(100svh-2.4rem)] lg:grid-cols-[minmax(0,34rem)_1fr]">
      <div className="flex flex-col px-5 py-6 sm:px-10">
        <div className="flex items-center">
          <a href={href("/")} aria-label={t.home} className="w-fit">
            <Wordmark />
          </a>
        </div>
        <CookieNotice />
        <main id="main" className="my-auto w-full max-w-md py-12">
          {children}
        </main>
      </div>
      <div className="hidden p-4 lg:block">
        <div className="flex h-full flex-col justify-between rounded-[2rem] bg-tint-blue p-12">
          <div>
            <p className="display max-w-[12ch] text-[3.25rem] text-text">
              {t.sideTitleA}
              <span className="text-sky">{t.sideTitleB}</span>.
            </p>
            <ul className="mt-10 grid gap-4 text-lg text-text/85">
              {t.side.map((line) => (
                <li key={line} className="flex gap-3">
                  <Check className="mt-1 size-5 shrink-0 text-sky" aria-hidden="true" /> {line}
                </li>
              ))}
            </ul>
          </div>
          <p className="text-sm text-text/70">{t.sideNote}</p>
        </div>
      </div>
    </div>
  )
}

function Title({ children, sub }: { children: ReactNode; sub?: ReactNode }) {
  return (
    <div className="mb-7">
      <h1 className="display text-[2rem] text-text">{children}</h1>
      {sub ? <p className="mt-2 text-sm text-dim">{sub}</p> : null}
    </div>
  )
}

function Field({ id, label, hint, ...props }: { id: string; label: string; hint?: string } & React.ComponentProps<typeof Input>) {
  return (
    <div className="grid gap-2">
      <Label htmlFor={id} className="label-dim">
        {label}
      </Label>
      <Input id={id} {...props} aria-describedby={hint ? `${id}-hint` : undefined} className="h-11" />
      {hint ? (
        <p id={`${id}-hint`} className="text-xs text-dim">
          {hint}
        </p>
      ) : null}
    </div>
  )
}

function Problem({ children }: { children: ReactNode }) {
  return children ? (
    <p role="alert" className="rounded-xl bg-tint-rust px-4 py-3 text-sm text-[#ffd2c4]">
      {children}
    </p>
  ) : null
}

const GitHubMark = () => (
  <svg viewBox="0 0 16 16" className="size-4" aria-hidden="true" fill="currentColor">
    <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z" />
  </svg>
)
const GoogleMark = () => (
  <svg viewBox="0 0 18 18" className="size-4" aria-hidden="true">
    <path fill="#4285F4" d="M17.64 9.2c0-.64-.06-1.25-.16-1.84H9v3.48h4.84a4.14 4.14 0 01-1.8 2.72v2.26h2.92c1.7-1.57 2.68-3.88 2.68-6.62z" />
    <path fill="#34A853" d="M9 18c2.43 0 4.47-.8 5.96-2.18l-2.92-2.26c-.8.54-1.83.86-3.04.86-2.34 0-4.32-1.58-5.03-3.7H.96v2.33A9 9 0 009 18z" />
    <path fill="#FBBC05" d="M3.97 10.72A5.41 5.41 0 013.68 9c0-.6.1-1.18.29-1.72V4.95H.96A9 9 0 000 9c0 1.45.35 2.83.96 4.05l3.01-2.33z" />
    <path fill="#EA4335" d="M9 3.58c1.32 0 2.51.45 3.44 1.35l2.58-2.58A9 9 0 009 0 9 9 0 00.96 4.95l3.01 2.33C4.68 5.16 6.66 3.58 9 3.58z" />
  </svg>
)

// "Continue with GitHub / Google", only for providers set up on the server.
function Providers({ returnTo }: { returnTo?: string }) {
  const t = TEXT
  const [p, setP] = useState<{ github: boolean; google: boolean } | null>(null)
  useEffect(() => {
    auth.providers().then(setP)
  }, [])
  if (!p || (!p.github && !p.google)) return null
  return (
    <div className="grid gap-3">
      {p.github ? (
        <Button variant="secondary" size="lg" asChild>
          <a href={providerUrl("github", { returnTo })}>
            <GitHubMark /> {t.github}
          </a>
        </Button>
      ) : null}
      {p.google ? (
        <Button variant="secondary" size="lg" asChild>
          <a href={providerUrl("google", { returnTo })}>
            <GoogleMark /> {t.google}
          </a>
        </Button>
      ) : null}
      <div className="my-2 flex items-center gap-3 text-xs text-dim">
        <span className="h-px flex-1 bg-line" aria-hidden="true" /> {t.or} <span className="h-px flex-1 bg-line" aria-hidden="true" />
      </div>
    </div>
  )
}

export function SignInPage({ route }: { route: Route }) {
  const t = TEXT
  const [email, setEmail] = useState("")
  const [password, setPassword] = useState("")
  const [error, setError] = useState(route.query.get("error") ?? "")
  const [busy, setBusy] = useState(false)
  return (
    <AuthLayout>
      <Title>{t.signIn}</Title>
      <Providers returnTo={location.hash.startsWith("#/device") ? "/app" + location.hash : undefined} />
      <form
        className="grid gap-5"
        onSubmit={async (e) => {
          e.preventDefault()
          setBusy(true)
          setError("")
          try {
            await auth.login(email, password)
            // Signing in to approve Godot: stay on that page.
            if (!location.hash.startsWith("#/device")) go("/projects")
          } catch (err) {
            setError((err as Error).message)
          } finally {
            setBusy(false)
          }
        }}
      >
        <Field id="email" label={t.email} type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} autoFocus required />
        <div className="grid gap-2">
          <div className="flex items-baseline">
            <Label htmlFor="password" className="label-dim">
              {t.password}
            </Label>
            <a href="#/forgot" className="ml-auto text-xs font-semibold text-sky hover:underline">
              {t.forgot}
            </a>
          </div>
          <Input id="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} className="h-11" required />
        </div>
        <Problem>{error || null}</Problem>
        <Button type="submit" size="lg" disabled={busy}>
          {busy ? t.signingIn : t.signIn}
        </Button>
      </form>
      <p className="mt-8 text-sm text-dim">
        {t.newHere}{" "}
        <a href="#/start" className="font-semibold text-sky hover:underline">
          {t.startTeam}
        </a>
        <span className="mx-2" aria-hidden="true">
          ·
        </span>
        {t.joining}
      </p>
    </AuthLayout>
  )
}

export function StartPage({ route }: { route: Route }) {
  const t = TEXT
  const me = useMe()
  // During the beta there is no plan to pick: the steps start at Account.
  const first = PLANS_OPEN ? 0 : 1
  const [step, setStep] = useState(first)
  const [plan, setPlan] = useState<PlanId>(PLANS_OPEN ? (planById(route.query.get("plan"))?.id ?? "team") : "beta")
  const [period, setPeriod] = useState<"month" | "year">("month")
  const [form, setForm] = useState({ name: "", email: "", password: "" })
  const [error, setError] = useState("")
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    if (!PLANS_OPEN && me && step === 1) go("/projects")
  }, [me, step])

  return (
    <AuthLayout>
      <ol className="mb-8 flex flex-wrap items-center gap-2" aria-label={t.stepsLabel}>
        {t.steps.map((s, i) => i < first ? null : (
          <li key={s} className="flex items-center gap-2" aria-current={i === step ? "step" : undefined}>
            <span className={cn("grid size-6 place-items-center rounded-full text-xs font-bold", i < step ? "bg-sky text-sky-ink" : i === step ? "bg-tint-blue text-sky" : "bg-card-2 text-dim")}>
              {i < step ? <Check className="size-3.5" aria-hidden="true" /> : i + 1 - first}
            </span>
            <span className={cn("text-sm", i === step ? "font-semibold text-text" : "text-dim")}>{s}</span>
            {i < t.steps.length - 1 ? <span className="mx-1 h-px w-5 bg-line" aria-hidden="true" /> : null}
          </li>
        ))}
      </ol>

      {step === 0 ? (
        <>
          <Title sub={t.planSub}>{t.planTitle}</Title>
          <div className="flex w-fit rounded-full bg-card-2 p-1" role="group" aria-label={t.period}>
            {(["month", "year"] as const).map((p) => (
              <button
                key={p}
                type="button"
                aria-pressed={period === p}
                onClick={() => setPeriod(p)}
                className={cn("cursor-pointer rounded-full px-4 py-1.5 text-sm font-semibold transition-colors", period === p ? "bg-sky text-sky-ink" : "text-dim hover:text-text")}
              >
                {p === "month" ? t.monthly : t.yearlyLabel}
              </button>
            ))}
          </div>
          <div className="mt-4 grid gap-2" role="radiogroup" aria-label={t.planLabel}>
            {PLANS.map((p) => (
              <button
                key={p.id}
                type="button"
                role="radio"
                aria-checked={plan === p.id}
                onClick={() => setPlan(p.id)}
                className={cn(
                  "grid cursor-pointer grid-cols-[1fr_auto] items-center gap-x-4 gap-y-2 rounded-2xl border-2 px-5 py-4 text-left transition-colors",
                  plan === p.id ? "border-sky bg-tint-blue" : "border-transparent bg-card hover:bg-card-2",
                )}
              >
                <span className="font-bold text-text">{p.name}</span>
                <span className="font-semibold text-sky tabular-nums">
                  {euro(period === "month" ? p.month : yearly(p.month))}
                  <span className="text-xs font-medium text-text/75">
                    {" "}
                    / {period === "month" ? t.perMonth : t.perYear}, {t.vat}
                  </span>
                </span>
                <span className="flex gap-1" aria-hidden="true">
                  {Array.from({ length: p.seats }, (_, i) => (
                    <span key={i} className={cn("size-2.5 rounded-[3px]", i === 0 ? "bg-sky" : "border border-text/45")} />
                  ))}
                </span>
                <span className="text-xs text-text/75">
                  {t.people(p.seats)} · {p.storageGb} GB · {t.moreSeats(p.maxExtraSeats)}
                </span>
              </button>
            ))}
          </div>
          <Button size="lg" className="mt-6 w-full" onClick={() => (me ? go("/projects") : setStep(1))}>
            {t.cont} <ArrowRight aria-hidden="true" />
          </Button>
          <p className="mt-6 text-sm text-dim">
            {t.haveAccount}{" "}
            <a href="#/sign-in" className="font-semibold text-sky hover:underline">
              {t.signIn}
            </a>
          </p>
        </>
      ) : step === 1 ? (
        <>
          <Title sub={t.accountSub}>{t.accountTitle}</Title>
          <Providers />
          <form
            className="grid gap-5"
            onSubmit={async (e) => {
              e.preventDefault()
              setError("")
              setBusy(true)
              try {
                await auth.register(form.email, form.password, form.name)
                try {
                  localStorage.setItem("yhde.plan", JSON.stringify({ plan, period }))
                } catch {
                  /* not remembered; they choose again later */
                }
                setStep(2)
              } catch (err) {
                setError((err as Error).message)
              } finally {
                setBusy(false)
              }
            }}
          >
            <Field id="name" label={t.name} autoComplete="name" maxLength={48} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} hint={t.nameHint} required />
            <Field id="email" label={t.email} type="email" autoComplete="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} required />
            <Field id="password" label={t.password} type="password" autoComplete="new-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} hint={t.pwHint} required minLength={10} />
            <Problem>{error || null}</Problem>
            <div className="flex gap-2">
              {PLANS_OPEN ? (
                <Button type="button" variant="ghost" size="lg" onClick={() => setStep(0)}>
                  <ArrowLeft aria-hidden="true" /> {t.back}
                </Button>
              ) : null}
              <Button type="submit" size="lg" className="flex-1" disabled={busy}>
                {busy ? t.making : t.make}
              </Button>
            </div>
            <p className="text-xs leading-relaxed text-dim">
              {t.agreeA}{" "}
              <a href={href("/terms")} className="font-semibold text-sky hover:underline">
                {t.terms}
              </a>{" "}
              {t.and}{" "}
              <a href={href("/privacy")} className="font-semibold text-sky hover:underline">
                {t.privacy}
              </a>
              .
            </p>
          </form>
        </>
      ) : (
        <div className="grid gap-5">
          <span className="grid size-14 place-items-center rounded-2xl bg-tint-green text-good">
            <MailCheck className="size-7" aria-hidden="true" />
          </span>
          <Title sub={t.checkSub(form.email)}>{t.checkTitle}</Title>
          <p className="text-sm text-dim">{t.spam}</p>
          <Button variant="secondary" size="lg" asChild>
            <a href="#/sign-in">{t.toSignIn}</a>
          </Button>
        </div>
      )}
    </AuthLayout>
  )
}

// A confirm link works once; React may run the effect twice while developing.
let verifying: Promise<unknown> | null = null

export function VerifyPage({ route }: { route: Route }) {
  const t = TEXT
  const [state, setState] = useState<"busy" | "ok" | "failed">("busy")
  const [error, setError] = useState("")
  useEffect(() => {
    const token = route.query.get("token") ?? ""
    verifying ??= auth.verify(token)
    verifying.then(
      () => setState("ok"),
      (e) => {
        setError((e as Error).message)
        setState("failed")
      },
    )
    // The token is used once; take it out of the address bar.
    history.replaceState(null, "", location.pathname + "#/verify")
  }, [])
  return (
    <AuthLayout>
      {state === "busy" ? (
        <Title>{t.confirming}</Title>
      ) : state === "ok" ? (
        <div className="grid gap-5">
          <span className="grid size-14 place-items-center rounded-2xl bg-tint-green text-good">
            <Check className="size-7" aria-hidden="true" />
          </span>
          <Title sub={t.setSub}>{t.setTitle}</Title>
          <Button size="lg" asChild>
            <a href="#/projects">{t.continueBtn}</a>
          </Button>
        </div>
      ) : (
        <div className="grid gap-5">
          <Title>{t.badLink}</Title>
          <Problem>{error}</Problem>
          <Button variant="secondary" size="lg" asChild>
            <a href="#/sign-in">{t.toSignIn}</a>
          </Button>
        </div>
      )}
    </AuthLayout>
  )
}

export function ForgotPage() {
  const t = TEXT
  const [email, setEmail] = useState("")
  const [sent, setSent] = useState(false)
  const [error, setError] = useState("")
  const [busy, setBusy] = useState(false)
  return (
    <AuthLayout>
      {sent ? (
        <div className="grid gap-5">
          <span className="grid size-14 place-items-center rounded-2xl bg-tint-green text-good">
            <MailCheck className="size-7" aria-hidden="true" />
          </span>
          <Title sub={t.resetSent(email)}>{t.checkTitle}</Title>
          <Button variant="secondary" size="lg" asChild>
            <a href="#/sign-in">{t.backSignIn}</a>
          </Button>
        </div>
      ) : (
        <>
          <Title sub={t.forgotSub}>{t.forgotTitle}</Title>
          <form
            className="grid gap-5"
            onSubmit={async (e) => {
              e.preventDefault()
              setBusy(true)
              setError("")
              try {
                await auth.forgot(email)
                setSent(true)
              } catch (err) {
                setError((err as Error).message)
              } finally {
                setBusy(false)
              }
            }}
          >
            <Field id="email" label={t.email} type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} autoFocus required />
            <Problem>{error || null}</Problem>
            <Button type="submit" size="lg" disabled={busy}>
              {busy ? t.sending : t.sendReset}
            </Button>
            <a href="#/sign-in" className="text-sm font-semibold text-sky hover:underline">
              {t.backSignIn}
            </a>
          </form>
        </>
      )}
    </AuthLayout>
  )
}

export function ResetPage({ route }: { route: Route }) {
  const t = TEXT
  const [token] = useState(route.query.get("token") ?? "")
  const [password, setPassword] = useState("")
  const [error, setError] = useState("")
  const [busy, setBusy] = useState(false)
  useEffect(() => history.replaceState(null, "", location.pathname + "#/reset"), [])
  return (
    <AuthLayout>
      <Title sub={t.newPwSub}>{t.newPwTitle}</Title>
      <form
        className="grid gap-5"
        onSubmit={async (e) => {
          e.preventDefault()
          setBusy(true)
          setError("")
          try {
            await auth.reset(token, password)
            go("/projects")
          } catch (err) {
            setError((err as Error).message)
          } finally {
            setBusy(false)
          }
        }}
      >
        <Field id="password" label={t.newPw} type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} hint={t.pwHint} autoFocus required minLength={10} />
        <Problem>{error || null}</Problem>
        <Button type="submit" size="lg" disabled={busy || !token}>
          {busy ? t.saving : t.saveSignIn}
        </Button>
      </form>
    </AuthLayout>
  )
}

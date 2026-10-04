import { useState } from "react"
import { Check, Download, MailX, ShieldCheck, UserX } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { COMPANY, LEGAL_UPDATED, companyLine } from "../company"
import { leaveEarlyAccess } from "../api"
import { href } from "../chrome"
import { Draft, PageTop, Prose, WithToc } from "../parts"

const TOC = [
  { id: "who", label: "Who we are" },
  { id: "ai", label: "No AI training" },
  { id: "what", label: "What we collect" },
  { id: "why", label: "Why, and the legal basis" },
  { id: "where", label: "Where it is stored" },
  { id: "sharing", label: "Who else sees it" },
  { id: "how-long", label: "How long we keep it" },
  { id: "cookies", label: "Cookies" },
  { id: "rights", label: "Your rights (GDPR)" },
  { id: "your-data", label: "Do it yourself" },
  { id: "children", label: "Children" },
  { id: "changes", label: "Changes" },
]

export function Privacy() {
  return (
    <>
      <PageTop title="Privacy" accent="policy" lead={<>Updated {LEGAL_UPDATED}. The short version: we keep what's needed to run your projects, nothing is sold, and there's no tracking.</>}>
        <NoAiTraining />
        <Draft>This policy describes how YHDE works today, during the beta. It follows the EU General Data Protection Regulation (GDPR) and has not yet been reviewed by a lawyer.</Draft>
      </PageTop>
      <WithToc toc={TOC}>
        <Prose>
          <h2 id="who">Who we are</h2>
          <p>
            YHDE is run by {companyLine()}. We are the controller of the personal data described here. For anything about privacy, email{" "}
            <a href={`mailto:${COMPANY.privacy}`}>{COMPANY.privacy}</a>.
          </p>

          <h2 id="ai">We don't train AI on your data</h2>
          <p>
            <strong>We never use your games, projects, files, scripts, messages, comments or any other data to train AI or machine learning
            models</strong>, and we don't sell, license or give it to anyone who would. The providers listed below only process it to run YHDE
            for us. Your game is yours.
          </p>

          <h2 id="what">What we collect</h2>
          <ul>
            <li>
              <strong>Your account</strong>: name, email address, and either a password (stored only as an Argon2id hash) or the GitHub or Google
              account you sign in with. From GitHub or Google we get your name, email address and an account number, nothing else.
            </li>
            <li>
              <strong>Your name in projects</strong>, which your teammates see next to your cursor, in chat and in the activity list.
            </li>
            <li>
              <strong>Your projects</strong>: scenes, scripts, files, the project image, chat messages, comments and the history of changes, with who
              made each change and when.
            </li>
            <li>
              <strong>Sign-ins and connections</strong>: your signed-in browsers and editors (device name, when last used), when you connected to which
              project, and your add-on version.
            </li>
            <li>
              <strong>Technical data</strong>: IP addresses, used briefly to limit repeated tries at signing in and at codes, and in server logs.
            </li>
            <li>
              <strong>Early access sign-ups</strong>: the email address you leave, if you ask to hear when YHDE opens.
            </li>
            <li>
              <strong>Payments</strong>: none during the beta. When paid plans start, a payment provider will handle cards; we will see the plan,
              invoices and payment status, never your card number. This policy will be updated before that.
            </li>
          </ul>

          <h2 id="why">Why we use it, and the legal basis</h2>
          <ul>
            <li>
              <strong>To provide YHDE</strong> to you and your team (contract): syncing projects, showing who works where, keeping the history, and
              letting the owner manage the team.
            </li>
            <li>
              <strong>To keep YHDE secure and working</strong> (legitimate interest): limiting abuse, finding faults, and the audit log of important
              changes.
            </li>
            <li>
              <strong>To send the emails the service needs</strong> (contract): confirming your address, password resets, and team invitations.
            </li>
            <li>
              <strong>To tell you YHDE has opened</strong>, if you signed up for early access (consent). You can take that back at any time.
            </li>
            <li>
              <strong>Accounting records</strong> once payments start (legal obligation).
            </li>
          </ul>
          <p>We don't use your data for advertising or to train AI, we don't build profiles, and we don't sell it.</p>

          <h2 id="where">Where it is stored</h2>
          <p>
            On our server at Hetzner Online in Helsinki, Finland, inside the EU. Every connection is encrypted (HTTPS and WSS). The database is
            backed up every day on the same provider, also in Finland. Your teammates' Godot projects also hold copies of the project files, on their
            own computers.
          </p>

          <h2 id="sharing">Who else sees it</h2>
          <ul>
            <li>People in a project with you see your name and what you work on there.</li>
            <li>A project's owner sees who is in it, the invitations waiting and when people were last online.</li>
            <li>A few of our staff can see accounts and teams on our admin page to help with problems. Their actions are logged.</li>
            <li>
              Providers that process data for us under data processing agreements: Hetzner (hosting, Finland) and Resend (sending email). If you sign in
              with GitHub or Google, they know you used YHDE; their own privacy policies apply to that.
            </li>
            <li>Nobody else, unless the law requires it.</li>
          </ul>
          <p>
            Resend and, if you use them, GitHub and Google are US companies. Transfers to them rely on the EU-US Data Privacy Framework or the EU's
            standard contractual clauses.
          </p>

          <h2 id="how-long">How long we keep it</h2>
          <ul>
            <li>Projects: until the team owner or an admin deletes them. Deleted projects leave the backups within 14 days.</li>
            <li>
              Your account: until you delete it. Your account details, sign-ins and early-access sign-up are then deleted at once. Changes, chat
              messages and comments you made in a team's projects stay there under the name you used, so the project still makes sense to the team.
            </li>
            <li>Sign-ins: a website sign-in lasts 30 days, an editor sign-in 90 days, or until you sign out.</li>
            <li>Server logs: only until they fill up and roll over, usually a few days.</li>
            <li>Early access sign-ups: until YHDE opens, or until you <a href="#your-data">remove yours</a>.</li>
            <li>Accounting records, once payments start: as long as Finnish law requires (usually 6 years).</li>
          </ul>
          <p>When the beta ends, we tell team owners at least 30 days before anything about their data changes.</p>

          <h2 id="cookies">Cookies</h2>
          <p>
            We only use cookies the site needs to work, such as keeping you signed in, and no tracking, analytics or advertising cookies. The full
            list is on the <a href={href("/cookies")}>cookies page</a>.
          </p>

          <h2 id="rights">Your rights under the GDPR</h2>
          <ul>
            <li>
              <strong>See and take your data</strong> (access and portability): under Account, <strong>Download my data</strong> gives you your
              account, sign-ins, team, and the messages and comments you wrote, as a file.
            </li>
            <li>
              <strong>Correct it</strong>: change your name under Account. To change your email address, ask us.
            </li>
            <li>
              <strong>Delete it</strong>: delete your account yourself under Account, or ask us to.
            </li>
            <li>
              <strong>Object to or limit</strong> how we use it, and take back a consent you gave.
            </li>
          </ul>
          <p>
            For anything you can't do yourself, email <a href={`mailto:${COMPANY.privacy}`}>{COMPANY.privacy}</a>. We answer within a month. You can
            also complain to the Finnish Data Protection Ombudsman (<a href="https://tietosuoja.fi/en" rel="noreferrer">tietosuoja.fi</a>).
          </p>

          <h2 id="your-data">Do it yourself</h2>
          <p>Most of this takes one click, no email needed.</p>
          <YourData />

          <h2 id="children">Children</h2>
          <p>YHDE is not meant for children under 13, and they may not make an account. Between 13 and 18, ask a parent or guardian first.</p>

          <h2 id="changes">Changes</h2>
          <p>If we change this policy in a way that matters, we tell account holders by email before it takes effect.</p>
        </Prose>
      </WithToc>
    </>
  )
}

// The promise at the top of the page, in plain words.
function NoAiTraining() {
  return (
    <div className="mt-6 flex max-w-[44rem] gap-4 rounded-2xl bg-tint-green px-5 py-4">
      <ShieldCheck className="mt-0.5 size-6 shrink-0 text-good" aria-hidden="true" />
      <div>
        <p className="font-bold text-text">Your game is never used to train AI.</p>
        <p className="mt-1 text-sm leading-relaxed text-text/80">
          Not your scenes, scripts, art, files, chat or comments. We don't do it, and we don't let anyone else. Nothing is sold, and there's no
          tracking.
        </p>
      </div>
    </div>
  )
}

// The rights people can use on their own, each one step away.
function YourData() {
  const actions = [
    { icon: Download, title: "Download your data", body: "Everything about your account, and the messages and comments you wrote, as a file.", to: "/app#/account", go: "Open Account" },
    { icon: UserX, title: "Delete your account", body: "Under Account, at the bottom. Your account details are gone at once.", to: "/app#/account", go: "Open Account" },
  ]
  return (
    <div className="mt-5 grid gap-3">
      {actions.map((a) => (
        <div key={a.title} className="flex flex-wrap items-center gap-4 rounded-2xl bg-card px-5 py-4">
          <a.icon className="size-5 shrink-0 text-sky" aria-hidden="true" />
          <div className="min-w-0 flex-1 basis-60">
            <p className="!mt-0 font-semibold text-text">{a.title}</p>
            <p className="!mt-0 text-sm text-dim">{a.body}</p>
          </div>
          <Button variant="secondary" size="sm" asChild>
            <a href={href(a.to)} className="!text-text hover:!no-underline">
              {a.go}
            </a>
          </Button>
        </div>
      ))}
      <LeaveList />
    </div>
  )
}

function LeaveList() {
  const [email, setEmail] = useState("")
  const [state, setState] = useState<"idle" | "busy" | "done">("idle")
  const [error, setError] = useState("")
  return (
    <div className="flex flex-wrap items-center gap-4 rounded-2xl bg-card px-5 py-4">
      <MailX className="size-5 shrink-0 text-sky" aria-hidden="true" />
      <div className="min-w-0 flex-1 basis-60">
        <p className="!mt-0 font-semibold text-text">Leave the early-access list</p>
        <p className="!mt-0 text-sm text-dim">We stop emailing you about YHDE opening and delete your address.</p>
      </div>
      {state === "done" ? (
        <p role="status" className="!mt-0 inline-flex items-center gap-2 text-sm font-semibold text-good">
          <Check className="size-4" aria-hidden="true" /> Removed
        </p>
      ) : (
        <form
          className="flex basis-full gap-2 sm:basis-auto"
          onSubmit={async (e) => {
            e.preventDefault()
            setError("")
            setState("busy")
            try {
              await leaveEarlyAccess(email.trim())
              setState("done")
            } catch (err) {
              setError((err as Error).message)
              setState("idle")
            }
          }}
        >
          <label htmlFor="leave-email" className="sr-only">
            Email address
          </label>
          <Input id="leave-email" type="email" required placeholder="you@studio.com" value={email} onChange={(e) => setEmail(e.target.value)} className="h-9 min-w-0 flex-1 sm:w-56" />
          <Button type="submit" size="sm" className="h-9" disabled={state === "busy"}>
            {state === "busy" ? "Removing…" : "Remove"}
          </Button>
        </form>
      )}
      {error ? (
        <p role="alert" className="!mt-0 basis-full text-sm text-bad">
          {error}
        </p>
      ) : null}
    </div>
  )
}

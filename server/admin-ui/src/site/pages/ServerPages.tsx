import { KeyRound, LogIn, Mail, Rocket } from "lucide-react"
import { Button } from "@/components/ui/button"
import { SITE, operatorName } from "@/world/site"
import { PoweredBy, href } from "../chrome"
import { PageTop, Prose } from "../parts"

// The pages of a self-hosted server (SiteMode.cs): its home, and the privacy,
// terms, cookies and contact pages, which belong to whoever runs the server,
// not to YHDE Development Organization.

const NOT_YHDE =
  "YHDE Development Organization makes the YHDE software. It does not run this server and cannot see or change anything on it."

export function ServerHome() {
  const name = SITE.operator?.name || "YHDE server"
  return (
    <>
      <PageTop
        title={name}
        lead={
          <>
            A YHDE server: the whole team works in the same Godot project at once, from the Godot editor. It is run by{" "}
            {operatorName()}.
          </>
        }
      >
        <div className="mt-8 flex flex-wrap gap-3">
          <Button size="lg" asChild>
            <a href={href("/app#/sign-in")}>
              <LogIn /> Sign in
            </a>
          </Button>
          <Button size="lg" variant="secondary" asChild>
            <a href={href("/app#/start")}>
              <Rocket /> Create an account
            </a>
          </Button>
          <Button size="lg" variant="secondary" asChild>
            <a href={href("/join")}>
              <KeyRound /> Join with a code
            </a>
          </Button>
        </div>
      </PageTop>
      <div className="mx-auto max-w-6xl px-5 pb-16 sm:px-7">
        <Prose>
          <p>
            New to YHDE? The <a href={href("/docs")}>getting started guide</a> shows how to install the add-on and invite
            your team.
          </p>
          <p>{NOT_YHDE}</p>
        </Prose>
        <PoweredBy className="mt-8" />
      </div>
    </>
  )
}

type Kind = "privacy" | "terms" | "cookies" | "contact"

const TITLES: Record<Kind, string> = { privacy: "Privacy", terms: "Terms", cookies: "Cookies", contact: "Contact" }

export function OperatorPage({ kind }: { kind: Kind }) {
  const op = SITE.operator
  const name = operatorName()
  const link = kind === "privacy" ? op?.privacyUrl : kind === "terms" ? op?.termsUrl : ""
  return (
    <>
      <PageTop title={TITLES[kind]} lead={<>This server is run by {name}. They decide how it is used and what happens to its data.</>} />
      <div className="mx-auto max-w-6xl px-5 pb-16 sm:px-7">
        <Prose>
          {kind === "privacy" ? (
            <p>
              {name} keeps the accounts, projects, files and messages on this server. Ask them what they store about you, or
              to delete it.
            </p>
          ) : null}
          {kind === "terms" ? <p>Using this server means using it on {name}&apos;s terms.</p> : null}
          {kind === "cookies" ? (
            <p>
              The YHDE software on this server only sets the cookies needed to keep you signed in: no tracking, analytics or
              ads.
            </p>
          ) : null}
          {link ? (
            <p>
              <a href={link} rel="noopener noreferrer">
                Read {name}&apos;s {kind === "privacy" ? "privacy policy" : "terms"}
              </a>
            </p>
          ) : null}
          {op?.email ? (
            <p>
              <a href={`mailto:${op.email}`} className="inline-flex items-center gap-2">
                <Mail className="size-4" aria-hidden="true" /> {op.email}
              </a>
            </p>
          ) : (
            <p>The people who run this server have not added their contact details here yet.</p>
          )}
          <p>{NOT_YHDE}</p>
        </Prose>
        <PoweredBy className="mt-8" />
      </div>
    </>
  )
}

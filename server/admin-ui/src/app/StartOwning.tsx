import { useState } from "react"
import { ArrowRight } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Panel } from "@/world/parts"
import { BETA, BETA_INVITES, BETA_PROJECTS } from "@/world/plans"
import { COMPANY } from "@/site/company"
import { OFFICIAL, SITE, operatorName } from "@/world/site"
import { run } from "./App"
import { api, useAppState } from "./store"

// For someone who can't make projects yet: during the beta that takes an
// access code from us. Being invited into other people's projects never does.
export function StartOwning() {
  const s = useAppState()
  const [code, setCode] = useState("")
  const [busy, setBusy] = useState(false)
  const needsCode = s.teamsNeedCode !== false

  return (
    <Panel title="Make your own projects">
      <form
        className="grid gap-5"
        onSubmit={async (e) => {
          e.preventDefault()
          setBusy(true)
          await run(() => api.startOwning(code), "You can make projects now")
          setBusy(false)
        }}
      >
        <div className="grid gap-1 rounded-2xl border-2 border-sky bg-tint-blue px-5 py-4">
          <span className="flex items-baseline justify-between gap-3">
            <span className="font-bold text-text">{OFFICIAL ? BETA.name : "Self-hosted"}</span>
            <span className="font-semibold text-sky">Free</span>
          </span>
          <span className="text-xs text-text/75">
            {OFFICIAL
              ? `Up to ${BETA_PROJECTS} projects · you and ${BETA_INVITES} invited people in each · paid plans come with YHDE 1.0`
              : `As many projects and people as this server can hold · run by ${operatorName()}`}
          </span>
        </div>
        {needsCode ? (
          <div className="grid gap-2">
            <Label htmlFor="own-code" className="label-dim">
              Access code
            </Label>
            <Input
              id="own-code"
              className="h-10 max-w-xs font-mono uppercase"
              value={code}
              maxLength={40}
              autoComplete="off"
              spellCheck={false}
              onChange={(e) => setCode(e.target.value)}
              required
            />
            {OFFICIAL || SITE.operator?.email ? (
              <p className="text-xs text-dim">
                {OFFICIAL ? "During the beta, making projects needs a code from us." : `Making projects on this server needs a code from ${operatorName()}.`}{" "}
                Don't have one? Ask at{" "}
                <a className="text-sky underline-offset-4 hover:underline" href={`mailto:${OFFICIAL ? COMPANY.email : SITE.operator?.email}`}>
                  {OFFICIAL ? COMPANY.email : SITE.operator?.email}
                </a>
                . Joining someone's project you're invited to needs no code.
              </p>
            ) : (
              <p className="text-xs text-dim">
                Making projects on this server needs a code from {operatorName()}. Joining someone's project you're invited to needs no code.
              </p>
            )}
          </div>
        ) : null}
        <Button type="submit" className="w-fit" disabled={busy || (needsCode && !code.trim())}>
          {busy ? "Checking…" : "Start making projects"} <ArrowRight aria-hidden="true" />
        </Button>
      </form>
    </Panel>
  )
}

import { useEffect, useState } from "react"
import { Check, MonitorSmartphone, X } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Wordmark } from "@/world/parts"
import type { Route } from "../App"
import { call as authCall, useMe } from "../auth"

// Approving a sign-in from the Godot editor (DeviceEndpoints.cs): Godot shows
// a code and opens this page; the person checks the code and allows it.
type Ask = { code: string; device: string; expires: string }

async function device<T>(method: "GET" | "POST", path: string, body?: unknown): Promise<T> {
  let res: Response
  try {
    res = await fetch("/api/device" + path, {
      method,
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-YHDE": "1" },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new Error("Can't reach the server. Check your connection and try again.")
  }
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error(data.detail || "Something went wrong. Try again.")
  return data as T
}

export function DevicePage({ route }: { route: Route }) {
  const me = useMe()
  const code = route.query.get("code") ?? ""
  const [ask, setAsk] = useState<Ask | null>(null)
  const [error, setError] = useState("")
  const [done, setDone] = useState<"allowed" | "denied" | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!code) {
      setError("This link has no sign-in code. Press Sign in in Godot again.")
      return
    }
    device<Ask>("GET", "/" + encodeURIComponent(code)).then(setAsk, (e) => setError((e as Error).message))
  }, [code])

  const answer = async (allow: boolean) => {
    setBusy(true)
    try {
      await device("POST", "/approve", { code, allow })
      setDone(allow ? "allowed" : "denied")
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-svh flex-col items-center px-5 py-8">
      <div className="flex w-full max-w-md items-center">
        <a href="/" aria-label="YHDE home">
          <Wordmark />
        </a>
        {me ? (
          <button
            type="button"
            className="ml-auto cursor-pointer text-sm text-dim hover:text-sky"
            onClick={async () => {
              await authCall("POST", "/logout").catch(() => {})
              location.reload()
            }}
          >
            Not {me.name}?
          </button>
        ) : null}
      </div>
      <main id="main" className="my-auto w-full max-w-md py-10">
        <div className="grid gap-6 rounded-[1.75rem] bg-card p-7 sm:p-9">
          {done === "allowed" ? (
            <>
              <span className="grid size-14 place-items-center rounded-2xl bg-tint-green text-good">
                <Check className="size-7" aria-hidden="true" />
              </span>
              <div>
                <h1 className="display text-[2rem] text-text">Godot is signed in</h1>
                <p className="mt-2 text-dim">Go back to Godot: your projects are in the YHDE panel. You can close this tab.</p>
              </div>
            </>
          ) : done === "denied" ? (
            <>
              <span className="grid size-14 place-items-center rounded-2xl bg-tint-rust text-bad">
                <X className="size-7" aria-hidden="true" />
              </span>
              <div>
                <h1 className="display text-[2rem] text-text">Not allowed</h1>
                <p className="mt-2 text-dim">Nothing was signed in. If this wasn't you, you don't need to do anything else.</p>
              </div>
            </>
          ) : error ? (
            <div>
              <h1 className="display text-[2rem] text-text">That didn't work</h1>
              <p role="alert" className="mt-3 rounded-xl bg-tint-rust px-4 py-3 text-sm text-[#ffd2c4]">
                {error}
              </p>
            </div>
          ) : !ask ? (
            <p className="text-dim" aria-busy="true">
              Checking the code…
            </p>
          ) : (
            <>
              <span className="grid size-14 place-items-center rounded-2xl bg-tint-blue text-sky">
                <MonitorSmartphone className="size-7" aria-hidden="true" />
              </span>
              <div>
                <h1 className="display text-[2rem] text-text">Sign in to YHDE in Godot?</h1>
                <p className="mt-2 text-dim">
                  <span className="text-text">{ask.device}</span> wants to use your account, {me?.name}. Only allow it if you just pressed Sign in in Godot.
                </p>
              </div>
              <div className="rounded-2xl bg-bg px-5 py-4 text-center">
                <p className="text-xs text-dim">Check that Godot shows the same code</p>
                <p className="mt-1 font-mono text-3xl font-bold tracking-[0.12em] text-text">{ask.code}</p>
              </div>
              <div className="flex flex-wrap gap-3">
                <Button size="lg" className="flex-1" disabled={busy} onClick={() => answer(true)}>
                  Allow
                </Button>
                <Button size="lg" variant="secondary" disabled={busy} onClick={() => answer(false)}>
                  Don't allow
                </Button>
              </div>
            </>
          )}
        </div>
      </main>
    </div>
  )
}

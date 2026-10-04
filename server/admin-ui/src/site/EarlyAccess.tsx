import { useState } from "react"
import { Check } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { cn } from "@/lib/utils"
import { joinEarlyAccess } from "./api"
import { href } from "./chrome"

const TEXT = {
  done: "You're on the list. We'll email you when there's news.",
  bad: "That email address doesn't look right.",
  label: "Email address",
  placeholder: "you@studio.com",
  busy: "Adding…",
  go: "Notify me",
  note: "Only news about YHDE, nothing else. See our",
  privacy: "privacy policy",
}

// Leaves an email address on the early-access list.
export function EarlyAccess({ source, className }: { source: string; className?: string }) {
  const t = TEXT
  const [email, setEmail] = useState("")
  const [state, setState] = useState<"idle" | "busy" | "done">("idle")
  const [error, setError] = useState("")

  if (state === "done")
    return (
      <p role="status" className={cn("inline-flex items-center gap-2.5 rounded-full bg-tint-green px-5 py-3 font-semibold text-good", className)}>
        <Check className="size-5" aria-hidden="true" /> {t.done}
      </p>
    )

  return (
    <form
      className={cn("grid w-full max-w-md gap-2", className)}
      onSubmit={async (e) => {
        e.preventDefault()
        setError("")
        if (!/^\S+@\S+\.\S+$/.test(email.trim())) return setError(t.bad)
        setState("busy")
        try {
          await joinEarlyAccess(email.trim(), source)
          setState("done")
        } catch (err) {
          setError((err as Error).message)
          setState("idle")
        }
      }}
    >
      <div className="flex gap-2 rounded-full bg-bg/70 p-1.5">
        <label htmlFor={`ea-${source}`} className="sr-only">
          {t.label}
        </label>
        <Input
          id={`ea-${source}`}
          type="email"
          autoComplete="email"
          placeholder={t.placeholder}
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? `ea-${source}-error` : undefined}
          className="h-10 flex-1 rounded-full border-0 bg-transparent px-4"
        />
        <Button type="submit" disabled={state === "busy"}>
          {state === "busy" ? t.busy : t.go}
        </Button>
      </div>
      {error ? (
        <p id={`ea-${source}-error`} role="alert" className="px-4 text-sm text-bad">
          {error}
        </p>
      ) : null}
      <p className="px-4 text-xs text-text/70">
        {t.note}{" "}
        <a href={href("/privacy")} className="font-semibold text-sky hover:underline">
          {t.privacy}
        </a>
        .
      </p>
    </form>
  )
}

import { Wrench } from "lucide-react"
import { Wordmark } from "@/world/parts"

const TEXT = {
  a: "Back ",
  b: "soon",
  fallback: "We're updating YHDE. This usually takes a few minutes.",
  editor: "If you're editing in Godot, keep going. Your changes are kept and sent when we're back.",
}

// Shown instead of every public page while maintenance mode is on.
export function Maintenance({ message }: { message: string }) {
  const t = TEXT
  return (
    <main id="main" className="grid min-h-svh place-items-center px-5 py-10">
      <div className="grid max-w-xl justify-items-center gap-5 text-center">
        <Wordmark />
        <span className="mt-6 grid size-16 place-items-center rounded-3xl bg-tint-blue text-sky">
          <Wrench className="size-8" aria-hidden="true" />
        </span>
        <h1 className="display text-[clamp(2.5rem,7vw,4rem)] text-text">
          {t.a}
          <span className="text-sky">{t.b}</span>
        </h1>
        <p className="text-lg leading-relaxed text-dim">{message || t.fallback}</p>
        <p className="text-sm text-dim">{t.editor}</p>
      </div>
    </main>
  )
}

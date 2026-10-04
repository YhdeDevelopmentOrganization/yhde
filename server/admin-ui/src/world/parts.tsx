import { useId, type ReactNode } from "react"
import { cn } from "@/lib/utils"
import { peerColor } from "./people"

// Shared building blocks for every page.

// A rounded card with an optional title row.
export function Panel({
  title,
  meta,
  action,
  children,
  className,
  bodyClassName,
  as: As = "section",
}: {
  title?: ReactNode
  meta?: ReactNode
  action?: ReactNode
  children: ReactNode
  className?: string
  bodyClassName?: string
  as?: "section" | "div" | "article"
}) {
  return (
    <As className={cn("flex min-w-0 flex-col rounded-[1.25rem] bg-card", className)}>
      {title || action || meta ? (
        <header className="flex min-h-12 flex-wrap items-baseline gap-x-3 gap-y-1 px-5 pt-5">
          {title ? <h2 className="min-w-0 truncate text-base font-semibold text-text">{title}</h2> : null}
          {meta ? <div className="min-w-0 truncate text-sm text-dim">{meta}</div> : null}
          {action ? <div className="ml-auto flex shrink-0 items-center gap-2 self-center">{action}</div> : null}
        </header>
      ) : null}
      <div className={cn("min-w-0 flex-1 p-5", bodyClassName)}>{children}</div>
    </As>
  )
}

// A rounded bar for a value from 0 to 1.
export function Meter({ value, className, color = "var(--accent)" }: { value: number; className?: string; color?: string }) {
  const v = Math.max(0, Math.min(1, value))
  return (
    <div className={cn("h-2 overflow-hidden rounded-full bg-card-2", className)} role="meter" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(v * 100)}>
      <div className="h-full rounded-full transition-[width] duration-500" style={{ width: `${v * 100}%`, background: color }} />
    </div>
  )
}

// A label and value on one line.
export function Readout({ label, value, hint, className }: { label: ReactNode; value: ReactNode; hint?: ReactNode; className?: string }) {
  return (
    <div className={cn("flex items-baseline gap-3 border-b border-line py-3 last:border-b-0", className)}>
      <span className="shrink-0 text-sm text-dim">{label}</span>
      {hint ? <span className="min-w-0 truncate text-xs text-dim">{hint}</span> : null}
      <span className="tabular ml-auto text-right font-semibold text-text">{value}</span>
    </div>
  )
}

// A headline number.
export function Figure({ label, value, hint, tone, className }: { label: ReactNode; value: ReactNode; hint?: ReactNode; tone?: "green" | "blue"; className?: string }) {
  return (
    <div className={cn("flex min-w-0 flex-col gap-1 rounded-[1.25rem] px-5 py-4", tone === "green" ? "bg-tint-green" : tone === "blue" ? "bg-tint-blue" : "bg-card", className)}>
      <span className={cn("truncate text-sm font-medium", tone === "green" ? "text-good" : tone === "blue" ? "text-sky" : "text-dim")}>{label}</span>
      <span className="tabular text-[1.875rem] leading-tight font-bold tracking-[-0.03em] text-text">{value}</span>
      {hint ? <span className={cn("truncate text-xs", tone === "green" ? "text-good/80" : tone === "blue" ? "text-sky/80" : "text-dim")}>{hint}</span> : null}
    </div>
  )
}

export function Wordmark({ className, sub }: { className?: string; sub?: ReactNode }) {
  return (
    <span className={cn("inline-flex items-center gap-2.5", className)}>
      <Mark />
      <span className="text-lg font-extrabold tracking-[-0.02em] text-text">yhde</span>
      {sub ? <span className="text-sm font-medium text-dim">{sub}</span> : null}
    </span>
  )
}

// The mark (the YHDE logo): a planet, the shared project, with a ring and
// two teammates' cursors around it. The thin gaps between the parts are cut
// out with masks, so the mark sits on any background.
const MARK = {
  ringBack: "M8.628 41.234a27 8.5-20 0 1 50.744-18.469",
  ringFront: "M59.372 22.765a27 8.5-20 0 1-50.744 18.47",
  ringGap: "M58.223 27.006a27 8.5-20 0 1-45.989 16.738",
  green: "M28.5 36v19.55l5.175-4.83 3.565 7.82 3.45-1.495-3.45-7.705h6.785Z",
  coral: "M58 25V5.45l-5.175 4.83-3.565-7.82-3.45 1.495 3.45 7.705h-6.785Z",
}

export function Mark({ className }: { className?: string }) {
  const id = "mark" + useId().replace(/[^a-zA-Z0-9]/g, "")
  // A cursor's own shape plus its outline, as a hole in what lies under it.
  const cursorHole = (d: string) => <path d={d} fill="#000" stroke="#000" strokeWidth="4.2" strokeLinejoin="round" />
  const gapHole = <path d={MARK.ringGap} fill="none" stroke="#000" strokeWidth="7.5" />
  return (
    <svg viewBox="3.5 0 61 61" className={cn("size-8 shrink-0", className)} aria-hidden="true">
      <defs>
        <mask id={`${id}-a`} maskUnits="userSpaceOnUse" x="0" y="-4" width="68" height="68">
          <rect x="0" y="-4" width="68" height="68" fill="#fff" />
          {cursorHole(MARK.green)}
          {gapHole}
          {cursorHole(MARK.coral)}
        </mask>
        <mask id={`${id}-b`} maskUnits="userSpaceOnUse" x="0" y="-4" width="68" height="68">
          <rect x="0" y="-4" width="68" height="68" fill="#fff" />
          {gapHole}
          {cursorHole(MARK.coral)}
        </mask>
        <mask id={`${id}-c`} maskUnits="userSpaceOnUse" x="0" y="-4" width="68" height="68">
          <rect x="0" y="-4" width="68" height="68" fill="#fff" />
          {cursorHole(MARK.coral)}
        </mask>
      </defs>
      <g mask={`url(#${id}-a)`}>
        <path d={MARK.ringBack} fill="none" stroke="var(--text)" strokeWidth="3.5" strokeLinecap="round" />
        <circle cx="34" cy="32" r="14" fill="var(--accent)" />
      </g>
      <path d={MARK.green} mask={`url(#${id}-b)`} fill="#5ee6a8" stroke="#5ee6a8" strokeWidth="1.5" strokeLinejoin="round" />
      <path d={MARK.ringFront} mask={`url(#${id}-c)`} fill="none" stroke="var(--text)" strokeWidth="3.5" strokeLinecap="round" />
      <path d={MARK.coral} fill="#ff8a65" stroke="#ff8a65" strokeWidth="1.5" strokeLinejoin="round" />
    </svg>
  )
}

// A person: a rounded square in their color with their initial.
export function Peer({ name, color, size = "md" }: { name: string; color?: string; size?: "sm" | "md" | "lg" }) {
  const c = color ?? peerColor(name)
  const s = size === "sm" ? "size-6 rounded-lg text-[0.6875rem]" : size === "lg" ? "size-10 rounded-xl text-sm" : "size-8 rounded-[0.6rem] text-xs"
  return (
    <span title={name} className={cn("inline-grid shrink-0 place-items-center font-bold text-[#141416]", s)} style={{ background: c }}>
      {name.trim().charAt(0).toUpperCase() || "?"}
    </span>
  )
}

export function PeerStack({ names, max = 4 }: { names: string[]; max?: number }) {
  const shown = names.slice(0, max)
  return (
    <span className="inline-flex items-center">
      {shown.map((n, i) => (
        <span key={n + i} className={cn("rounded-[0.6rem] ring-2 ring-card", i > 0 && "-ml-2")}>
          <Peer name={n} size="sm" />
        </span>
      ))}
      {names.length > max ? <span className="ml-1.5 text-xs text-dim">+{names.length - max}</span> : null}
    </span>
  )
}

export function LiveTag({ children = "Online" }: { children?: ReactNode }) {
  return (
    <span className="inline-flex items-center rounded-full bg-tint-green px-2.5 py-0.5 text-xs font-semibold text-good">
      {children}
    </span>
  )
}

// An empty state that says what to do next.
export function EmptyState({ title, children, action }: { title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="grid justify-items-start gap-2 rounded-[1.25rem] border border-dashed border-line px-6 py-8">
      <p className="font-semibold text-text">{title}</p>
      {children ? <p className="max-w-prose text-sm text-dim">{children}</p> : null}
      {action ? <div className="mt-2">{action}</div> : null}
    </div>
  )
}

// Simple vertical bars for a row of numbers (for example changes per day).
export function Bars({ values, label, height = 140, labels }: { values: number[]; label: string; height?: number; labels?: [string, string, string] }) {
  const max = Math.max(1, ...values)
  return (
    <figure className="grid gap-2">
      <div role="img" aria-label={label} className="flex items-end gap-[3px] select-none" style={{ height }}>
        {values.map((v, i) => (
          <div
            key={i}
            title={String(v)}
            className="flex-1 rounded-t-[5px] rounded-b-[2px] bg-sky transition-[height,filter] duration-300 hover:brightness-125"
            style={{ height: `${Math.max(v > 0 ? 3 : 1.5, (v / max) * 100)}%`, opacity: v > 0 ? 1 : 0.25 }}
          />
        ))}
      </div>
      {labels ? (
        <figcaption className="flex justify-between text-xs text-dim">
          <span>{labels[0]}</span>
          <span>{labels[1]}</span>
          <span>{labels[2]}</span>
        </figcaption>
      ) : null}
    </figure>
  )
}

// A segmented choice (Monthly / Yearly and the like).
export function Segmented<T extends string>({ value, onChange, options, label }: { value: T; onChange: (v: T) => void; options: { value: T; label: string }[]; label: string }) {
  return (
    <div className="inline-flex rounded-full bg-card-2 p-1" role="group" aria-label={label}>
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          aria-pressed={value === o.value}
          onClick={() => onChange(o.value)}
          className={cn(
            "cursor-pointer rounded-full px-4 py-1.5 text-sm font-semibold transition-colors",
            value === o.value ? "bg-sky text-sky-ink" : "text-dim hover:text-text",
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

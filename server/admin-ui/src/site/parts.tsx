import type { ReactNode } from "react"
import { cn } from "@/lib/utils"

// Layout pieces for the content pages.

export function PageTop({ title, accent, lead, children }: { title: string; accent?: string; lead?: ReactNode; children?: ReactNode }) {
  return (
    <div className="mx-auto max-w-6xl px-5 pt-14 pb-10 sm:px-7 sm:pt-20">
      <h1 className="display max-w-[18ch] text-[clamp(2.5rem,6vw,4.25rem)] text-text">
        {title} {accent ? <span className="text-sky">{accent}</span> : null}
      </h1>
      {lead ? <div className="mt-5 max-w-[42rem] text-lg leading-relaxed text-dim">{lead}</div> : null}
      {children}
    </div>
  )
}

// Readable long text: headings, paragraphs, lists.
export function Prose({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div
      className={cn(
        "max-w-[44rem] text-[1.0625rem] leading-[1.75] text-text/85",
        "[&_h2]:mt-12 [&_h2]:scroll-mt-24 [&_h2]:text-2xl [&_h2]:font-bold [&_h2]:tracking-[-0.02em] [&_h2]:text-text [&_h2:first-child]:mt-0",
        "[&_h3]:mt-8 [&_h3]:text-lg [&_h3]:font-bold [&_h3]:text-text",
        "[&_p]:mt-4 [&_ul]:mt-4 [&_ul]:grid [&_ul]:gap-2 [&_ul]:pl-5 [&_ul]:list-disc [&_ul]:marker:text-sky [&_ol]:mt-4 [&_ol]:grid [&_ol]:gap-2 [&_ol]:pl-5 [&_ol]:list-decimal [&_ol]:marker:text-sky [&_ol]:marker:font-bold",
        "[&_a]:font-semibold [&_a]:text-sky hover:[&_a]:underline [&_strong]:text-text [&_code]:rounded-md [&_code]:bg-card-2 [&_code]:px-1.5 [&_code]:py-0.5 [&_code]:text-[0.9em]",
        className,
      )}
    >
      {children}
    </div>
  )
}

// A draft notice for texts that still need a real check.
export function Draft({ children }: { children: ReactNode }) {
  return <div className="mt-6 max-w-[44rem] rounded-2xl bg-tint-rust px-5 py-4 text-sm leading-relaxed text-[#ffd2c4]">{children}</div>
}

// Scrolls to a section without swapping the page's own address part.
function jump(e: React.MouseEvent, id: string) {
  e.preventDefault()
  document.getElementById(id)?.scrollIntoView({ behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" })
  if (!location.pathname.startsWith("/ui/")) history.replaceState(null, "", "#" + id)
}

// A left-hand table of contents with the body next to it.
export function WithToc({ toc, children }: { toc: { id: string; label: string }[]; children: ReactNode }) {
  return (
    <div className="mx-auto grid max-w-6xl gap-10 px-5 pb-16 sm:px-7 lg:grid-cols-[14rem_1fr] lg:gap-16">
      <nav aria-label="On this page" className="hidden lg:block">
        <ul className="sticky top-24 grid gap-1">
          {toc.map((t) => (
            <li key={t.id}>
              <a href={`#${t.id}`} onClick={(e) => jump(e, t.id)} className="block rounded-xl px-3 py-2 text-sm font-medium text-dim transition-colors hover:bg-card hover:text-text">
                {t.label}
              </a>
            </li>
          ))}
        </ul>
      </nav>
      <div className="min-w-0">{children}</div>
    </div>
  )
}

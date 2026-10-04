import { ArrowLeft, BookOpen, LifeBuoy } from "lucide-react"
import { Button } from "@/components/ui/button"
import { href } from "../chrome"

const TEXT = {
  title: "This page isn't here.",
  body: "The link may be old, or there's a typo in the address. If you came here from an invite, ask for a new link.",
  home: "Back to the home page",
  docs: "Getting started",
  contact: "Contact us",
}

export function NotFound() {
  const t = TEXT
  return (
    <div className="mx-auto grid max-w-3xl justify-items-center px-5 py-24 text-center sm:py-32">
      <p className="display text-[clamp(6rem,20vw,11rem)] leading-none text-sky" aria-hidden="true">
        404
      </p>
      <h1 className="display mt-4 text-[clamp(2rem,5vw,3rem)] text-text">{t.title}</h1>
      <p className="mt-4 max-w-[34rem] text-lg text-dim">{t.body}</p>
      <div className="mt-9 flex flex-wrap justify-center gap-3">
        <Button size="lg" asChild>
          <a href={href("/")}>
            <ArrowLeft aria-hidden="true" /> {t.home}
          </a>
        </Button>
        <Button size="lg" variant="secondary" asChild>
          <a href={href("/docs")}>
            <BookOpen aria-hidden="true" /> {t.docs}
          </a>
        </Button>
        <Button size="lg" variant="ghost" asChild>
          <a href={href("/contact")}>
            <LifeBuoy aria-hidden="true" /> {t.contact}
          </a>
        </Button>
      </div>
    </div>
  )
}

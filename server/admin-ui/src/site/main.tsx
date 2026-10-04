import { StrictMode, useEffect, useState, type ReactNode } from "react"
import { createRoot } from "react-dom/client"
import "@/index.css"
import { SiteFooter, SiteHeader } from "./chrome"
import { NotFound } from "./pages/NotFound"
import { Privacy } from "./pages/Privacy"
import { Terms } from "./pages/Terms"
import { Security } from "./pages/Security"
import { Cookies } from "./pages/Cookies"
import { Contact } from "./pages/Contact"
import { Status } from "./pages/Status"
import { Changelog } from "./pages/Changelog"
import { Docs } from "./pages/Docs"
import { News } from "./pages/News"
import { Maintenance } from "./pages/Maintenance"
import { OperatorPage, ServerHome } from "./pages/ServerPages"
import { OFFICIAL, SITE } from "@/world/site"

// The public content pages and the 404. The server serves this one file for
// /privacy, /terms, /security, /cookies, /contact, /status, /changelog, /docs and any unknown path
// (with status 404); the page picks what to show from the address. On the
// Vite dev server the page name comes after the # instead.
const PAGES: Record<string, { title: string; view: () => ReactNode }> = {
  privacy: { title: "Privacy", view: Privacy },
  terms: { title: "Terms", view: Terms },
  security: { title: "Security", view: Security },
  cookies: { title: "Cookies", view: Cookies },
  contact: { title: "Contact", view: Contact },
  status: { title: "Status", view: Status },
  changelog: { title: "What's new", view: Changelog },
  docs: { title: "Getting started", view: Docs },
  news: { title: "News", view: News },
}

// A self-hosted server: its own home on "/", and the operator's privacy,
// terms, cookies and contact pages instead of YHDE's (SiteMode.cs).
if (!OFFICIAL) {
  PAGES[""] = { title: SITE.operator?.name || "YHDE server", view: ServerHome }
  for (const kind of ["privacy", "terms", "cookies", "contact"] as const)
    PAGES[kind] = { title: PAGES[kind].title, view: () => <OperatorPage kind={kind} /> }
}
const SITE_NAME = OFFICIAL ? "YHDE" : SITE.operator?.name || "YHDE server"

function current() {
  const dev = location.pathname.startsWith("/ui/")
  const raw = dev ? location.hash.replace(/^#\/?/, "").split("#")[0] : location.pathname.replace(/^\/|\/$/g, "")
  return raw.split("/")[0].toLowerCase()
}

// Set by the server while maintenance mode is on.
const pageData: { maintenance?: string } = JSON.parse(document.getElementById("page-data")?.textContent || "{}")

function Site() {
  const [page, setPage] = useState(current)
  useEffect(() => {
    const on = () => setPage(current())
    addEventListener("hashchange", on)
    return () => removeEventListener("hashchange", on)
  }, [])
  const entry = PAGES[page]
  useEffect(() => {
    document.title =
      pageData.maintenance !== undefined
        ? `Back soon · ${SITE_NAME}`
        : entry
          ? page === "" ? SITE_NAME : `${entry.title} · ${SITE_NAME}`
          : `Page not found · ${SITE_NAME}`
  }, [entry])

  // Jump to #section links inside a page once it is drawn.
  useEffect(() => {
    const id = location.hash.split("#").pop()
    if (id && !id.startsWith("/")) document.getElementById(id)?.scrollIntoView()
  }, [page])

  if (pageData.maintenance !== undefined) return <Maintenance message={pageData.maintenance} />
  const View = entry?.view ?? NotFound
  return (
    <div className="flex min-h-svh flex-col overflow-x-clip">
      <SiteHeader />
      <main id="main" tabIndex={-1} className="flex-1 animate-in fade-in-0 duration-300 outline-none">
        <View />
      </main>
      <SiteFooter />
    </div>
  )
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Site />
  </StrictMode>,
)

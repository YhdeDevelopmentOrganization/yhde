// Is this YHDE's own site or a self-hosted server, and who runs it? The
// server writes it into every page (SiteMode.cs, the "site-mode" block).
// Self-hosted servers show their operator instead of YHDE's company, and have
// no plans, prices or early access.

export type SiteOperator = { name: string; email: string; privacyUrl: string; termsUrl: string }
export type SiteMode = { official: boolean; operator: SiteOperator | null; poweredBy: string }

const raw = typeof document !== "undefined" ? document.getElementById("site-mode")?.textContent : null

// The Vite dev server has no server behind it: it shows the official site.
export const SITE: SiteMode = raw
  ? (JSON.parse(raw) as SiteMode)
  : { official: true, operator: null, poweredBy: "https://yhde.frostinteractive.fi" }

export const OFFICIAL = SITE.official

// The operator's name for running text ("run by …").
export const operatorName = () => SITE.operator?.name || "the people who run this server"

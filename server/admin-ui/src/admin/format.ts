export function bytes(n: number) {
  if (!n) return "0 B"
  const u = ["B", "KB", "MB", "GB", "TB"]
  const i = Math.min(u.length - 1, Math.floor(Math.log(n) / Math.log(1024)))
  return (n / Math.pow(1024, i)).toFixed(i ? 1 : 0) + " " + u[i]
}

export const num = (n: number) => Number(n || 0).toLocaleString()

export const plural = (n: number, one: string, many = one + "s") => `${num(n)} ${n === 1 ? one : many}`

export function ago(when: string | null | undefined) {
  if (!when) return "never"
  const s = (Date.now() - new Date(when).getTime()) / 1000
  if (s < 60) return "just now"
  if (s < 3600) return `${Math.floor(s / 60)} min ago`
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`
  return `${Math.floor(s / 86400)} d ago`
}

export function until(when: string) {
  const s = (new Date(when).getTime() - Date.now()) / 1000
  if (s < 3600) return `${Math.max(1, Math.round(s / 60))} min`
  if (s < 86400) return `${Math.round(s / 3600)} h`
  return `${Math.round(s / 86400)} d`
}

export function duration(seconds: number) {
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  return h ? `${h} h ${m} min` : `${m} min`
}

export const dateTime = (d: string) => new Date(d).toLocaleString([], { dateStyle: "medium", timeStyle: "short" })
export const shortDay = (iso: string) => new Date(iso + "T00:00:00Z").toLocaleDateString([], { day: "numeric", month: "short" })
export const shortSha = (sha?: string | null) => (sha ?? "").slice(0, 7)

// The last 30 days as YYYY-MM-DD (UTC), oldest first.
export function last30Days() {
  const out: string[] = []
  const now = new Date()
  const today = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate())
  for (let i = 29; i >= 0; i--) out.push(new Date(today - i * 86400000).toISOString().slice(0, 10))
  return out
}

// Chart series colors: the theme's chart colors.
export const seriesColor = (i: number) => `var(--chart-${(i % 5) + 1})`

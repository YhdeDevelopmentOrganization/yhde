// The public site's own API (SiteEndpoints.cs).

export type Post = {
  id: string
  kind: "news" | "release"
  title: string
  version: string
  summary: string
  body: string
  cover: string
  status: "draft" | "published"
  publishAt: string | null
  created: string
  updated: string
}
export type Announcement = { enabled: boolean; text: string; link: string; tone: "info" | "warning" }
export type SiteInfo = { announcement: Announcement | null; maintenance: { enabled: boolean; message: string } | null }

export async function getJson<T>(path: string): Promise<T> {
  const res = await fetch(path, { headers: { Accept: "application/json" } })
  if (!res.ok) throw new Error(res.statusText)
  return (await res.json()) as T
}

export const livePosts = (kind: "news" | "release") => getJson<Post[]>(`/api/posts?kind=${kind}`)

let siteInfo: Promise<SiteInfo> | null = null
export const getSiteInfo = () => (siteInfo ??= getJson<SiteInfo>("/api/site").catch(() => ({ announcement: null, maintenance: null })))

export async function joinEarlyAccess(email: string, source: string) {
  const res = await fetch("/api/early-access", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, source }),
  })
  if (!res.ok) {
    const data = await res.json().catch(() => ({}))
    throw new Error(data.detail || "That didn't work. Try again in a moment.")
  }
}

export async function leaveEarlyAccess(email: string) {
  const res = await fetch("/api/early-access/remove", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email }),
  })
  if (!res.ok) {
    const data = await res.json().catch(() => ({}))
    throw new Error(data.detail || "That didn't work. Try again in a moment.")
  }
}

export const postDate = (p: Pick<Post, "publishAt" | "created">) =>
  new Date(p.publishAt ?? p.created).toLocaleDateString("en-GB", { day: "numeric", month: "long", year: "numeric" })

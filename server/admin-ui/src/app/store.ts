import { useSyncExternalStore } from "react"
import { BETA, planById, type PlanId } from "@/world/plans"

// The dashboard's data, from the server (TeamEndpoints.cs): the signed-in
// person's projects (their own and the ones they were invited to), who is in
// each, and real statistics. Every change is a request; the server checks
// permissions and plan limits, and the dashboard reloads what it shows
// afterwards. It also refreshes every few seconds, so who is connected and
// new changes appear on their own.

export type Access = "edit" | "view"
export type Member = { id: string; name: string; email: string; role: "owner" | "member"; access: Access; joined: string; lastSeen: string | null }
// Invitations last 14 days; ones that ran out show (with Resend) but hold no seat.
export type PendingInvite = { id: string; email: string; sent: string; expires: string; expired: boolean }
export type Invitation = { id: string; projectId: string; project: string; from: string; fromEmail: string; to: string; sent: string }
// The invitation an email's link opens (GET /api/invitations/by-token/{token}).
export type TokenInvitation = { project: string; from: string; fromEmail: string; to: string; sent: string; signedInAs: string | null }
export type Link = { id: string; label: string; created: string; expires: string | null; maxUses: number | null; uses: number; revoked: boolean }
export type Activity = { id: string; at: string; who: string; whoId?: string | null; kind: string; path: string; text: string }
export type Project = {
  id: string
  name: string
  created: string
  archived: boolean
  files: number
  bytes: number
  changes: number
  daily: number[] // changes per day, last 30 days, oldest first
  activity: Activity[]
  image: string | null // address of the project's own image, if it has one
  role: "owner" | "member"
  myAccess: Access
  owner: { id: string; name: string } | null
  members: Member[] // the owner first
  peopleLimit: number // people besides the owner (members and open invitations)
  viewers: number // people the view links let in
  viewersLimit: number
  invites: PendingInvite[] // the owner's view only
  links: Link[] // the owner's view only
  joinCode: string | null // the owner's view only: anyone who enters it joins as a developer
}

// The picture for a project: its own, or the YHDE "No image yet" one
// (the YHDE project picture).
export const projectImage = (p: Pick<Project, "image">) => p.image ?? "/ui/media/project-default-wide.png"
export const MAX_IMAGE_BYTES = 2 * 1024 * 1024
export type Presence = { sessionId: string; name: string; projectId: string; scene: string; since: string }
export type TeamPromo = { text: string; redeemedAt: string; until: string | null }
// Someone who can make projects (they used an access code): their plan.
export type MyPlan = {
  id: PlanId
  period: "month" | "year"
  extraSeats: number
  extraStorage: number
  bonusSeats: number
  bonusStorageGb: number
  freeSeats: number
  promos: TeamPromo[]
  created: string
  maxProjects: number | null
  projects: number
  peoplePerProject: number
  storageBytes: number
  usedBytes: number
}
export type State = {
  user: { id: string; name: string; email: string; emailVerified: boolean }
  plan: MyPlan | null
  projects: Project[]
  invitations: Invitation[]
  presence: Presence[]
  teamsNeedCode?: boolean // making projects needs an access code
}

// What an operation did, in words (OperationType.cs).
const KINDS: Record<string, string> = {
  CreateNode: "Added a node",
  DeleteNode: "Deleted a node",
  MoveNode: "Moved a node",
  RenameNode: "Renamed a node",
  ReorderNode: "Reordered nodes",
  ChangeNodeType: "Changed a node's type",
  SetSceneRoot: "Changed the scene root",
  ChangeProperty: "Changed a property",
  AddResource: "Added a resource",
  RemoveResource: "Removed a resource",
  ChangeResourceProperty: "Changed a resource",
  RegisterAsset: "Added a file",
  UpdateAsset: "Updated a file",
  MoveAsset: "Moved a file",
  DeleteAsset: "Deleted a file",
  EditText: "Edited a script",
}
export const describe = (kind: string, path: string) => {
  const what = KINDS[kind] ?? kind.replace(/([a-z])([A-Z])/g, "$1 $2")
  return path ? `${what}: ${path}` : what
}

type Status = { state?: State; error?: string }
let status: Status = {}
const listeners = new Set<() => void>()
const set = (next: Status) => {
  status = next
  listeners.forEach((l) => l())
}

export class ApiError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

export async function request<T>(method: "GET" | "POST", path: string, body?: unknown, raw?: Blob, rawType = "application/zip"): Promise<T> {
  let res: Response
  try {
    res = await fetch("/api" + path, {
      method,
      credentials: "same-origin",
      headers: raw ? { "X-YHDE": "1", "Content-Type": rawType } : { "Content-Type": "application/json", "X-YHDE": "1" },
      body: raw ?? (body === undefined ? undefined : JSON.stringify(body)),
    })
  } catch {
    throw new ApiError("Can't reach the server. Check your connection and try again.", 0)
  }
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new ApiError(data.detail || "Something went wrong. Try again.", res.status)
  return data as T
}

type Raw = Omit<State, "projects"> & { projects: (Omit<Project, "activity"> & { activity: Omit<Activity, "text">[] })[] }

export async function refresh() {
  try {
    const raw = await request<Raw>("GET", "/dashboard")
    const state: State = {
      ...raw,
      projects: raw.projects.map((p) => ({ ...p, activity: p.activity.map((a) => ({ ...a, who: a.who || "Someone", text: describe(a.kind, a.path) })) })),
    }
    set({ state })
  } catch (e) {
    // Keep showing the last good data when a background refresh fails.
    set({ state: status.state, error: status.state ? undefined : (e as Error).message })
  }
}

export function useDashboard(): Status {
  return useSyncExternalStore(
    (l) => (listeners.add(l), () => void listeners.delete(l)),
    () => status,
  )
}

// The loaded state (the pages behind the dashboard shell).
export function useAppState(): State {
  const s = useDashboard().state
  if (!s) throw new Error("The dashboard is not loaded yet.")
  return s
}

export const owns = (p: Project) => p.role === "owner"

// Refreshes while the page is open and visible.
export function startRefresh() {
  refresh()
  const t = setInterval(() => {
    if (!document.hidden) refresh()
  }, 8000)
  const onShow = () => !document.hidden && refresh()
  document.addEventListener("visibilitychange", onShow)
  return () => {
    clearInterval(t)
    document.removeEventListener("visibilitychange", onShow)
  }
}

// The owner's usage against their plan, as the server enforces it. Warn from
// 80 % of storage on, before a file is refused.
export function planUsage(plan: MyPlan) {
  const info = planById(plan.id) ?? BETA
  const storageShare = plan.storageBytes ? plan.usedBytes / plan.storageBytes : 0
  return {
    info,
    storageShare,
    storageWarning: storageShare >= 0.8,
    storageFull: storageShare >= 1,
    projectsFull: plan.maxProjects !== null && plan.projects >= plan.maxProjects,
  }
}

// People in a project besides its owner: members and invitations still open.
export function projectSeats(p: Project) {
  const used = p.members.filter((m) => m.role === "member").length + p.invites.filter((i) => !i.expired).length
  return { used, free: Math.max(0, p.peopleLimit - used), limit: p.peopleLimit }
}

// Each change is a request, then a reload of what the dashboard shows.
const post = async <T = unknown>(path: string, body?: unknown) => {
  const r = await request<T>("POST", path, body ?? {})
  await refresh()
  return r
}

const P = (id: string) => `/team/projects/${id}`

export const api = {
  startOwning: (code: string) => post("/team", { code }),
  async createProject(name: string, zip?: File) {
    if (zip) {
      const n = name.trim() || zip.name.replace(/\.zip$/i, "")
      const r = await request<{ id: string }>("POST", `/team/projects/import?name=${encodeURIComponent(n)}`, undefined, zip)
      await refresh()
      return r.id
    }
    return (await post<{ id: string }>("/team/projects", { name })).id
  },
  renameProject: (id: string, name: string) => post(`${P(id)}/rename`, { name }),
  async setProjectImage(id: string, image: File) {
    if (image.size > MAX_IMAGE_BYTES) throw new ApiError("The image is too big. Use one under 2 MB.", 413)
    await request("POST", `${P(id)}/image`, undefined, image, image.type || "application/octet-stream")
    await refresh()
  },
  removeProjectImage: (id: string) => post(`${P(id)}/image/remove`),
  archiveProject: (id: string, archived: boolean) => post(`${P(id)}/archive`, { archived }),
  deleteProject: (id: string, typed: string) => post(`${P(id)}/delete`, { name: typed }),
  async createLink(projectId: string, input: { label: string; hours: number | null; maxUses: number }) {
    return (await post<{ url: string }>(`${P(projectId)}/links`, input)).url
  },
  revokeLink: (linkId: string) => post(`/team/links/${linkId}/revoke`),
  deleteLink: (linkId: string) => post(`/team/links/${linkId}/delete`),
  invite: (projectId: string, email: string) => post(`${P(projectId)}/invites`, { email }),
  resendInvite: (projectId: string, inviteId: string) => post(`${P(projectId)}/invites/${inviteId}/resend`),
  cancelInvite: (projectId: string, inviteId: string) => post(`${P(projectId)}/invites/${inviteId}/cancel`),
  makeJoinCode: (projectId: string) => post<{ code: string }>(`${P(projectId)}/join-code`),
  joinCodeOff: (projectId: string) => post(`${P(projectId)}/join-code/off`),
  joinWithCode: (code: string) => post<{ id: string; name: string; already: boolean }>("/team/join", { code }),
  removeMember: (projectId: string, userId: string) => post(`${P(projectId)}/members/${userId}/remove`),
  setAccess: (projectId: string, userId: string, access: Access) => post(`${P(projectId)}/members/${userId}/access`, { access }),
  leaveProject: (projectId: string) => post(`${P(projectId)}/leave`),
  transferProject: (projectId: string, userId: string) => post(`${P(projectId)}/transfer`, { userId }),
  activity: (projectId: string, who?: string | null) =>
    request<{ people: { id: string; name: string; changes: number }[]; activity: Omit<Activity, "text">[] }>(
      "GET",
      `${P(projectId)}/activity${who ? `?who=${encodeURIComponent(who)}` : ""}`,
    ),
  acceptInvitation: (id: string) => post(`/invitations/${id}/accept`),
  declineInvitation: (id: string) => post(`/invitations/${id}/decline`),
  invitationByToken: (token: string) => request<TokenInvitation>("GET", `/invitations/by-token/${encodeURIComponent(token)}`),
  acceptInvitationByToken: (token: string) => post(`/invitations/by-token/${encodeURIComponent(token)}/accept`),
  declineInvitationByToken: (token: string) => request("POST", `/invitations/by-token/${encodeURIComponent(token)}/decline`, {}),
  setExtraSeats: (n: number) => post("/team/seats", { count: n }),
  changePlan: (plan: PlanId, period: "month" | "year") => post("/team/plan", { plan, period }),
  setExtraStorage: (n: number) => post("/team/storage", { count: n }),
  redeemCode: (code: string) => post("/team/promo", { code }),
  async deleteAccount(email: string) {
    await request("POST", "/me/delete", { email })
    set({})
  },
}

export const exportUrl = "/api/me/export"

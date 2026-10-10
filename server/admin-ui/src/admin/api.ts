// The admin API (admin.md) and the shapes it returns.

export type Invite = { id: string; label: string; created: string; revoked: string | null; fromLink: string | null }
export type Link = {
  id: string
  label: string
  created: string
  expires: string | null
  maxUses: number | null
  uses: number
  revoked: string | null
}
export type Project = {
  id: string
  name: string
  created: string
  archived: string | null
  operations: number
  files: number
  fileBytes: number
  lastActivity: string | null
  online: number
  invites: Invite[]
  links: Link[]
  joinCode: string | null
}
export type OnlinePerson = { name: string; project: string; projectId: string; scene: string; tool: string; version: string; via: string }
export type Addon = { version: string; sha256: string; size: number; platforms: string[]; uploaded: string }
export type Overview = {
  version: string
  started: string
  projects: Project[]
  online: OnlinePerson[]
  addon: Addon | null
  addonPending: Addon | null
  backups: { configured: boolean; files: { name: string; bytes: number; at: string }[] }
}

export type Stats = {
  days: number
  activity: {
    daily: { day: string; project: string; n: number }[]
    heat: { dow: number; hour: number; n: number }[]
    kinds: { type: string; n: number }[]
    totals: { all: number; today: number; week: number }
  }
  people: {
    list: {
      id: string
      name: string
      sessions: number
      seconds: number
      lastSeen: string
      firstSeen: string
      version: string
      online: boolean
      changes: number
      chat: number
      comments: number
      projects: string[]
    }[]
    daily: { day: string; n: number }[]
  }
  storage: {
    stored: { files: number; bytes: number }
    database: number
    disk: { free: number; total: number; minFree: number }
    maxFile: number
    byProject: { project: string; files: number; bytes: number }[]
    byKind: { kind: string; files: number; bytes: number }[]
    biggest: { project: string; path: string; size: number }[]
  }
  health: {
    started: string
    connections: number
    editing: number
    memoryMb: number
    cpu: number
    requests: number
    errors: number
    slowDisconnects: number
    runtime: string
    os: string
    cores: number
    samples: { at: string; connections: number; editing: number; memoryMb: number; cpu: number; requests: number; errors: number }[]
  }
}

export type Commit = { sha: string; author: string; date: string; subject: string }
export type Updates = {
  configured: boolean
  requested?: string | null
  checkRequested?: boolean
  status?: {
    checkedAt: string
    branch: string
    current: Commit | null
    available: Commit[]
    updating: { target: string; startedAt: string } | null
    fetchError: string | null
    last: { ok: boolean; from: string; to: string; at: string; message: string } | null
  } | null
}

export class SignedOut extends Error {}

// Who is signed in on the admin page (AdminStaffEndpoints.cs): a staff
// account, or the server password (account: false).
export type Me = { name: string; email: string; role: "admin" | "support"; account: boolean }

export async function api<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch("/admin/api" + path, {
    method,
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", "X-YHDE-Admin": "1" },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (res.status === 401 && !path.startsWith("/login")) throw new SignedOut("Signed out")
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error(data.detail || data.title || res.statusText)
  return data as T
}

// Uploads a file with progress (fetch cannot report upload progress).
export function upload<T>(path: string, file: File, onProgress: (fraction: number) => void, contentType = "application/zip"): Promise<T> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open("POST", "/admin/api" + path)
    xhr.setRequestHeader("Content-Type", contentType)
    xhr.setRequestHeader("X-YHDE-Admin", "1")
    xhr.upload.onprogress = (e) => e.lengthComputable && onProgress(e.loaded / e.total)
    xhr.onload = () => {
      let data: { detail?: string; title?: string } = {}
      try {
        data = JSON.parse(xhr.responseText)
      } catch {
        /* not JSON */
      }
      if (xhr.status >= 200 && xhr.status < 300) resolve(data as T)
      else reject(new Error(data.detail || data.title || "The upload failed."))
    }
    xhr.onerror = () => reject(new Error("The upload failed (connection lost)."))
    xhr.send(file)
  })
}

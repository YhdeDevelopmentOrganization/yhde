import { useSyncExternalStore } from "react"

// The signed-in person, from the real account API (AuthEndpoints.cs).

export type Me = {
  id: string
  email: string
  name: string
  emailVerified: boolean
  hasPassword: boolean
  created: string
  providers: { provider: "github" | "google"; email: string; created: string }[]
}
export type Session = { id: string; kind: "web" | "editor"; device: string; ip: string; created: string; lastSeen: string; current: boolean }

export class AuthError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

export async function call<T>(method: "GET" | "POST", path: string, body?: unknown): Promise<T> {
  let res: Response
  try {
    res = await fetch("/api/auth" + path, {
      method,
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-YHDE": "1" },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new AuthError("Can't reach the server. Check your connection and try again.", 0)
  }
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new AuthError(data.detail || "Something went wrong. Try again.", res.status)
  return data as T
}

// The current person: undefined while loading, null when signed out.
let me: Me | null | undefined = undefined
const listeners = new Set<() => void>()
const set = (m: Me | null) => {
  me = m
  listeners.forEach((l) => l())
}

export async function loadMe() {
  try {
    set(await call<Me>("GET", "/me"))
  } catch (e) {
    set(null)
    if (e instanceof AuthError && e.status !== 401 && e.status !== 404) throw e
  }
}

export function useMe() {
  return useSyncExternalStore(
    (l) => (listeners.add(l), () => void listeners.delete(l)),
    () => me,
  )
}

export const auth = {
  providers: () => call<{ github: boolean; google: boolean }>("GET", "/providers").catch(() => ({ github: false, google: false })),
  register: (email: string, password: string, name: string) => call<{ checkEmail: boolean }>("POST", "/register", { email, password, name }),
  login: async (email: string, password: string) => set(await call<Me>("POST", "/login", { email, password })),
  logout: async () => {
    await call("POST", "/logout").catch(() => {})
    set(null)
  },
  // The link in the email confirms it and signs that account in here.
  verify: async (token: string) => {
    await call("POST", "/verify", { token })
    await loadMe()
  },
  resend: () => call("POST", "/verify/resend"),
  forgot: (email: string) => call("POST", "/forgot", { email }),
  reset: async (token: string, password: string) => set(await call<Me>("POST", "/reset", { token, password })),
  changePassword: (current: string, next: string) => call("POST", "/password", { current, next }),
  rename: async (name: string) => set(await call<Me>("POST", "/profile", { name })),
  sessions: () => call<Session[]>("GET", "/sessions"),
  endSession: (id: string) => call("POST", `/sessions/${id}/revoke`),
  unlink: async (provider: string) => {
    await call("POST", `/providers/${provider}/unlink`)
    await loadMe()
  },
}

// Starts GitHub or Google sign-in (a full page visit to the server).
export function providerUrl(provider: "github" | "google", opts: { link?: boolean; returnTo?: string } = {}) {
  const q = new URLSearchParams()
  if (opts.link) q.set("link", "true")
  q.set("return", opts.returnTo ?? "/app#/projects")
  return `/auth/${provider}?${q}`
}

// Where someone was when they started signing in or making an account (the
// page that approves Godot, say), so they come back to it afterwards, also
// from the link in the confirmation email. Only pages of this app.
const RETURN_KEY = "yhde.return"
export function rememberReturn(hash: string) {
  try {
    if (hash.startsWith("#/")) localStorage.setItem(RETURN_KEY, hash)
  } catch {
    /* storage blocked: they land on Projects instead */
  }
}
export function takeReturn(): string | null {
  try {
    const h = localStorage.getItem(RETURN_KEY)
    localStorage.removeItem(RETURN_KEY)
    return h && h.startsWith("#/") ? h : null
  } catch {
    return null
  }
}

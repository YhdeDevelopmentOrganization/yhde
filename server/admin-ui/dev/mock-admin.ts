import type { Plugin } from "vite"

// `npm run dev` only: answers /admin/api with sample data so the admin page
// can be worked on without a running YHDE server. Never part of a build.
export function mockAdmin(): Plugin {
  const now = Date.now()
  const iso = (msAgo: number) => new Date(now - msAgo).toISOString()
  const H = 3600000
  const D = 24 * H
  const day = (i: number) => new Date(now - i * D).toISOString().slice(0, 10)
  const projects = [
    { id: "11111111-1111-1111-1111-111111111111", name: "Skyward", created: iso(60 * D), archived: null, operations: 48210, files: 1284, fileBytes: 3.1e9, lastActivity: iso(60000), online: 2,
      invites: [{ id: "a1", label: "Maya · download 1", created: iso(58 * D), revoked: null, fromLink: "l1" }, { id: "a2", label: "Alex", created: iso(41 * D), revoked: null, fromLink: null }],
      links: [{ id: "l1", label: "Maya", created: iso(58 * D), expires: iso(-5 * D), maxUses: 5, uses: 1, revoked: null }, { id: "l2", label: "Old jam link", created: iso(90 * D), expires: iso(80 * D), maxUses: null, uses: 3, revoked: null }] },
    { id: "22222222-2222-2222-2222-222222222222", name: "Dungeon Tide", created: iso(35 * D), archived: null, operations: 12877, files: 612, fileBytes: 1.4e9, lastActivity: iso(26 * H), online: 0, invites: [], links: [] },
    { id: "33333333-3333-3333-3333-333333333333", name: "Jam: Tiny Tides", created: iso(120 * D), archived: iso(80 * D), operations: 5304, files: 214, fileBytes: 3.1e8, lastActivity: iso(90 * D), online: 0, invites: [], links: [] },
  ]
  const overview = {
    version: "0.3.0", started: iso(3 * D), projects,
    online: [
      { name: "Maya", project: "Skyward", projectId: projects[0].id, scene: "res://levels/level_03.tscn", tool: "", version: "0.3.0", via: "" },
      { name: "Alex", project: "Skyward", projectId: projects[0].id, scene: "res://scripts/player.gd", tool: "", version: "0.3.0", via: "" },
    ],
    addon: { version: "0.3.0", sha256: "ab", size: 743452, platforms: ["windows", "linux", "macos"], uploaded: iso(4 * D) },
    addonPending: { version: "0.3.1", sha256: "cd", size: 751002, platforms: ["windows", "linux", "macos"], uploaded: iso(2 * H) },
    backups: { configured: true, files: [0, 1, 2].map((i) => ({ name: `yhde-${day(i)}.sql.gz`, bytes: 4.2e7 - i * 1e6, at: iso(i * D + 3 * H) })) },
  }
  const daily = [] as { day: string; project: string; n: number }[]
  for (let i = 29; i >= 0; i--) {
    const wk = new Date(now - i * D).getDay()
    const f = wk === 0 || wk === 6 ? 0.4 : 1
    daily.push({ day: day(i), project: projects[0].id, n: Math.round(f * (300 + 200 * Math.sin(i))) })
    if (i % 3) daily.push({ day: day(i), project: projects[1].id, n: Math.round(f * (90 + 60 * Math.cos(i))) })
  }
  const heat = [] as { dow: number; hour: number; n: number }[]
  for (let h = 0; h < 24; h++) heat.push({ dow: 1, hour: h, n: Math.round(Math.max(0, Math.sin(((h - 6) / 24) * Math.PI * 2)) * 900 + (h > 17 ? 400 : 0)) })
  const people = ["Mika", "Maya", "Alex", "Sam"].map((name, i) => ({
    id: String(i), name, sessions: 40 - i * 7, seconds: (90 - i * 20) * 3600, lastSeen: iso(i < 3 ? 60000 : 3 * H), firstSeen: iso(60 * D),
    version: "0.3.0", online: i === 1 || i === 2, changes: 18000 - i * 4000, chat: 320 - i * 60, comments: 41 - i * 9, projects: i === 3 ? ["Skyward"] : ["Skyward", "Dungeon Tide"],
  }))
  const samples = Array.from({ length: 240 }, (_, i) => ({
    at: iso((240 - i) * 6 * 60000), connections: Math.round(2 + 2 * Math.sin(i / 20) + (i > 200 ? 1 : 0)), editing: 2,
    memoryMb: 180 + i * 0.1 + 8 * Math.sin(i / 9), cpu: Math.round((3 + 2 * Math.sin(i / 7)) * 10) / 10, requests: Math.round(40 + 20 * Math.sin(i / 11)), errors: 0,
  }))
  const stats = {
    days: 30,
    activity: { daily, heat, kinds: [["ChangeProperty", 21000], ["EditText", 9000], ["CreateNode", 3100], ["MoveNode", 1400], ["RegisterAsset", 1300], ["DeleteNode", 700], ["RenameNode", 420]].map(([type, n]) => ({ type, n })), totals: { all: 66391, today: 412, week: 3310 } },
    people: { list: people, daily: Array.from({ length: 30 }, (_, i) => ({ day: day(29 - i), n: 2 + (i % 3) })) },
    storage: {
      stored: { files: 2110, bytes: 4.8e9 }, database: 3.1e8, disk: { free: 1.6e10, total: 4e10 }, maxFile: 5.4e8,
      byProject: projects.map((p) => ({ project: p.id, files: p.files, bytes: p.fileBytes })),
      byKind: [["Images", 2.1e9, 900], ["Audio", 1.3e9, 210], ["3D models", 8e8, 120], ["Scenes", 2e8, 480], ["Scripts", 4e7, 400]].map(([kind, bytes, files]) => ({ kind, bytes, files })),
      biggest: [["res://audio/theme_full.ogg", 8.8e7], ["res://art/parallax_4k.png", 6.1e7], ["res://models/boss.glb", 4.2e7]].map(([path, size]) => ({ project: projects[0].id, path, size })),
    },
    health: { started: iso(3 * D), connections: 3, editing: 2, memoryMb: 204, cpu: 4.2, requests: 51230, errors: 2, runtime: ".NET 10", os: "Linux", cores: 2, samples },
  }
  const updates = {
    configured: true, requested: null, checkRequested: false,
    status: { checkedAt: iso(20 * 60000), branch: "main", current: { sha: "042c5ec48adf", author: "Mika", date: iso(D), subject: "Admin and join pages rebuilt" },
      available: [{ sha: "9f1e2d3c4b5a", author: "Mika", date: iso(2 * H), subject: "Home page, dashboard and a new look" }], updating: null, fetchError: null, last: { ok: true, from: "dcc1675", to: "042c5ec", at: iso(D), message: "" } },
  }
  type MockPost = { id: string; kind: string; title: string; version: string; summary: string; body: string; status: string; publishAt: string | null; created: string; updated: string }
  const posts: MockPost[] = [
    { id: "n1", kind: "news", title: "Accounts open in October", version: "", summary: "Sign up for early access and be among the first teams in.", body: "We're getting accounts and billing ready.\n\n## What happens next\n- Early access emails go out first\n- Teammates still join **free**", status: "published", publishAt: iso(2 * D), created: iso(3 * D), updated: iso(2 * D) },
    { id: "r1", kind: "release", title: "Sample scheduled release", version: "0.3.1", summary: "", body: "## Fixed\n- Sample text for the preview", status: "published", publishAt: iso(-3 * D), created: iso(D), updated: iso(D) },
    { id: "d1", kind: "news", title: "Behind the scenes: the sync engine", version: "", summary: "", body: "Draft text.", status: "draft", publishAt: null, created: iso(H), updated: iso(H) },
  ]
  let settings = { announcement: { enabled: true, text: "Beta 0.3 is out: invite links and starter projects", link: "/changelog", tone: "info" }, maintenance: { enabled: false, message: "" } }
  let signups = [
    { email: "indie.dev@example.com", source: "home-close", created: iso(5 * H) },
    { email: "pixel.studio@example.com", source: "home-close", created: iso(2 * D) },
  ]
  const member = (id: string, name: string, role: "owner" | "member", access = "edit") => ({ id, name, email: `${name.toLowerCase()}@example.com`, role, access, joined: iso(20 * D), lastSeen: iso(2 * H) })
  const daily30 = () => Array.from({ length: 30 }, (_, i) => Math.round(Math.max(0, Math.sin(i / 3)) * 120))
  const mockProject = (id: string, name: string, role: "owner" | "member", extra: Record<string, unknown>) => ({
    id, name, created: iso(40 * D), archived: false, files: 612, bytes: 7.2e8, changes: 12877, daily: daily30(),
    activity: [{ id: "x1", at: iso(4 * 60000), who: "Maya", kind: "MoveNode", path: "" }], image: null,
    role, myAccess: "edit", owner: role === "owner" ? { id: "u-alex", name: "Alex" } : { id: "u-maya", name: "Maya" },
    peopleLimit: 3, viewers: 0, viewersLimit: 5, invites: [], links: [], members: [], ...extra,
  })
  const dashboard = () => ({
    user: { id: "u-alex", name: "Alex", email: "alex@example.com", emailVerified: true },
    plan: { id: "beta", period: "month", extraSeats: 0, extraStorage: 0, bonusSeats: 0, bonusStorageGb: 0, freeSeats: 0, promos: [{ text: "Early access", redeemedAt: iso(30 * D), until: null }], created: iso(30 * D), maxProjects: 3, projects: 2, peoplePerProject: 3, storageBytes: 2 * 1024 ** 3, usedBytes: 1.75 * 1024 ** 3 },
    projects: [
      mockProject("p-sky", "Skyward", "owner", {
        members: [member("u-alex", "Alex", "owner"), member("u-maya", "Maya", "member"), member("u-sam", "Sam", "member", "view")],
        invites: [{ id: "i1", email: "jordan@example.com", sent: iso(2 * D), expires: iso(-12 * D), expired: false }, { id: "i2", email: "old@example.com", sent: iso(16 * D), expires: iso(2 * D), expired: true }],
        viewers: 2, links: [{ id: "l1", label: "Playtesters", created: iso(3 * D), expires: iso(-4 * D), maxUses: 2, uses: 1, revoked: false }],
      }),
      mockProject("p-tide", "Dungeon Tide", "owner", { members: [member("u-alex", "Alex", "owner")], bytes: 1.1e9 }),
      mockProject("p-owl", "Night Owl", "member", { members: [member("u-maya", "Maya", "owner"), member("u-alex", "Alex", "member")], peopleLimit: 3 }),
    ],
    invitations: [{ id: "inv1", projectId: "p-x", project: "Tiny Tides", from: "Sam", fromEmail: "sam@example.com", to: "alex@example.com", sent: iso(5 * H) }],
    presence: [{ sessionId: "s1", name: "Maya", projectId: "p-sky", scene: "res://levels/level_03.tscn", since: iso(40 * 60000) }],
    teamsNeedCode: true,
  })
  const routes: Record<string, unknown> = { "/overview": overview, "/stats": stats, "/server-update": updates, "/addon": { current: overview.addon, pending: overview.addonPending } }
  return {
    name: "yhde-mock-admin",
    apply: "serve",
    configureServer(server) {
      server.middlewares.use("/health", (_req, res) => res.end("Healthy"))
      // The website content (SiteEndpoints.cs), in memory.
      const body = (req: import("node:http").IncomingMessage) =>
        new Promise<Record<string, unknown>>((ok) => {
          let raw = ""
          req.on("data", (c) => (raw += c))
          req.on("end", () => ok(raw ? JSON.parse(raw) : {}))
        })
      const live = (p: MockPost) => p.status === "published" && (!p.publishAt || new Date(p.publishAt as string) <= new Date())
      server.middlewares.use("/api", async (req, res, next) => {
        const path = (req.url ?? "/").split("?")[0]
        const q = new URLSearchParams((req.url ?? "").split("?")[1] ?? "")
        res.setHeader("Content-Type", "application/json")
        // The dashboard (TeamEndpoints.cs), signed in as Alex who owns two
        // projects and is in Maya's.
        if (path === "/auth/me") return res.end(JSON.stringify({ id: "u-alex", email: "alex@example.com", name: "Alex", emailVerified: true, hasPassword: true, staffRole: null }))
        if (path === "/dashboard") return res.end(JSON.stringify(dashboard()))
        const act = path.match(/^\/team\/projects\/([^/]+)\/activity$/)
        if (act) {
          const who = q.get("who")
          const rows = Array.from({ length: 30 }, (_, i) => ({ id: `a${i}`, at: iso(i * 7 * 60000), who: ["Maya", "Alex", "Sam"][i % 3], whoId: ["u-maya", "u-alex", "u-sam"][i % 3], kind: ["ChangeProperty", "MoveNode", "EditText", "RegisterAsset"][i % 4], path: i % 4 === 2 ? "res://player.gd" : i % 4 === 3 ? "res://art/tree.png" : "" }))
          return res.end(JSON.stringify({ people: [{ id: "u-maya", name: "Maya", changes: 812 }, { id: "u-alex", name: "Alex", changes: 640 }, { id: "u-sam", name: "Sam", changes: 97 }], activity: who ? rows.filter((r) => r.whoId === who) : rows }))
        }
        if (req.method === "POST" && (path.startsWith("/team") || path.startsWith("/invitations"))) return res.end(JSON.stringify({ ok: true, url: "http://localhost:5173/join/K7QM-3XRT-9WDA" }))
        if (path === "/site") return res.end(JSON.stringify({ announcement: settings.announcement.enabled ? settings.announcement : null, maintenance: null }))
        if (path === "/posts") return res.end(JSON.stringify(posts.filter((p) => live(p) && (!q.get("kind") || p.kind === q.get("kind")))))
        if (path === "/early-access" && req.method === "POST") {
          const b = await body(req)
          if (!/^\S+@\S+\.\S+$/.test(String(b.email ?? ""))) {
            res.statusCode = 400
            return res.end(JSON.stringify({ detail: "That email address does not look right." }))
          }
          if (!signups.some((x) => x.email === b.email)) signups.unshift({ email: String(b.email).toLowerCase(), source: String(b.source ?? ""), created: new Date().toISOString() })
          return res.end(JSON.stringify({ ok: true }))
        }
        next()
      })
      server.middlewares.use("/admin/api", async (req, res, next) => {
        const path = (req.url ?? "/").split("?")[0]
        const send = (v: unknown) => (res.setHeader("Content-Type", "application/json"), res.end(JSON.stringify(v)))
        if (path === "/posts" && req.method === "GET") return send(posts)
        if (path === "/posts" && req.method === "POST") {
          const b = await body(req)
          const p = { ...b, id: crypto.randomUUID(), created: new Date().toISOString(), updated: new Date().toISOString() } as MockPost
          if (p.status === "published" && !p.publishAt) p.publishAt = new Date().toISOString()
          posts.unshift(p)
          return send(p)
        }
        const m = path.match(/^\/posts\/([^/]+)(\/delete)?$/)
        if (m && req.method === "POST") {
          const i = posts.findIndex((p) => p.id === m[1])
          if (m[2]) {
            posts.splice(i, 1)
            return send({ ok: true })
          }
          const b = await body(req)
          posts[i] = { ...posts[i], ...b, updated: new Date().toISOString() } as MockPost
          if (posts[i].status === "published" && !posts[i].publishAt) posts[i].publishAt = new Date().toISOString()
          return send(posts[i])
        }
        if (path === "/site-settings" && req.method === "GET") return send(settings)
        if (path === "/site-settings" && req.method === "POST") {
          settings = (await body(req)) as typeof settings
          return send(settings)
        }
        if (path === "/early-access") return send(signups)
        if (path === "/early-access.csv") {
          res.setHeader("Content-Type", "text/csv")
          return res.end("email,source,signed_up\n" + signups.map((s) => `"${s.email}","${s.source}",${s.created}`).join("\n"))
        }
        if (path === "/early-access/remove") {
          const b = await body(req)
          signups = signups.filter((s) => s.email !== b.email)
          return send({ ok: true })
        }
        next()
      })
      server.middlewares.use("/admin/api", (req, res) => {
        const path = (req.url ?? "/").split("?")[0]
        res.setHeader("Content-Type", "application/json")
        if (req.method === "GET" && path in routes) return res.end(JSON.stringify(routes[path]))
        if (path === "/people") {
          // AdminStats.PeopleAsync: buckets for the range, then who.
          const range = new URLSearchParams((req.url ?? "").split("?")[1] ?? "").get("range") ?? "month"
          const spec: Record<string, [string, number, number]> = {
            hour: ["5min", 5 * 60000, 12], day: ["hour", H, 24], week: ["6h", 6 * H, 28], month: ["day", D, 30],
            quarter: ["week", 7 * D, 13], year: ["week", 7 * D, 52], all: ["month", 30 * D, 7],
          }
          const [unit, step, count] = spec[range] ?? spec.month
          const buckets = Array.from({ length: count }, (_, k) => {
            const w = Math.max(0, Math.sin(k * 0.7) + 0.6)
            return { at: new Date(now - (count - 1 - k) * step).toISOString(), people: Math.round(w * 2.5), hours: Math.round(w * (step / H) * 1.3 * 10) / 10, changes: Math.round(w * (step / H) * 40) }
          })
          const list = people.map((p, i) => ({ ...p, kind: i === 3 ? "test" : "account" })).concat([{ ...people[0], id: "g1", name: "Playtester", kind: "guest", online: false, changes: 12 }])
          return res.end(JSON.stringify({ range, unit, from: buckets[0].at, buckets, list }))
        }
        if (path === "/projects/11111111-1111-1111-1111-111111111111/links") return res.end(JSON.stringify({ url: "http://localhost:5173/join/K7QM-3XRT-9WDA" }))
        if (path.endsWith("/invites")) return res.end(JSON.stringify({ code: "YHDE-7K2M-QX9D-4RTW-88PA" }))
        res.end(JSON.stringify({ ok: true }))
      })
    },
  }
}

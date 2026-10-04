import { useCallback, useEffect, useState } from "react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { api } from "../api"
import { ago } from "../format"
import { Empty, Section } from "../parts"

type Row = { id: number; at: string; type: string; actorId: string | null; actor: string; projectId: string | null; project: string; targetId: string | null; detail: string }

const FILTERS = [
  { value: "all", label: "Everything" },
  { value: "admin.", label: "Admin page" },
  { value: "promo.", label: "Promo codes" },
  { value: "project.", label: "Projects" },
  { value: "account.", label: "Accounts" },
  { value: "site.", label: "Website" },
]

// What happened, in words.
const EVENTS: Record<string, string> = {
  "admin.signed_in": "Signed in to the admin page",
  "admin.refused": "Was refused the admin page",
  "admin.role_changed": "Changed someone's admin access",
  "admin.user_disabled": "Disabled an account",
  "admin.user_enabled": "Enabled an account",
  "admin.user_signed_out": "Signed someone out everywhere",
  "admin.confirmation_sent": "Sent a confirmation email",
  "admin.user_deleted": "Deleted an account",
  "admin.team_changed": "Changed an owner's plan",
  "admin.member_added": "Added someone to a project",
  "admin.member_removed": "Removed someone from a project",
  "admin.project_moved": "Moved a project to an owner",
  "project.transferred": "Handed a project to someone",
  "admin.promo_created": "Made a promo code",
  "admin.promo_changed": "Changed a promo code",
  "admin.promo_deleted": "Deleted a promo code",
  "promo.redeemed": "Used a promo code",
  "promo.refused": "Tried a promo code that didn't work",
  "team.created": "Started making projects with an access code",
  "team.refused": "Tried an access code that didn't work",
  "project.deleted": "Deleted a project",
  "account.deleted": "Deleted their account",
  "account.created": "Made an account",
  "account.signed_in": "Signed in",
  "account.password_changed": "Changed their password",
  "account.provider_linked": "Connected GitHub or Google",
  "account.provider_unlinked": "Disconnected GitHub or Google",
  "account.session_ended": "Ended a sign-in",
  "site.post_created": "Wrote a post",
  "site.post_updated": "Changed a post",
  "site.post_deleted": "Deleted a post",
  "site.settings_changed": "Changed the website settings",
  "site.early_access_removed": "Removed someone from early access",
}

function details(json: string) {
  try {
    const d = JSON.parse(json) as Record<string, unknown>
    return Object.entries(d)
      .filter(([k, v]) => v !== null && v !== "" && k !== "by")
      .map(([k, v]) => `${k}: ${typeof v === "object" ? JSON.stringify(v) : String(v)}`)
      .join(" · ")
  } catch {
    return json
  }
}

export function AuditPage() {
  const [filter, setFilter] = useState("all")
  const [rows, setRows] = useState<Row[] | null>(null)
  const [more, setMore] = useState(false)

  const load = useCallback(async (type: string, before?: number) => {
    try {
      const q = new URLSearchParams()
      if (type !== "all") q.set("type", type)
      if (before) q.set("before", String(before))
      const page = await api<Row[]>("GET", "/audit?" + q)
      setRows((old) => (before && old ? [...old, ...page] : page))
      setMore(page.length === 200)
    } catch (e) {
      toast.error((e as Error).message)
    }
  }, [])
  useEffect(() => {
    load(filter)
  }, [filter, load])

  return (
    <Section
      title="Who did what"
      description="Sign-ins to this page, changes to accounts, teams and codes, and deleted projects. Newest first."
      action={
        <Select value={filter} onValueChange={setFilter}>
          <SelectTrigger className="w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {FILTERS.map((f) => (
              <SelectItem key={f.value} value={f.value}>
                {f.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      }
    >
      {rows && rows.length === 0 ? (
        <Empty>Nothing yet.</Empty>
      ) : (
        <>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>When</TableHead>
                <TableHead>Who</TableHead>
                <TableHead>What</TableHead>
                <TableHead>Details</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(rows ?? []).map((r) => (
                <TableRow key={r.id}>
                  <TableCell className="whitespace-nowrap text-muted-foreground" title={new Date(r.at).toLocaleString()}>
                    {ago(r.at)}
                  </TableCell>
                  <TableCell>{r.actor || (r.type.startsWith("admin.") ? "Server password" : "-")}</TableCell>
                  <TableCell>
                    {EVENTS[r.type] ?? r.type}
                    {r.project ? <span className="block text-xs text-muted-foreground">{r.project}</span> : null}
                  </TableCell>
                  <TableCell className="max-w-md truncate text-xs text-muted-foreground" title={r.detail}>
                    {details(r.detail)}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          {more && rows ? (
            <Button variant="ghost" className="mt-3" onClick={() => load(filter, rows[rows.length - 1].id)}>
              Show older
            </Button>
          ) : null}
        </>
      )}
    </Section>
  )
}

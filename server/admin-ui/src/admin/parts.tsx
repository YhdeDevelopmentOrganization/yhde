import type { ReactNode } from "react"
import { EmptyState, Figure, Panel } from "@/world/parts"

// Small building blocks shared by the admin pages.

export function Stat({ label, value, hint, tone }: { label: string; value: ReactNode; hint?: ReactNode; tone?: "green" | "blue" }) {
  return <Figure label={label} value={value} hint={hint} tone={tone} />
}

export function Section({ title, description, action, children }: { title: string; description?: ReactNode; action?: ReactNode; children: ReactNode }) {
  return (
    <Panel title={title} meta={description} action={action}>
      {children}
    </Panel>
  )
}

export function Empty({ children }: { children: ReactNode }) {
  return <EmptyState title={String(children)} />
}

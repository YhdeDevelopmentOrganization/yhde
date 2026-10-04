import { Building2, LifeBuoy, Mail, ReceiptText, ShieldCheck } from "lucide-react"
import { cn } from "@/lib/utils"
import { COMPANY } from "../company"
import { PLANS_OPEN } from "@/world/plans"
import { SocialLinks, href } from "../chrome"
import { PageTop } from "../parts"

const TEXT = {
  title: "Get in",
  accent: "touch",
  leadA: "Real people answer. For help, the",
  leadLink: "getting started guide",
  leadB: "may be quicker.",
  ways: [
    { title: "General", body: "Questions about YHDE, the beta or access codes." },
    { title: "Help with the product", body: "Something doesn't work? Tell us your Godot version and what you see." },
    { title: "Billing", body: "Plans, invoices and payments." },
    { title: "Privacy and your data", body: "Ask what we store about you, or ask us to delete it." },
  ],
  company: "Company info",
  fields: { name: "Company", id: "Business ID", address: "Address", email: "Email", based: "Based in" },
  community: "Community",
}

const LOOK = [
  { icon: Mail, tint: "bg-tint-blue text-sky", email: COMPANY.email },
  { icon: LifeBuoy, tint: "bg-tint-green text-good", email: COMPANY.support },
  { icon: ReceiptText, tint: "bg-card-2 text-text", email: COMPANY.billing },
  { icon: ShieldCheck, tint: "bg-tint-rust text-[#ffb49c]", email: COMPANY.privacy },
]

export function Contact() {
  const t = TEXT
  return (
    <>
      <PageTop
        title={t.title}
        accent={t.accent}
        lead={
          <>
            {t.leadA}{" "}
            <a className="font-semibold text-sky hover:underline" href={href("/docs")}>
              {t.leadLink}
            </a>
            {t.leadB === "." ? "." : ` ${t.leadB}`}
          </>
        }
      />
      <div className="mx-auto max-w-6xl px-5 pb-16 sm:px-7">
        <div className={cn("grid gap-4 sm:grid-cols-2", PLANS_OPEN ? "lg:grid-cols-4" : "lg:grid-cols-3")}>
          {t.ways.map((w, i) => {
            // Nothing is sold during the beta, so there is no billing contact yet.
            if (!PLANS_OPEN && w.title === "Billing") return null
            const Icon = LOOK[i].icon
            return (
              <a key={w.title} href={`mailto:${LOOK[i].email}`} className="group rounded-[1.5rem] bg-card p-7 transition-colors hover:bg-card-2">
                <span className={cn("grid size-12 place-items-center rounded-2xl", LOOK[i].tint)}>
                  <Icon className="size-6" aria-hidden="true" />
                </span>
                <h2 className="mt-5 text-xl font-bold text-text">{w.title}</h2>
                <p className="mt-2 text-[0.9375rem] leading-relaxed text-dim">{w.body}</p>
                <p className="mt-5 font-semibold text-sky group-hover:underline">{LOOK[i].email}</p>
              </a>
            )
          })}
        </div>

        <section id="company" className="mt-4 scroll-mt-24 rounded-[1.5rem] bg-card p-7">
          <div className="flex flex-wrap items-center gap-4">
            <span className="grid size-12 place-items-center rounded-2xl bg-card-2 text-text">
              <Building2 className="size-6" aria-hidden="true" />
            </span>
            <h2 className="text-xl font-bold text-text">{t.company}</h2>
            <SocialLinks className="ml-auto" />
          </div>
          <dl className="mt-6 grid gap-x-10 gap-y-4 text-[0.9375rem] sm:grid-cols-2 lg:grid-cols-4">
            {[
              [t.fields.name, COMPANY.name],
              [t.fields.id, COMPANY.businessId],
              [COMPANY.address ? t.fields.address : t.fields.based, COMPANY.address || COMPANY.country],
              [t.fields.email, COMPANY.info],
            ]
              .filter(([, value]) => value)
              .map(([label, value]) => (
              <div key={label}>
                <dt className="text-sm text-dim">{label}</dt>
                <dd className="mt-1 font-semibold text-text">{value}</dd>
              </div>
            ))}
          </dl>
        </section>
      </div>
    </>
  )
}

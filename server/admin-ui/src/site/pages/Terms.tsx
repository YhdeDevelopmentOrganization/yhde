import { COMPANY, LEGAL_UPDATED, companyLine } from "../company"
import { PLANS_OPEN } from "@/world/plans"
import { href } from "../chrome"
import { Draft, PageTop, Prose, WithToc } from "../parts"

const TOC = [
  { id: "service", label: "The service" },
  { id: "beta", label: "The beta" },
  { id: "accounts", label: "Owners and teammates" },
  { id: "plans", label: "Plans and payment" },
  { id: "cancel", label: "Leaving" },
  { id: "content", label: "Your content" },
  { id: "use", label: "Fair use" },
  { id: "availability", label: "Availability" },
  { id: "liability", label: "Liability" },
  { id: "law", label: "Law and disputes" },
]

export function Terms() {
  return (
    <>
      <PageTop title="Terms of" accent="service" lead={<>Updated {LEGAL_UPDATED}. These terms are between you and {companyLine()}.</>}>
        <Draft>Written to match how YHDE works. Not yet reviewed by a lawyer.</Draft>
      </PageTop>
      <WithToc toc={TOC}>
        <Prose>
          <h2 id="service">The service</h2>
          <p>
            YHDE lets a team work in the same Godot project at the same time. It is made of an add-on for the Godot editor, our hosted servers, and a web
            dashboard where the team is managed. You need to be at least 13 to use it.
          </p>

          <h2 id="beta">The beta</h2>
          <p>
            YHDE is in beta until version 1.0. During the beta it is free: nothing is charged and no payment details are asked for. Everyone who makes
            projects is on the <strong>Beta tester</strong> plan: up to three projects, each with the owner and up to three invited people, within a
            fair storage limit.
          </p>
          <p>
            Paid plans start with version 1.0. We will tell every team owner at least 30 days before, and nobody is moved to a paid plan without choosing
            one. We may change or end the beta, or a team's place in it, if a team breaks these terms.
          </p>

          <h2 id="accounts">Owners and teammates</h2>
          <p>
            The person who subscribes is the team <strong>owner</strong>. The owner invites teammates, who join free and use YHDE under these terms too.
            The owner is responsible for who they invite and can remove people at any time. Keep invite links and codes to yourselves.
          </p>

          <h2 id="plans">Plans and payment</h2>
          {PLANS_OPEN ? (
            <p>
              Plans are priced by team size and storage, shown on the <a href={href("/#plans")}>pricing section</a>. All prices include VAT.
              Subscriptions are paid in advance, monthly or yearly, and renew automatically until cancelled. We will tell owners at least 30 days before a
              price change. Questions about invoices: <a href={`mailto:${COMPANY.billing}`}>{COMPANY.billing}</a>.
            </p>
          ) : (
            <p>
              There is nothing to pay during the beta. The paid plans, their prices and how payment works will be added here before version 1.0, and
              only apply to owners who choose a paid plan. Questions: <a href={`mailto:${COMPANY.email}`}>{COMPANY.email}</a>.
            </p>
          )}

          <h2 id="cancel">Leaving</h2>
          <p>
            You can leave a project, or delete your account under Account, at any time. A project's owner can delete it whenever they want.
            {PLANS_OPEN ? (
              <>
                {" "}
                The owner can cancel a paid plan any time; it stays active until the end of the paid period. Consumers in the EU may withdraw within 14
                days of the first purchase and get a full refund. After that, cancelling stops the next renewal, and time already paid for is not
                refunded.
              </>
            ) : null}
          </p>

          <h2 id="content">Your content</h2>
          <p>
            Your game and everything in your projects stays yours. You give us only the permission needed to store, sync and back it up for your team.
            We never use your content to train AI or machine learning models, and never let anyone else do so. Your
            files are also in each teammate's own Godot project folder, and the owner can delete projects whenever they want.
          </p>

          <h2 id="use">Fair use</h2>
          <ul>
            <li>Don't use YHDE to store or share illegal content or content you don't have the rights to.</li>
            <li>Don't try to break, overload or get around the security of the service.</li>
            <li>Storage limits are part of your plan; we may ask you to upgrade or clean up if you go over them.</li>
          </ul>

          <h2 id="availability">Availability</h2>
          <p>
            YHDE is in beta. We work to keep it running and your data safe, and we back projects up daily, but we can't promise it is always available or
            free of bugs. Keep your own copies of important work (for example with Git). Planned downtime is shown on the status page and the site.
          </p>

          <h2 id="liability">Liability</h2>
          <p>
            To the extent the law allows, our liability is limited to what the owner paid us in the 12 months before the claim, which during the free
            beta is nothing. Nothing here limits rights consumers have by law.
          </p>

          <h2 id="law">Law and disputes</h2>
          <p>
            Finnish law applies. We'd like to sort problems out directly: email <a href={`mailto:${COMPANY.email}`}>{COMPANY.email}</a>. Consumers can
            also contact the Finnish Consumer Disputes Board.
          </p>
        </Prose>
      </WithToc>
    </>
  )
}

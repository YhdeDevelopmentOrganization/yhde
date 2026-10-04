import { COMPANY, LEGAL_UPDATED } from "../company"
import { href } from "../chrome"
import { PageTop, Prose } from "../parts"

// Every cookie and browser storage key the site sets. Keep in step with
// AuthEndpoints.cs, OAuth.cs, AdminAuth.cs and the localStorage keys in the UI.
const COOKIES = [
  { name: "yhde_sid", what: "Keeps you signed in on the website.", lasts: "30 days, or until you sign out" },
  { name: "yhde_oauth", what: "Checks that a GitHub or Google sign-in you started comes back to you.", lasts: "10 minutes" },
  { name: "yhde_admin", what: "Keeps YHDE staff signed in on the admin page. Only set for staff.", lasts: "12 hours" },
]

const STORAGE = [
  { name: "yhde.cookies-seen", what: "Remembers that you closed the cookie note." },
  { name: "yhde.plan", what: "Remembers the plan you picked before making your account." },
  { name: "yhde.announcement.…", what: "Remembers which announcements you closed." },
]

function Table({ rows, lasts }: { rows: { name: string; what: string; lasts?: string }[]; lasts: boolean }) {
  return (
    <div className="mt-4 overflow-x-auto rounded-2xl bg-card">
      <table className="w-full min-w-[32rem] text-left text-[0.9375rem]">
        <thead className="text-sm text-dim">
          <tr>
            <th className="px-5 py-3 font-semibold">Name</th>
            <th className="px-5 py-3 font-semibold">What it does</th>
            {lasts ? <th className="px-5 py-3 font-semibold">How long</th> : null}
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.name} className="border-t border-line align-top">
              <td className="px-5 py-3">
                <code>{r.name}</code>
              </td>
              <td className="px-5 py-3">{r.what}</td>
              {lasts ? <td className="px-5 py-3 whitespace-nowrap">{r.lasts}</td> : null}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function Cookies() {
  return (
    <>
      <PageTop title="Cookies" accent="and storage" lead={<>Updated {LEGAL_UPDATED}. YHDE only uses what the site needs to work. No tracking, analytics or advertising.</>} />
      <div className="mx-auto max-w-6xl px-5 pb-16 sm:px-7">
        <Prose>
          <p>
            Cookies that a site needs to do what you asked for need no consent under EU law, and those are the only ones we use. That's why there is
            nothing to accept or decline. If we ever add anything else, you will be asked first, and it stays off until you say yes.
          </p>

          <h2 id="cookies">Cookies</h2>
          <p>All of them are first-party (only YHDE's own site can read them), HttpOnly (scripts on the page can't read them) and sent only over HTTPS.</p>
          <Table rows={COOKIES} lasts />

          <h2 id="storage">Browser storage</h2>
          <p>A few small settings are kept in your browser's local storage. They never leave your browser, and clearing your site data removes them.</p>
          <Table rows={STORAGE} lasts={false} />

          <h2 id="others">Other sites</h2>
          <p>
            The site loads no scripts, fonts or images from other sites, so no one else can set cookies through it. Signing in with GitHub or Google
            takes you to their site, where their own cookies apply.
          </p>

          <h2 id="questions">Questions</h2>
          <p>
            Read the <a href={href("/privacy")}>privacy policy</a>, or email <a href={`mailto:${COMPANY.privacy}`}>{COMPANY.privacy}</a>.
          </p>
        </Prose>
      </div>
    </>
  )
}

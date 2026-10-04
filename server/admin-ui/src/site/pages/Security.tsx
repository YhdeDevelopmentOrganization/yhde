import { COMPANY, LEGAL_UPDATED } from "../company"
import { OFFICIAL, operatorName } from "@/world/site"
import { PageTop, Prose, WithToc } from "../parts"

const TOC = [
  { id: "report", label: "Report a problem" },
  { id: "connections", label: "Connections" },
  { id: "accounts", label: "Accounts and sign-in" },
  { id: "projects", label: "Your projects" },
  { id: "editor", label: "The Godot add-on" },
  { id: "staff", label: "Our side" },
  { id: "you", label: "What you can do" },
]

// The public side of docs/security.md. Keep the two in step.
export function Security() {
  return (
    <>
      <PageTop
        title="Security"
        lead={<>Updated {LEGAL_UPDATED}. How YHDE protects your account and your projects, and how to tell us about a problem.</>}
      />
      <WithToc toc={TOC}>
        <Prose>
          <h2 id="report">Report a security problem</h2>
          <p>
            Found a weakness? Email <a href={`mailto:${COMPANY.support}?subject=Security`}>{COMPANY.support}</a> with "Security" in the subject, what
            you found and how to repeat it. We answer within a few working days and tell you when it's fixed.
          </p>
          {OFFICIAL ? null : (
            <p>
              This address is for weaknesses in the YHDE software. This server is run by {operatorName()}: problems with the server itself, your
              account or your data on it go to them.
            </p>
          )}
          <ul>
            <li>Only test against your own account, team and projects.</li>
            <li>Don't read, change or delete other people's data, and don't try to overload the service.</li>
            <li>Give us a reasonable time to fix it before telling anyone else.</li>
          </ul>
          <p>If you follow these, we won't take any action against you for your research. The same contact is in <code>/.well-known/security.txt</code>.</p>

          <h2 id="connections">Connections</h2>
          <ul>
            <li>Everything is encrypted: HTTPS for the website and files, WSS for editing. Browsers are told to always use HTTPS.</li>
            <li>The site's pages set a strict content security policy and can't be shown inside other sites.</li>
            <li>The add-on warns you if a server address isn't encrypted.</li>
          </ul>

          <h2 id="accounts">Accounts and sign-in</h2>
          <ul>
            <li>Passwords are stored only as Argon2id hashes. We can't see your password.</li>
            <li>Sign-ins are long random tokens, and we store only their hashes.</li>
            <li>Under Account you see every browser and editor signed in to your account and can sign any of them out. An editor you sign out is disconnected at once.</li>
            <li>Sign-in, password resets, invite codes and access codes are limited to a few tries, so they can't be guessed.</li>
            <li>Other sites can't make your browser change anything on YHDE for you.</li>
          </ul>

          <h2 id="projects">Your projects</h2>
          <ul>
            <li>The server checks every change against the person's role and project access. A modified add-on gets no extra rights.</li>
            <li>Who made a change comes from their sign-in, never from what the editor sends.</li>
            <li>The history of changes is only ever added to, and each change is chained to the one before it, so old changes can't be quietly rewritten.</li>
            <li>Files are stored by their checksum and checked on every transfer.</li>
            <li>Projects are stored in Helsinki, Finland, and the database is backed up every day.</li>
          </ul>

          <h2 id="editor">The Godot add-on</h2>
          <ul>
            <li>Add-on updates are only offered, never installed without your click, and only when they are signed as a YHDE release.</li>
            <li>Files from teammates that can run code on your computer (plugins, native libraries, @tool scripts) wait until you accept them.</li>
            <li>Scripts built into scenes are code, so accepting them from teammates is off until you turn it on.</li>
            <li>YHDE is open source: the add-on and the server are on <a href="https://github.com/YhdeDevelopmentOrganization/yhde" rel="noreferrer">GitHub</a>.</li>
          </ul>

          <h2 id="staff">Our side</h2>
          <ul>
            <li>Staff sign in to the admin page with their own accounts, and every change they make is logged.</li>
            <li>Server updates are applied only after a backup, and rolled back if the new version doesn't start.</li>
          </ul>

          <h2 id="you">What you can do</h2>
          <ul>
            <li>Use a password you don't use anywhere else, or sign in with GitHub or Google.</li>
            <li>Give invite links a time limit and a use limit, and revoke ones you no longer need.</li>
            <li>Remove people who leave your team; they lose access at once.</li>
            <li>Keep your own copies of important work, for example with Git.</li>
          </ul>
        </Prose>
      </WithToc>
    </>
  )
}

import { COMPANY } from "../company"
import { OFFICIAL, SITE, operatorName } from "@/world/site"
import { href } from "../chrome"
import { PageTop, Prose, WithToc } from "../parts"

const TOC = [
  { id: "requirements", label: "What you need" },
  { id: "owner", label: "Start a team (owner)" },
  { id: "join", label: "Join a project" },
  { id: "working", label: "Working together" },
  { id: "offline", label: "Offline and reconnecting" },
  { id: "updates", label: "Updating YHDE" },
  { id: "git", label: "YHDE and Git" },
  { id: "troubleshooting", label: "Troubleshooting" },
]

export function Docs() {
  return (
    <>
      <PageTop title="Getting" accent="started" lead="Everything you need to get your team into the same Godot project. It takes a few minutes." />
      <WithToc toc={TOC}>
        <Prose>
          <h2 id="requirements">What you need</h2>
          <ul>
            <li>
              <strong>Godot 4.7 or newer</strong>, on Windows, macOS or Linux, from <a href="https://godotengine.org/download/" rel="noreferrer">godotengine.org</a>.
            </li>
            <li>An internet connection. YHDE keeps working when it drops for a while.</li>
            <li>
              {OFFICIAL
                ? "A free YHDE account for everyone. Only the owner picks a plan; teammates join free."
                : "A free account on this server for everyone."}
            </li>
          </ul>

          <h2 id="owner">Start a team (owner)</h2>
          <ol>
            <li>
              {OFFICIAL ? (
                <>
                  Pick a plan on the <a href={href("/#plans")}>pricing section</a> and make your account.
                </>
              ) : (
                <>
                  <a href={href("/app#/start")}>Make your account</a> on this server.
                </>
              )}
            </li>
            <li>In your dashboard, make a project. Start empty, or upload a zip of your existing Godot project.</li>
            <li>
              On the project's page, under <strong>People</strong>, invite your teammates by email. They accept from the email or their dashboard, with a free account.
            </li>
            <li>
              Or open a project and press <strong>Make invite link</strong>: the link downloads the game as a ready Godot project. Opening it in a chat
              preview doesn't use it up.
            </li>
          </ol>

          <h2 id="join">Join a project</h2>
          <ol>
            <li>
              Get the YHDE add-on: <a href="/addon/yhde-addon.zip">download it</a> (one file, no need to unzip). In Godot, open a new, empty project,
              go to the <strong>AssetLib</strong> tab at the top, press <strong>Import…</strong>, pick the file and press <strong>Install</strong>. Then
              turn YHDE on under Project Settings, Plugins.
            </li>
            <li>
              In the <strong>YHDE panel</strong>, press <strong>Sign in with your browser</strong>. Your browser opens: sign in (or make your free
              account), check the code matches the one in Godot, and press <strong>Allow</strong>.
            </li>
            <li>Your team's projects show up in the panel. Click one: its files and settings arrive, and you're in.</li>
          </ol>
          <p>
            Your name in Godot is your account's name, so your teammates always know who is who. Got an invite link instead? Open it: it downloads the
            game with YHDE inside, and the panel offers to join with it.
          </p>

          <h2 id="working">Working together</h2>
          <ul>
            <li>Everyone's cursor, selection and name show up in the 2D and 3D views. Click a name in the YHDE panel to follow what they see.</li>
            <li>Every change to nodes and Inspector properties reaches your team in milliseconds. You don't need to save to share.</li>
            <li>Ctrl+Z undoes your own last change, never a teammate's.</li>
            <li>If two people change the same property at the same moment, the most recent change wins for everyone. Nothing ends up broken.</li>
            <li>Use the chat and comments in the YHDE panel to talk about what you're doing.</li>
          </ul>

          <h2 id="offline">Offline and reconnecting</h2>
          <p>
            If your connection drops, keep working. The YHDE panel shows how many changes are waiting, keeps them even if Godot closes, and sends them in
            order when you're back online.
          </p>

          <h2 id="updates">Updating YHDE</h2>
          <p>
            When a new version is out, the YHDE panel shows <strong>Update</strong>. It downloads the update, checks it, installs it, and offers to
            restart Godot. See <a href={href("/changelog")}>what's new</a>.
          </p>

          <h2 id="git">YHDE and Git</h2>
          <p>
            Keep using Git for history and releases. YHDE is for working together live. The <code>addons/yhde</code> folder belongs to each person's own
            setup, so you can leave it out of your repository.
          </p>

          <h2 id="troubleshooting">Troubleshooting</h2>
          <h3>“Native core missing” in the YHDE panel</h3>
          <p>
            Your Godot is older than 4.7. Install Godot 4.7 or newer and open the project again.
          </p>
          <h3>Connect doesn't work</h3>
          <ul>
            <li>Check that you're online and that your network allows secure web connections (port 443).</li>
            <li>
              If the panel says your invite doesn't work, it may have been turned off or used up. Ask the owner for a new link.
            </li>
            <li>
              Check the <a href={href("/status")}>status page</a> to see whether the servers are up.
            </li>
          </ul>
          <h3>I don't see a teammate's changes</h3>
          <p>Check that you both show Live in the YHDE panel and are in the same project. Changes to a scene show up once it is open.</p>
          <h3>Still stuck?</h3>
          {OFFICIAL ? (
            <p>
              Email <a href={`mailto:${COMPANY.support}`}>{COMPANY.support}</a> with your Godot version, your operating system and what the YHDE panel
              says.
            </p>
          ) : (
            <p>
              Ask {operatorName()}
              {SITE.operator?.email ? (
                <>
                  {" "}at <a href={`mailto:${SITE.operator.email}`}>{SITE.operator.email}</a>
                </>
              ) : null}{" "}
              with your Godot version, your operating system and what the YHDE panel says. They run this server.
            </p>
          )}
        </Prose>
      </WithToc>
    </>
  )
}

import { COMPANY } from "../company"
import { OFFICIAL, SITE, operatorName } from "@/world/site"
import { href } from "../chrome"
import { PageTop, Prose, WithToc } from "../parts"

// The oldest Godot the add-on runs on (compatibility_minimum in
// yhde.gdextension; the server's AddonStore.OldestGodot for other pages).
const GODOT = "4.7"

const TOC = [
  { id: "requirements", label: "What you need" },
  { id: "invited", label: "I was invited" },
  { id: "start", label: "Start a project" },
  { id: "share-a-game", label: "Share a game you have" },
  { id: "working", label: "Working together" },
  { id: "offline", label: "Offline and reconnecting" },
  { id: "updates", label: "Updating YHDE" },
  { id: "git", label: "YHDE and Git" },
  { id: "troubleshooting", label: "Troubleshooting" },
  ...(OFFICIAL ? [{ id: "own-server", label: "Run your own server" }] : []),
]

export function Docs() {
  return (
    <>
      <PageTop title="Getting" accent="started" lead="Get your team into the same Godot project. It takes a few minutes." />
      <WithToc toc={TOC}>
        <Prose>
          {OFFICIAL ? (
            <figure className="mb-12">
              <video
                className="aspect-video w-full rounded-2xl bg-card"
                src="/ui/media/yhde-tutorial.mp4"
                poster="/ui/media/yhde-tutorial.jpg"
                controls
                playsInline
                preload="metadata"
                aria-label="Setting up YHDE: making an account and a project, inviting a teammate, installing the add-on in Godot and signing in."
              />
              <figcaption className="mt-3 text-sm text-dim">Recorded with YHDE 0.6.8, before joining got shorter. The steps below are the current way.</figcaption>
            </figure>
          ) : null}
          <h2 id="requirements">What you need</h2>
          <ul>
            <li>
              <strong>Godot {GODOT} or newer</strong>, on Windows, macOS or Linux, from <a href="https://godotengine.org/download/" rel="noreferrer">godotengine.org</a>.
            </li>
            <li>An internet connection. YHDE keeps working when it drops for a while.</li>
            <li>A free {OFFICIAL ? "YHDE account" : "account on this server"}. You make it while joining.</li>
          </ul>

          <h2 id="invited">I was invited</h2>
          <ol>
            <li>
              Open the invite link and press <strong>Download</strong>.
            </li>
            <li>
              In Godot, press <strong>Import</strong>, pick the zip, then press <strong>Import</strong> again.
            </li>
            <li>
              In the YHDE panel on the right, press <strong>Join</strong>. Sign in or make an account if your browser asks. The game's files arrive.
            </li>
          </ol>
          <p>
            Got a code instead of a link? Sign in on <a href={href("/app#/projects")}>the YHDE website</a> and type it under{" "}
            <strong>I was invited</strong>.
          </p>

          <h2 id="start">Start a project</h2>
          <ol>
            <li>
              <a href={href("/app#/projects")}>Sign in</a> (or make an account), press <strong>Start a project</strong> and name it.
              {OFFICIAL ? " During the beta this needs an access code from us." : null}
            </li>
            <li>
              Press <strong>Download</strong>, open the zip in Godot with <strong>Import</strong>, and press <strong>Join</strong> in the YHDE panel.
            </li>
            <li>
              Press <strong>Invite people</strong> and send the link. Pick <strong>Can edit</strong> for a teammate, or <strong>Can only watch</strong>{" "}
              for a playtester. Each link is for one person.
            </li>
          </ol>

          <h2 id="share-a-game">Share a game you have</h2>
          <ol>
            <li>
              <a href="/addon/yhde-addon.zip">Download YHDE</a>. Open your game in Godot, go to the <strong>Asset Store</strong> tab at the top, press{" "}
              <strong>Import…</strong>, pick the file and press <strong>Install</strong>. Then turn YHDE on under Project, Project Settings, Plugins.
            </li>
            <li>
              In the YHDE panel on the right, press <strong>Sign in</strong>, then <strong>Share this game</strong>. Your files go up once.
            </li>
            <li>
              Press <strong>Invite people</strong> and send the link.
            </li>
          </ol>
          <p>Your name in Godot is your account's name, so your teammates always know who is who.</p>

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
          <h3>“YHDE needs Godot {GODOT} or newer”</h3>
          <p>
            Install a newer Godot from <a href="https://godotengine.org/download/" rel="noreferrer">godotengine.org</a> and open the project again. Nothing
            in your project was changed.
          </p>
          <h3>“YHDE can't start on this computer”</h3>
          <p>A file of YHDE is missing. Press <strong>Get YHDE again</strong> in the panel, put it in your project the same way, and restart Godot.</p>
          <h3>Connect doesn't work</h3>
          <ul>
            <li>Check that you're online and that your network allows secure web connections (port 443).</li>
            <li>
              If your invite link doesn't work, it may have run out, been used or been turned off. Ask the person who sent it for a new one.
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
          {OFFICIAL ? (
            <>
              <h2 id="own-server">Run your own server</h2>
              <p>
                YHDE is open source. The server and the add-on's native core are under the AGPL-3.0, and the add-on's scripts under MIT. To run a
                server for your own team, follow the{" "}
                <a href="https://github.com/YhdeDevelopmentOrganization/yhde/blob/main/docs/setup_your_server.md" rel="noreferrer">
                  step-by-step setup guide
                </a>{" "}
                on GitHub. It takes about 30 minutes on one small server, and no server experience is needed.
              </p>
            </>
          ) : null}
        </Prose>
      </WithToc>
    </>
  )
}

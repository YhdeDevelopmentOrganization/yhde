import { useEffect, useState, type ReactNode } from "react";
import {
  CreditCard,
  FolderKanban,
  LogOut,
  User,
  WifiOff,
} from "lucide-react";
import { toast } from "sonner";
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { Meter, Peer, Wordmark } from "@/world/parts";
import { planUsage, startRefresh, useAppState, useDashboard } from "./store";
import { ProjectsPage } from "./pages/Projects";
import { ProjectPage } from "./pages/Project";
import { BillingPage } from "./pages/Billing";
import { AccountPage } from "./pages/Account";
import { DevicePage } from "./pages/Device";
import { InvitationList, InvitePage, pendingInvite } from "./pages/Invite";
import {
  ForgotPage,
  ResetPage,
  SignInPage,
  StartPage,
  VerifyPage,
} from "./pages/Auth";
import { loadMe, auth, useMe } from "./auth";
import { PLANS_OPEN } from "@/world/plans";
import { OFFICIAL } from "@/world/site";

// A tiny hash router: #/path?query. Hash routes keep every page on /app, so
// the server serves one file.
export type Route = { path: string[]; query: URLSearchParams };
function parse(): Route {
  const [p, q] = location.hash.replace(/^#\/?/, "").split("?");
  return {
    path: p ? p.split("/").filter(Boolean) : [],
    query: new URLSearchParams(q),
  };
}
export function go(to: string) {
  location.hash = to.startsWith("#") ? to : "#" + to;
}
function useRoute() {
  const [route, setRoute] = useState(parse);
  useEffect(() => {
    const on = () => {
      setRoute(parse());
      window.scrollTo({ top: 0 });
    };
    addEventListener("hashchange", on);
    return () => removeEventListener("hashchange", on);
  }, []);
  return route;
}

export const JOIN_KEY = "yhde.join";

export default function App() {
  const route = useRoute();
  const me = useMe();
  const dash = useDashboard();
  useEffect(() => {
    loadMe().catch(() => {});
  }, []);
  // The team's data, once someone is signed in.
  useEffect(() => (me ? startRefresh() : undefined), [me?.id]);

  const [first] = route.path;
  // Already signed in: the sign-in and sign-up pages lead to the dashboard.
  const signedInAway = !!me && (first === "sign-in" || first === "start") && !pendingInvite();
  useEffect(() => {
    if (signedInAway) go("/projects");
  }, [signedInAway]);
  // Signed in after opening an invitation's link: back to that invitation.
  useEffect(() => {
    const token = me ? pendingInvite() : null;
    if (token && first !== "invite") go(`/invite/${token}`);
  }, [me?.id, first]);
  // A join code from a link (/app#/join/<code>): kept through signing in,
  // then filled in on the Projects page.
  useEffect(() => {
    if (first !== "join") return;
    try {
      if (route.path[1]) localStorage.setItem(JOIN_KEY, decodeURIComponent(route.path[1]));
    } catch {
      /* storage blocked: they type the code instead */
    }
    if (me) go("/projects");
  }, [first, me?.id]);
  // Open to everyone; the rest needs a signed-in account.
  const open: Record<string, () => ReactNode> = {
    start: () => <StartPage route={route} />,
    "sign-in": () => <SignInPage route={route} />,
    verify: () => <VerifyPage route={route} />,
    forgot: () => <ForgotPage />,
    reset: () => <ResetPage route={route} />,
    // An invitation's link from its email: works signed in or out.
    invite: () => <InvitePage route={route} />,
  };
  let page: ReactNode;
  if (signedInAway) page = null;
  else if (first && open[first]) page = open[first]();
  else if (me === undefined)
    page = (
      <div
        className="grid min-h-[60svh] place-items-center text-dim"
        aria-busy="true"
      >
        Loading…
      </div>
    );
  else if (me === null) page = <SignInPage route={route} />
  // Approving a Godot sign-in needs an account, not a team.
  else if (first === "device") page = <DevicePage route={route} />;
  else if (!dash.state)
    page = dash.error ? (
      <div
        className="grid min-h-[60svh] place-items-center px-5 text-center text-dim"
        role="alert"
      >
        {dash.error}
      </div>
    ) : (
      <div
        className="grid min-h-[60svh] place-items-center text-dim"
        aria-busy="true"
      >
        Loading…
      </div>
    );
  else
    page = (
      <Shell active={first ?? "projects"}>
        {first === "projects" && route.path[1] ? (
          <ProjectPage id={route.path[1]} />
        ) : first === "billing" && dash.state.plan && OFFICIAL ? (
          <BillingPage />
        ) : first === "account" ? (
          <AccountPage />
        ) : (
          <ProjectsPage />
        )}
      </Shell>
    );

  return (
    <TooltipProvider>
      <OfflineBar />
      {page}
      <Toaster position="bottom-right" />
    </TooltipProvider>
  );
}

// Says so when this device loses its connection, and goes away when it's back.
function OfflineBar() {
  const [offline, setOffline] = useState(() => !navigator.onLine);
  useEffect(() => {
    const on = () => setOffline(false);
    const off = () => setOffline(true);
    addEventListener("online", on);
    addEventListener("offline", off);
    return () => {
      removeEventListener("online", on);
      removeEventListener("offline", off);
    };
  }, []);
  if (!offline) return null;
  return (
    <div
      role="status"
      className="flex items-center gap-2.5 bg-tint-rust px-4 py-2 text-[0.8125rem] text-text"
    >
      <WifiOff className="size-4 text-bad" />
      <span className="font-semibold">You're offline.</span>
      <span className="text-text/75">
        Changes here wait until you're back online.
      </span>
    </div>
  );
}

const NAV = [
  { id: "projects", label: "Projects", icon: FolderKanban },
  { id: "billing", label: PLANS_OPEN ? "Plan & billing" : "Plan", icon: CreditCard },
  { id: "account", label: "Account", icon: User },
];

function Shell({ active, children }: { active: string; children: ReactNode }) {
  const s = useAppState();
  const me = useMe();
  const online = s.presence.length;
  const usage = s.plan ? planUsage(s.plan) : null;
  // The plan page is for people who make projects.
  // A self-hosted server has no plans or billing.
  const nav = NAV.filter((n) => n.id !== "billing" || (s.plan && OFFICIAL));

  return (
    <div className="lg:grid lg:min-h-svh lg:grid-cols-[15.5rem_1fr]">
      <aside className="bg-side lg:sticky lg:top-0 lg:flex lg:h-svh lg:flex-col">
        <div className="flex items-center gap-3 px-4 py-4 lg:block lg:px-5 lg:py-5">
          <a href="/" aria-label="YHDE home">
            <Wordmark />
          </a>
          <div className="ml-auto min-w-0 text-right lg:mt-5 lg:ml-0 lg:text-left">
            <p className="truncate text-sm text-text">{s.user.name}</p>
            <p className="label-dim mt-1">
              {usage ? usage.info.name : "Invited"} · {online} online
            </p>
          </div>
        </div>
        <nav
          className="flex gap-1 overflow-x-auto px-3 pb-3 lg:grid lg:gap-0.5 lg:px-3 lg:pb-0"
          aria-label="Dashboard"
        >
          {nav.map((n) => (
            <a
              key={n.id}
              href={"#/" + n.id}
              aria-current={active === n.id ? "page" : undefined}
              className={cn(
                "flex shrink-0 items-center gap-3 rounded-xl px-3 py-2.5 text-[0.9375rem] font-medium transition-colors",
                active === n.id
                  ? "bg-card-2 text-text"
                  : "text-dim hover:bg-card/70 hover:text-text",
              )}
            >
              <n.icon
                className={cn("size-4", active === n.id ? "text-sky" : "")}
              />{" "}
              {n.label}
            </a>
          ))}
          <button
            type="button"
            className="flex shrink-0 cursor-pointer items-center gap-3 rounded-xl px-3 py-2.5 text-[0.9375rem] font-medium text-dim transition-colors hover:bg-card/70 hover:text-text lg:hidden"
            onClick={async () => {
              await auth.logout();
              location.href = "/";
            }}
          >
            <LogOut className="size-4" /> Sign out
          </button>
        </nav>
        <div className="mx-3 mt-auto mb-3 hidden gap-5 rounded-2xl bg-card p-4 lg:grid">
          {s.plan && usage ? (
            <>
              <div className="grid gap-2">
                <div className="flex items-baseline justify-between">
                  <span className="label-dim">Projects</span>
                  <span className="text-xs text-text tabular-nums">
                    {s.plan.projects}
                    {s.plan.maxProjects !== null ? ` / ${s.plan.maxProjects}` : ""}
                  </span>
                </div>
                {s.plan.maxProjects !== null ? <Meter value={s.plan.projects / s.plan.maxProjects} /> : null}
              </div>
            </>
          ) : null}
          <div className="flex items-center gap-2.5 border-t border-line pt-4">
            <Peer
              name={me?.name ?? s.user.name}
              color="var(--accent)"
              size="sm"
            />
            <span className="min-w-0 flex-1 truncate text-xs text-text">
              {me?.name ?? s.user.name}
            </span>
            <button
              type="button"
              aria-label="Sign out"
              className="cursor-pointer p-1 text-dim hover:text-sky"
              onClick={async () => {
                await auth.logout();
                location.href = "/";
              }}
            >
              <LogOut className="size-3.5" />
            </button>
          </div>
        </div>
      </aside>
      <main className="min-w-0 px-4 py-6 sm:px-6 lg:px-10 lg:py-9">
        <div
          key={location.hash.split("?")[0]}
          className="animate-in fade-in-0 slide-in-from-bottom-1 duration-300"
        >
          {s.invitations.length ? (
            <div className="mb-8">
              <InvitationList invitations={s.invitations} />
            </div>
          ) : null}
          {children}
        </div>
      </main>
    </div>
  );
}

// Page title row shared by the dashboard pages.
export function PageHead({
  title,
  sub,
  action,
  back,
}: {
  title: ReactNode;
  sub?: ReactNode;
  action?: ReactNode;
  back?: ReactNode;
}) {
  return (
    <div className="mb-8 grid gap-3">
      {back}
      <div className="flex flex-wrap items-end gap-x-6 gap-y-4">
        <div className="min-w-0">
          <h1 className="display text-[2.25rem] text-text">{title}</h1>
          {sub ? <div className="mt-1.5 text-xs text-dim">{sub}</div> : null}
        </div>
        {action ? (
          <div className="ml-auto flex flex-wrap items-center gap-2">
            {action}
          </div>
        ) : null}
      </div>
    </div>
  );
}

// Runs an API call and shows its error as a toast; true when it worked.
export async function run(
  fn: () => Promise<unknown>,
  done?: string,
): Promise<boolean> {
  return (await call(fn, done)) !== FAILED;
}

// Like run, but gives back the call's result (or FAILED).
export const FAILED = Symbol("failed");
export async function call<T>(
  fn: () => Promise<T>,
  done?: string,
): Promise<T | typeof FAILED> {
  try {
    const r = await fn();
    if (done) toast.success(done);
    return r;
  } catch (e) {
    toast.error((e as Error).message);
    return FAILED;
  }
}

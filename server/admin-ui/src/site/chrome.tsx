import { useEffect, useState } from "react";
import { ArrowRight, FolderKanban, LogOut, Menu, Megaphone, TriangleAlert, User, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Toaster } from "@/components/ui/sonner";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { auth, loadMe, useMe, type Me } from "@/app/auth";
import { cn } from "@/lib/utils";
import { Mark, Peer, Wordmark } from "@/world/parts";
import { versionLabel } from "@/world/version";
import { PLANS_OPEN } from "@/world/plans";
import { OFFICIAL, SITE, operatorName } from "@/world/site";
import { COMPANY, SOCIAL } from "./company";
import { getSiteInfo, type Announcement } from "./api";

// Addresses on the public site. On the server every page has its own path;
// the Vite dev server only has the built files, so they are mapped there.
const DEV =
  typeof location !== "undefined" && location.pathname.startsWith("/ui/");
export function href(to: string) {
  if (!DEV) return to;
  const [path, hash] = to.split("#");
  const page =
    path === "/" || path === ""
      ? "/ui/home.html"
      : path.startsWith("/app")
        ? "/ui/app.html"
        : path === "/join"
          ? "/ui/join.html"
          : `/ui/site.html#${path}`;
  return hash !== undefined ? `${page.split("#")[0]}#${hash}` : page;
}

const TEXT = {
  nav: [
    ...(OFFICIAL
      ? [
          { to: "/#features", label: "Features" },
          { to: "/#plans", label: "Pricing" },
        ]
      : []),
    { to: "/docs", label: "Docs" },
    { to: "/news", label: "News" },
    { to: "/contact", label: "Contact" },
  ],
  home: "YHDE home",
  skip: "Skip to content",
  signIn: "Sign in",
  start: OFFICIAL ? "Start your team" : "Get started",
  openMenu: "Open menu",
  closeMenu: "Close menu",
  closeAnnouncement: "Close announcement",
  tagline: !OFFICIAL
    ? `A YHDE server for real-time teamwork in Godot, run by ${operatorName()}.`
    : PLANS_OPEN
      ? "Real-time teamwork for Godot. The owner pays, the team joins free."
      : "Real-time teamwork for Godot. Free while in beta.",
  footer: [
    {
      title: "Product",
      links: [
        ...(OFFICIAL
          ? [
              { to: "/#features", label: "Features" },
              { to: "/#plans", label: "Pricing" },
            ]
          : []),
        { to: "/changelog", label: "What's new" },
        { to: "/news", label: "News" },
        { to: "/status", label: "Status" },
      ],
    },
    {
      title: "Help",
      links: [
        { to: "/docs", label: "Getting started" },
        { to: "/docs#troubleshooting", label: "Troubleshooting" },
        { to: "/join", label: "Join with a code" },
        { to: "/contact", label: "Contact" },
      ],
    },
    {
      title: "Legal",
      links: [
        { to: "/privacy", label: "Privacy & GDPR" },
        { to: "/terms", label: "Terms" },
        { to: "/cookies", label: "Cookies" },
        { to: "/security", label: "Security" },
        ...(OFFICIAL ? [{ to: "/contact#company", label: "Company info" }] : []),
      ],
    },
  ],
  vat: OFFICIAL && PLANS_OPEN ? "All prices include VAT." : "",
  trademark:
    "Godot is a trademark of the Godot Foundation. YHDE is not affiliated with it.",
};

// The announcement set on the admin page. Closing it hides that text for
// this visitor; a new text shows again.
function AnnouncementBar() {
  const t = TEXT;
  const [a, setA] = useState<Announcement | null>(null);
  const key = (x: Announcement) =>
    "yhde.announcement." + x.text.length + ":" + x.text.slice(0, 40);
  useEffect(() => {
    getSiteInfo().then((s) => {
      const x = s.announcement;
      if (!x) return;
      try {
        if (localStorage.getItem(key(x))) return;
      } catch {
        /* storage blocked: just show it */
      }
      setA(x);
    });
  }, []);
  if (!a) return null;
  const Icon = a.tone === "warning" ? TriangleAlert : Megaphone;
  const body = (
    <>
      <Icon className="size-4 shrink-0" />
      <span className="min-w-0">{a.text}</span>
      {a.link ? <ArrowRight className="size-4 shrink-0" /> : null}
    </>
  );
  return (
    <div
      className={cn(
        "relative flex items-center justify-center px-12 py-2.5 text-sm font-semibold",
        a.tone === "warning"
          ? "bg-[#4a3413] text-[#ffd79a]"
          : "bg-sky text-sky-ink",
      )}
    >
      {a.link ? (
        <a
          href={href(a.link)}
          className="inline-flex items-center gap-2 hover:underline"
        >
          {body}
        </a>
      ) : (
        <span className="inline-flex items-center gap-2">{body}</span>
      )}
      <button
        type="button"
        aria-label={t.closeAnnouncement}
        className="absolute right-3 grid size-7 cursor-pointer place-items-center rounded-full hover:bg-black/15"
        onClick={() => {
          try {
            localStorage.setItem(key(a), "1");
          } catch {
            /* not remembered; fine */
          }
          setA(null);
        }}
      >
        <X className="size-4" />
      </button>
    </div>
  );
}

// Where a signed-in person can go from the site.
const ACCOUNT_LINKS = [
  { to: "/app#/projects", label: "Projects", icon: FolderKanban },
  { to: "/app#/account", label: "Account", icon: User },
];

async function signOut() {
  await auth.logout();
  location.href = href("/");
}

// The avatar in the header once signed in: opens the dashboard pages.
function AccountMenu({ me }: { me: Me }) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label={`${me.name}: your account`}
          title={me.name || me.email}
          className="grid size-9 cursor-pointer place-items-center rounded-full bg-sky text-sm font-bold text-sky-ink ring-2 ring-transparent ring-offset-2 ring-offset-bg transition-shadow hover:ring-sky/60 focus-visible:ring-sky data-[state=open]:ring-sky"
        >
          {(me.name || me.email).trim().charAt(0).toUpperCase() || "?"}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-52">
        <DropdownMenuLabel className="grid gap-0.5">
          <span className="truncate text-text">{me.name}</span>
          <span className="truncate text-xs font-normal text-dim">{me.email}</span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {ACCOUNT_LINKS.map((l) => (
          <DropdownMenuItem key={l.to} asChild>
            <a href={href(l.to)}>
              <l.icon /> {l.label}
            </a>
          </DropdownMenuItem>
        ))}
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={signOut}>
          <LogOut /> Sign out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

export function SiteHeader() {
  const t = TEXT;
  const me = useMe();
  useEffect(() => {
    loadMe().catch(() => {});
  }, []);
  const [open, setOpen] = useState(false);
  useEffect(() => {
    const close = () => setOpen(false);
    addEventListener("hashchange", close);
    return () => removeEventListener("hashchange", close);
  }, []);
  useEffect(() => {
    document.body.style.overflow = open ? "hidden" : "";
    return () => void (document.body.style.overflow = "");
  }, [open]);

  return (
    <>
      <a
        href="#main"
        className="sr-only z-[60] rounded-full bg-sky px-4 py-2 font-semibold text-sky-ink focus:not-sr-only focus:fixed focus:top-3 focus:left-3"
      >
        {t.skip}
      </a>
      <AnnouncementBar />
      <header className="sticky top-0 z-40 bg-bg/85 backdrop-blur-md">
        <div className="mx-auto flex h-16 max-w-6xl items-center gap-8 px-5 sm:px-7">
          <a
            href={href("/")}
            aria-label={t.home}
            className="flex items-center gap-3"
          >
            <Wordmark />
          </a>
          <nav className="hidden items-center gap-7 md:flex" aria-label="Main">
            {t.nav.map((n) => (
              <a
                key={n.to}
                href={href(n.to)}
                className="text-[0.9375rem] font-medium text-dim transition-colors hover:text-text"
              >
                {n.label}
              </a>
            ))}
          </nav>
          <div className="ml-auto flex items-center gap-3">
            {me ? (
              <AccountMenu me={me} />
            ) : me === null ? (
              <>
                <a
                  href={href("/app#/sign-in")}
                  className="hidden text-[0.9375rem] font-medium text-dim transition-colors hover:text-text sm:inline"
                >
                  {t.signIn}
                </a>
                <Button asChild className="hidden sm:inline-flex">
                  <a href={href("/app#/start")}>{t.start}</a>
                </Button>
              </>
            ) : null}
            <Button
              variant="secondary"
              size="icon"
              className="md:hidden"
              aria-label={open ? t.closeMenu : t.openMenu}
              aria-expanded={open}
              onClick={() => setOpen(!open)}
            >
              {open ? <X /> : <Menu />}
            </Button>
          </div>
        </div>
      </header>

      {/* Phone menu (outside the header: its blur would trap a fixed panel) */}
      <div
        className={cn(
          "fixed inset-0 z-50 overflow-y-auto bg-bg px-5 pb-8 transition-[opacity,transform] duration-200 md:hidden",
          open
            ? "translate-y-0 opacity-100"
            : "pointer-events-none -translate-y-2 opacity-0",
        )}
        aria-hidden={!open}
      >
        <div className="flex h-16 items-center gap-3">
          <Wordmark />
          <Button
            variant="secondary"
            size="icon"
            aria-label={t.closeMenu}
            tabIndex={open ? 0 : -1}
            onClick={() => setOpen(false)}
          >
            <X />
          </Button>
        </div>
        <nav className="mt-2 grid gap-1" aria-label="Menu">
          {t.nav.map((n) => (
            <a
              key={n.to}
              href={href(n.to)}
              onClick={() => setOpen(false)}
              tabIndex={open ? 0 : -1}
              className="rounded-2xl px-4 py-3.5 text-lg font-semibold text-text hover:bg-card"
            >
              {n.label}
            </a>
          ))}
          {me ? null : (
            <a
              href={href("/app#/sign-in")}
              tabIndex={open ? 0 : -1}
              className="rounded-2xl px-4 py-3.5 text-lg font-semibold text-text hover:bg-card"
            >
              {t.signIn}
            </a>
          )}
        </nav>
        {me ? (
          <div className="mt-6 grid gap-1 rounded-2xl bg-card p-2">
            <div className="flex items-center gap-3 px-3 py-2">
              <Peer name={me.name || me.email} color="var(--accent)" />
              <span className="min-w-0">
                <span className="block truncate font-semibold text-text">{me.name}</span>
                <span className="block truncate text-xs text-dim">{me.email}</span>
              </span>
            </div>
            {ACCOUNT_LINKS.map((l) => (
              <a
                key={l.to}
                href={href(l.to)}
                tabIndex={open ? 0 : -1}
                className="flex items-center gap-3 rounded-xl px-3 py-3 font-semibold text-text hover:bg-card-2"
              >
                <l.icon className="size-4 text-dim" /> {l.label}
              </a>
            ))}
            <button
              type="button"
              tabIndex={open ? 0 : -1}
              onClick={signOut}
              className="flex cursor-pointer items-center gap-3 rounded-xl px-3 py-3 text-left font-semibold text-text hover:bg-card-2"
            >
              <LogOut className="size-4 text-dim" /> Sign out
            </button>
          </div>
        ) : (
          <Button size="lg" className="mt-6 w-full" asChild>
            <a href={href("/app#/start")} tabIndex={open ? 0 : -1}>
              {t.start}
            </a>
          </Button>
        )}
      </div>
    </>
  );
}

const SOCIAL_ICONS: Record<
  keyof typeof SOCIAL,
  { label: string; path: string }
> = {
  discord: {
    label: "Discord",
    path: "M20.32 4.37a19.8 19.8 0 00-4.89-1.52.07.07 0 00-.08.04c-.21.38-.44.86-.61 1.25a18.27 18.27 0 00-5.49 0 12.6 12.6 0 00-.62-1.25.08.08 0 00-.08-.04 19.74 19.74 0 00-4.88 1.52.07.07 0 00-.03.03C.53 9.05-.32 13.58.1 18.06a.08.08 0 00.03.06 19.9 19.9 0 005.99 3.03.08.08 0 00.08-.03c.46-.63.87-1.3 1.23-1.99a.08.08 0 00-.04-.11 13.1 13.1 0 01-1.87-.89.08.08 0 01-.01-.13l.37-.29a.07.07 0 01.08-.01c3.93 1.79 8.18 1.79 12.07 0a.07.07 0 01.08.01l.37.29a.08.08 0 01-.01.13c-.6.35-1.22.64-1.87.89a.08.08 0 00-.04.11c.36.7.77 1.36 1.22 1.99a.08.08 0 00.08.03 19.84 19.84 0 006-3.03.08.08 0 00.03-.06c.5-5.18-.84-9.67-3.55-13.66a.06.06 0 00-.03-.03zM8.02 15.33c-1.18 0-2.16-1.09-2.16-2.42 0-1.33.96-2.42 2.16-2.42 1.21 0 2.18 1.1 2.16 2.42 0 1.33-.96 2.42-2.16 2.42zm7.97 0c-1.18 0-2.15-1.09-2.15-2.42 0-1.33.95-2.42 2.15-2.42 1.21 0 2.18 1.1 2.16 2.42 0 1.33-.95 2.42-2.16 2.42z",
  },
  github: {
    label: "GitHub",
    path: "M12 .3a12 12 0 00-3.8 23.38c.6.12.83-.26.83-.57L9 21.07c-3.34.72-4.04-1.61-4.04-1.61-.55-1.39-1.34-1.76-1.34-1.76-1.09-.74.08-.73.08-.73 1.2.09 1.84 1.24 1.84 1.24 1.07 1.83 2.8 1.3 3.49 1 .1-.78.42-1.31.76-1.61-2.66-.3-5.47-1.33-5.47-5.93 0-1.31.47-2.38 1.24-3.22-.14-.3-.54-1.52.1-3.18 0 0 1-.32 3.3 1.23a11.5 11.5 0 016 0c2.28-1.55 3.29-1.23 3.29-1.23.64 1.66.24 2.88.12 3.18a4.65 4.65 0 011.23 3.22c0 4.61-2.8 5.63-5.48 5.92.42.36.81 1.1.81 2.22l-.01 3.29c0 .31.2.69.82.57A12 12 0 0012 .3",
  },
  youtube: {
    label: "YouTube",
    path: "M23.5 6.2a3 3 0 00-2.1-2.1C19.5 3.6 12 3.6 12 3.6s-7.5 0-9.4.5A3 3 0 00.5 6.2 31.4 31.4 0 000 12a31.4 31.4 0 00.5 5.8 3 3 0 002.1 2.1c1.9.5 9.4.5 9.4.5s7.5 0 9.4-.5a3 3 0 002.1-2.1A31.4 31.4 0 0024 12a31.4 31.4 0 00-.5-5.8zM9.6 15.6V8.4l6.3 3.6-6.3 3.6z",
  },
  x: {
    label: "X",
    path: "M18.9 1.2h3.7l-8 9.2L24 22.8h-7.4l-5.8-7.6-6.6 7.6H.5l8.6-9.8L0 1.2h7.6l5.2 6.9 6.1-6.9zm-1.3 19.4h2L6.5 3.3H4.3z",
  },
};

// What a community button says while that community doesn't exist yet.
const NOT_YET: Record<keyof typeof SOCIAL, string> = {
  discord: "YHDE doesn't have a Discord server yet.",
  github: "YHDE isn't on GitHub yet.",
  youtube: "YHDE doesn't have a YouTube channel yet.",
  x: "YHDE isn't on X yet.",
};

// Links to YHDE's communities (addresses in company.ts). One that doesn't
// exist yet still shows, and says so when clicked.
export function SocialLinks({ className }: { className?: string }) {
  const keys = Object.keys(SOCIAL) as (keyof typeof SOCIAL)[];
  const icon = (k: keyof typeof SOCIAL) => (
    <svg viewBox="0 0 24 24" className="size-4" fill="currentColor" aria-hidden="true">
      <path d={SOCIAL_ICONS[k].path} />
    </svg>
  );
  return (
    <div className={cn("flex items-center gap-2", className)}>
      {keys.map((k) =>
        SOCIAL[k] ? (
          <a
            key={k}
            href={SOCIAL[k]}
            target="_blank"
            rel="noreferrer noopener me"
            aria-label={SOCIAL_ICONS[k].label}
            className="grid size-9 place-items-center rounded-full bg-card-2 text-dim transition-colors hover:bg-[#2e2e34] hover:text-text"
          >
            {icon(k)}
          </a>
        ) : (
          // Not set up yet: says so instead of going nowhere.
          <button
            key={k}
            type="button"
            aria-label={`${SOCIAL_ICONS[k].label}: not available yet`}
            onClick={() =>
              toast.warning(NOT_YET[k], {
                id: "social",
                description: SOCIAL.github
                  ? "For now, follow YHDE on GitHub: it is open source."
                  : undefined,
                action: SOCIAL.github
                  ? {
                      label: "GitHub",
                      onClick: () => window.open(SOCIAL.github, "_blank", "noopener"),
                    }
                  : undefined,
              })
            }
            className="grid size-9 cursor-pointer place-items-center rounded-full bg-card-2 text-dim/60 transition-colors hover:bg-[#2e2e34] hover:text-dim"
          >
            {icon(k)}
          </button>
        ),
      )}
    </div>
  );
}

// A short note about cookies, shown once. YHDE only sets the cookies it
// needs to work (signing in), which need no consent, so there is nothing to
// accept or decline: this just says so. If tracking or analytics are ever
// added, this must become a real choice that blocks them until accepted.
const COOKIE_KEY = "yhde.cookies-seen";

export function CookieNotice() {
  const [open, setOpen] = useState(() => {
    try {
      return localStorage.getItem(COOKIE_KEY) !== "1";
    } catch {
      return true;
    }
  });
  if (!open) return null;
  const close = () => {
    try {
      localStorage.setItem(COOKIE_KEY, "1");
    } catch {
      /* shown again next time */
    }
    setOpen(false);
  };
  return (
    <section
      aria-label="Cookies"
      className="fixed inset-x-4 bottom-4 z-50 flex flex-wrap items-center gap-x-5 gap-y-3 rounded-2xl border border-line bg-card-2 p-5 shadow-[0_12px_40px_rgb(0_0_0/0.45)] sm:left-auto sm:max-w-md"
    >
      <p className="min-w-0 flex-1 basis-60 text-sm leading-relaxed text-text/85">
        We only use the cookies needed to keep you signed in. No tracking,
        analytics or ads.{" "}
        <a
          href={href("/cookies")}
          className="font-semibold text-sky hover:underline"
        >
          Read more
        </a>
      </p>
      <Button size="sm" onClick={close}>
        Got it
      </Button>
    </section>
  );
}

// The credit on every self-hosted server's pages.
export function PoweredBy({ className }: { className?: string }) {
  return (
    <a
      href={SITE.poweredBy}
      className={cn("inline-flex items-center gap-2 text-sm font-semibold text-dim transition-colors hover:text-text", className)}
    >
      <Mark className="size-4" /> Powered by YHDE
    </a>
  );
}

export function SiteFooter() {
  const t = TEXT;
  return (
    <>
      <CookieNotice />
      <Toaster position="bottom-center" />
      <footer className="mt-8 border-t border-line">
        <div className="mx-auto grid max-w-6xl gap-10 px-5 py-14 sm:px-7 md:grid-cols-[1.4fr_repeat(3,1fr)]">
          <div className="grid content-start gap-4">
            <Wordmark />
            <p className="max-w-[28ch] text-sm leading-relaxed text-dim">
              {t.tagline}
            </p>
            {OFFICIAL ? <SocialLinks /> : <PoweredBy />}
          </div>
          {t.footer.map((col) => (
            <div key={col.title}>
              <p className="text-sm font-semibold text-text">{col.title}</p>
              <ul className="mt-4 grid gap-2.5">
                {col.links.map((l) => (
                  <li key={l.to}>
                    <a
                      href={href(l.to)}
                      className="text-sm text-dim transition-colors hover:text-text"
                    >
                      {l.label}
                    </a>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
        <div className="border-t border-line">
          <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-x-6 gap-y-2 px-5 py-6 text-xs text-dim sm:px-7">
            <span className="inline-flex items-center gap-2">
              <Mark className="size-5" /> © {new Date().getFullYear()}{" "}
              {OFFICIAL
                ? COMPANY.businessId
                  ? `${COMPANY.name} · ${COMPANY.businessId}`
                  : COMPANY.name
                : operatorName()}
            </span>
            <span>{versionLabel()}</span>
            {t.vat ? <span>{t.vat}</span> : null}
            <span className="lg:ml-auto">{t.trademark}</span>
          </div>
        </div>
      </footer>
    </>
  );
}

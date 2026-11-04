"use client";

import type { ReactNode } from "react";
import { usePathname } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Activity, AppWindow, House, KeyRound, LogOut, ShieldCheck, Users, UsersRound } from "lucide-react";
import { cn } from "./ui/cn";

// Pages for someone who isn't (or is no longer) signed in: no menu, which would only lead back to sign-in.
const publicPages = ["/signed-out", "/not-admin", "/sign-in-failed"];

/** The menu on the left and the page next to it; public pages get a centred card instead. */
export function AppShell({ showTestToken, children }: { showTestToken: boolean; children: ReactNode }) {
  const pathname = usePathname();
  const t = useTranslations("App");

  if (publicPages.includes(pathname)) {
    return (
      <main className="flex min-h-screen items-center justify-center p-4">
        <div className="w-full max-w-md">
          <Brand className="mb-6 justify-center text-slate-900" />
          <div className="rounded-lg border border-slate-200 bg-white p-8 shadow-sm">{children}</div>
        </div>
      </main>
    );
  }

  const links = [
    { href: "/", label: t("home"), icon: House },
    { href: "/applications", label: t("applications"), icon: AppWindow },
    { href: "/users", label: t("users"), icon: Users },
    { href: "/groups", label: t("groups"), icon: UsersRound },
    { href: "/sessions", label: t("sessions"), icon: Activity },
    // Development/Testing only, like the page itself.
    ...(showTestToken ? [{ href: "/test-token", label: t("testToken"), icon: KeyRound }] : []),
  ];

  const isCurrent = (href: string) => href === "/" ? pathname === "/" : pathname === href || pathname.startsWith(`${href}/`);

  return (
    <div className="min-h-screen lg:flex">
      <aside className="flex flex-col bg-slate-900 text-slate-300 lg:fixed lg:inset-y-0 lg:w-60">
        <Brand className="px-5 py-4 text-white" />
        <nav aria-label={t("menu")} className="flex gap-1 overflow-x-auto px-3 pb-3 lg:flex-1 lg:flex-col lg:overflow-visible">
          {links.map(({ href, label, icon: Icon }) => (
            <a
              key={href}
              href={href}
              aria-current={isCurrent(href) ? "page" : undefined}
              className={cn(
                "flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium whitespace-nowrap no-underline transition-colors",
                isCurrent(href) ? "bg-slate-800 text-white" : "text-slate-300 hover:bg-slate-800/60 hover:text-white",
              )}
            >
              <Icon className="size-4" aria-hidden />
              {label}
            </a>
          ))}
        </nav>
        <div className="hidden border-t border-slate-800 p-3 lg:block">
          <SignOutButton />
        </div>
      </aside>
      <div className="flex min-w-0 flex-1 flex-col lg:pl-60">
        <header className="flex items-center justify-end gap-4 border-b border-slate-200 bg-white px-6 py-3">
          <LanguageSwitch />
          <div className="lg:hidden"><SignOutButton light /></div>
        </header>
        <main className="mx-auto w-full max-w-6xl flex-1 p-6">{children}</main>
      </div>
    </div>
  );
}

function Brand({ className }: { className?: string }) {
  const t = useTranslations("App");
  return (
    <div className={cn("flex items-center gap-2 text-lg font-semibold", className)}>
      <ShieldCheck className="size-6 text-brand-500" aria-hidden />
      {t("title")}
    </div>
  );
}

function LanguageSwitch() {
  const t = useTranslations("App");
  const locale = useLocale();
  return (
    <nav aria-label={t("language")} className="flex rounded-md border border-slate-200 p-0.5 text-xs font-medium">
      {(["tr", "en"] as const).map(language => (
        <a
          key={language}
          href={`/language?to=${language}`}
          aria-current={locale === language ? "true" : undefined}
          className={cn(
            "rounded px-2 py-1 uppercase no-underline",
            locale === language ? "bg-slate-900 text-white hover:text-white" : "text-slate-500 hover:text-slate-900",
          )}
        >
          {language}
        </a>
      ))}
    </nav>
  );
}

/** A form post, not a link: the UI refuses a sign-out that doesn't come from its own pages. */
function SignOutButton({ light }: { light?: boolean }) {
  const t = useTranslations("App");
  return (
    <form method="post" action="/logout">
      <button
        type="submit"
        className={cn(
          "flex w-full cursor-pointer items-center gap-3 rounded-md px-3 py-2 text-sm font-medium",
          light ? "text-slate-600 hover:bg-slate-100" : "text-slate-300 hover:bg-slate-800/60 hover:text-white",
        )}
      >
        <LogOut className="size-4" aria-hidden />
        {t("signOut")}
      </button>
    </form>
  );
}

import type { ComponentType } from "react";
import { getTranslations } from "next-intl/server";
import { Activity, AppWindow, Users, UsersRound } from "lucide-react";
import { PageHeader } from "@/components/ui/page-header";
import { listApplications } from "@/lib/applications";
import { listGroups } from "@/lib/groups";
import { listOpenSessions } from "@/lib/open-sessions";
import { adminApi } from "@/lib/server";
import { listUsers } from "@/lib/users";

export const dynamic = "force-dynamic";

export default async function HomePage() {
  const t = await getTranslations("Home");
  const me = (await (await adminApi("me")).json()) as { userName: string; displayName: string };
  const [applications, users, groups, sessions] = await Promise.all([
    listApplications(adminApi), listUsers(adminApi), listGroups(adminApi), listOpenSessions(adminApi, {}),
  ]);

  return (
    <>
      <PageHeader title={t("title")} description={t("signedInAs", { displayName: me.displayName, userName: me.userName })} />
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <Tile href="/applications" icon={AppWindow} label={t("applications")} count={applications.length}
          detail={t("active", { count: applications.filter(a => a.isActive).length })} />
        <Tile href="/users" icon={Users} label={t("users")} count={users.length}
          detail={t("active", { count: users.filter(u => u.isActive).length })} />
        <Tile href="/groups" icon={UsersRound} label={t("groups")} count={groups.length} detail={t("groupsDetail")} />
        <Tile href="/sessions" icon={Activity} label={t("sessions")} count={sessions.length} detail={t("sessionsDetail")} />
      </div>
    </>
  );
}

function Tile({ href, icon: Icon, label, count, detail }: {
  href: string; icon: ComponentType<{ className?: string }>; label: string; count: number; detail: string;
}) {
  return (
    <a href={href} className="group rounded-lg border border-slate-200 bg-white p-5 text-slate-900 no-underline shadow-sm transition hover:border-brand-200 hover:shadow">
      <div className="flex items-center justify-between">
        <span className="text-sm font-medium text-slate-500 group-hover:text-slate-700">{label}</span>
        <span className="rounded-md bg-brand-50 p-2 text-brand-700"><Icon className="size-5" /></span>
      </div>
      <p className="mt-3 text-3xl font-semibold tracking-tight">{count}</p>
      <p className="mt-1 text-xs text-slate-500">{detail}</p>
    </a>
  );
}
